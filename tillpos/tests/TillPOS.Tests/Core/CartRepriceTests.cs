using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Sales;
using TillPOS.Core.Tax;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

/// <summary>Re-pricing the open bill after new prices arrive from ERPNext (weighed items whose price changes during the day).</summary>
public class CartRepriceTests
{
    private static readonly SalesTaxTemplate Vat = new("UAE VAT 5% - AAML", [new TaxRow(1, "VAT 5% - AAML", "VAT 5%", 5m, true)], null);
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();

    public CartRepriceTests()
    {
        catalog.Items.Add(new Item("MILK", "Full Cream Milk 1L", "Dairy", null, "PCS", false, true));
        catalog.Prices.Add(new ItemPrice("P-MILK", "MILK", "PCS", M("6.79"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("111", "MILK", null));
        catalog.Items.Add(new Item("000089", "TOMATO", "Food", null, "Kg", false, true));
        catalog.Prices.Add(new ItemPrice("P-TOM", "000089", "Kg", M("4.50"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("000089", "000089", "Kg"));
    }

    private Cart NewCart() => new(new SaleContext(catalog, new MoneySettings(3, RoundingMethod.Bankers, 0.25m), "Standard Selling",
        "Stores - AAML", null, Vat, () => new DateOnly(2026, 10, 10)));

    private void SetPrice(string name, string itemCode, string uom, string rate)
    {
        catalog.Prices.RemoveAll(p => p.Name == name);
        catalog.Prices.Add(new ItemPrice(name, itemCode, uom, M(rate), null, null));
    }

    [Fact]
    public void A_weighed_line_takes_the_new_price_and_keeps_its_weight()
    {
        var cart = NewCart();
        cart.AddBarcode("2000089007400");                       // scale label: 0.740 Kg
        SetPrice("P-TOM", "000089", "Kg", "3.75");

        var changes = cart.Reprice();

        var change = Assert.Single(changes);
        Assert.Equal(("TOMATO", M("4.50"), M("3.75"), "Kg"), (change.ItemName, change.OldRate, change.NewRate, change.Uom));
        var line = Assert.Single(cart.Lines);
        Assert.Equal(M("3.75"), line.Rate);
        Assert.Equal(M("3.75"), line.PriceListRate);
        Assert.Equal(M("0.740"), line.Qty);
        Assert.True(line.FromScaleLabel);
        Assert.Equal(M("2.775"), cart.Totals().GrandTotal);
    }

    [Fact]
    public void Lines_whose_price_did_not_change_are_left_alone()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        cart.AddBarcode("2000089007400");
        var ids = cart.Lines.Select(l => l.Id).ToList();
        SetPrice("P-MILK", "MILK", "PCS", "7.25");

        var change = Assert.Single(cart.Reprice());

        Assert.Equal("Full Cream Milk 1L", change.ItemName);
        Assert.Equal(ids, cart.Lines.Select(l => l.Id));                 // same lines (selection and order kept)
        Assert.Equal(M("4.50"), cart.Lines[1].Rate);
        Assert.Empty(cart.Reprice());                                      // nothing new the second time
    }

    [Fact]
    public void A_line_whose_price_is_gone_keeps_the_price_it_was_scanned_at()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        catalog.Prices.Clear();

        Assert.Empty(cart.Reprice());
        Assert.Equal(M("6.79"), Assert.Single(cart.Lines).Rate);
    }

    [Fact]
    public void An_empty_bill_has_nothing_to_reprice() => Assert.Empty(NewCart().Reprice());
}
