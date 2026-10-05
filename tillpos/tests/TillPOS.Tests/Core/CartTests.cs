using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Pricing;
using TillPOS.Core.Sales;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class CartTests
{
    private static readonly SalesTaxTemplate Vat = new("UAE VAT 5%", [new TaxRow(1, "VAT 5% - S", "VAT 5%", 5m, true)], null);
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();

    private Cart NewCart() => new(new SaleContext(catalog, new MoneySettings(), "Retail", "Stores - S", null, Vat, () => new DateOnly(2026, 10, 5)));

    private void AddItem(string code, string name, string group, string price, string barcode, string? pctOff = null)
    {
        catalog.Items.Add(new Item(code, name, group, null, "Nos", false, true));
        catalog.Barcodes.Add(new ItemBarcode(barcode, code, null));
        catalog.Prices.Add(new ItemPrice("P-" + code, code, "Nos", M(price), null, null));
        if (pctOff is not null)
            catalog.Rules.Add(new PricingRule("R-" + code, RuleApplyOn.ItemCode, [code], RuleKind.DiscountPercentage, M(pctOff), 0, null, null, null, null, null));
    }

    [Fact]
    public void Scanning_a_known_barcode_adds_a_priced_line_with_offer()
    {
        AddItem("RICE5", "Basmati Rice 5kg", "Rice", "2150", "8901234500024", "10");
        var cart = NewCart();

        var result = cart.AddBarcode("8901234500024");

        Assert.Equal(AddOutcome.Added, result.Outcome);
        var line = Assert.Single(cart.Lines);
        Assert.Equal(M("2150"), line.PriceListRate);
        Assert.Equal(M("1935.00"), line.Rate);
        Assert.Equal("10% OFF", line.Rule!.Label);
    }

    [Fact]
    public void Price_list_rate_is_rounded_to_currency_precision()
    {
        catalog.Items.Add(new Item("HALF", "Half pack", "Food", null, "Nos", false, true));
        catalog.Uoms.Add(new ItemUom("HALF", "Half", M("0.5")));
        catalog.Barcodes.Add(new ItemBarcode("555", "HALF", "Half"));
        catalog.Prices.Add(new ItemPrice("P-HALF", "HALF", "Nos", M("7.25"), null, null));
        var cart = NewCart();

        cart.AddBarcode("555");

        var line = Assert.Single(cart.Lines);
        Assert.Equal(M("3.62"), line.PriceListRate);
        Assert.Equal(M("3.62"), line.Rate);
    }

    [Fact]
    public void Scanning_the_same_item_twice_increases_quantity()
    {
        AddItem("MILK", "Full Cream Milk 1L", "Dairy", "290", "111");
        var cart = NewCart();
        cart.AddBarcode("111");
        cart.AddBarcode(" 111 ");
        Assert.Equal(2m, Assert.Single(cart.Lines).Qty);
    }

    [Fact]
    public void Unknown_barcode_adds_nothing()
    {
        var cart = NewCart();
        Assert.Equal(AddOutcome.UnknownBarcode, cart.AddBarcode("999").Outcome);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void Disabled_item_cannot_be_sold()
    {
        catalog.Items.Add(new Item("OLD", "Old item", "Food", null, "Nos", true, true));
        catalog.Barcodes.Add(new ItemBarcode("222", "OLD", null));
        var cart = NewCart();
        Assert.Equal(AddOutcome.ItemNotSellable, cart.AddBarcode("222").Outcome);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void Item_without_price_cannot_be_sold()
    {
        catalog.Items.Add(new Item("NOPRICE", "No price", "Food", null, "Nos", false, true));
        catalog.Barcodes.Add(new ItemBarcode("333", "NOPRICE", null));
        var cart = NewCart();
        Assert.Equal(AddOutcome.NoPrice, cart.AddBarcode("333").Outcome);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void Item_with_unknown_item_tax_template_adds_nothing()
    {
        AddItem("SPECIAL", "Special item", "Food", "10", "555");
        catalog.ItemTaxRows["SPECIAL"] = [new ItemTaxAssignment("Missing T", null, null, 1)];
        var cart = NewCart();

        Assert.Equal(AddOutcome.UnsupportedTax, cart.AddBarcode("555").Outcome);
        Assert.Empty(cart.Lines);
        Assert.Equal(0m, cart.Totals().GrandTotal);
    }

    [Fact]
    public void Carton_barcode_uses_its_uom_and_conversion_factor()
    {
        AddItem("WATER", "Water 500ml", "Food", "2.00", "444");
        catalog.Uoms.Add(new ItemUom("WATER", "Box", 12m));
        catalog.Barcodes.Add(new ItemBarcode("445", "WATER", "Box"));
        var cart = NewCart();

        cart.AddBarcode("445");

        var line = Assert.Single(cart.Lines);
        Assert.Equal("Box", line.Uom);
        Assert.Equal(12m, line.ConversionFactor);
        Assert.Equal(M("24.00"), line.Rate);
    }

    [Fact]
    public void Quantity_changes_and_removal()
    {
        AddItem("MILK", "Full Cream Milk 1L", "Dairy", "290", "111");
        var cart = NewCart();
        var id = cart.AddBarcode("111").Line!.Id;

        cart.Increment(id);
        Assert.Equal(2m, cart.Lines[0].Qty);
        cart.Decrement(id);
        cart.Decrement(id);
        Assert.Equal(1m, cart.Lines[0].Qty);
        cart.SetQty(id, 6m);
        Assert.Equal(6m, cart.Lines[0].Qty);
        Assert.Throws<ArgumentOutOfRangeException>(() => cart.SetQty(id, 0m));
        cart.Remove(id);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void Mockup_basket_totals_and_savings()
    {
        AddItem("MILK", "Full Cream Milk 1L", "Dairy", "290", "1");
        AddItem("RICE5", "Basmati Rice 5kg", "Rice", "2150", "2", "10");
        AddItem("BREAD", "White Bread Large", "Food", "180", "3");
        AddItem("EGGS", "Eggs Tray (12)", "Food", "420", "4");
        AddItem("OIL", "Cooking Oil 1L Pouch", "Food", "610", "5", "5");
        AddItem("KETCHUP", "Tomato Ketchup 800g", "Food", "395", "6");
        AddItem("DISH", "Dishwash Liquid 500ml", "Household", "340", "7", "15");
        AddItem("SUGAR", "Sugar 1kg", "Food", "160", "8");
        var cart = NewCart();
        foreach (var b in new[] { "1", "1", "2", "3", "4", "5", "5", "6", "7", "8", "8", "8" }) cart.AddBarcode(b);

        var t = cart.Totals();

        Assert.Equal(8, cart.Lines.Count);
        Assert.Equal(M("5438.00"), t.GrandTotal);
        Assert.Equal(M("258.95"), t.TotalTaxes);
        Assert.Equal(M("327.00"), cart.DiscountSaved());
    }
}
