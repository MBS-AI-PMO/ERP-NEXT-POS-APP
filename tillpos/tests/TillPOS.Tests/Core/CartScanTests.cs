using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Sales;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.Core.ScaleLabelTests;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class CartScanTests
{
    private static readonly SalesTaxTemplate Vat = new("UAE VAT 5% - AAML", [new TaxRow(1, "VAT 5% - AAML", "VAT 5%", 5m, true)], null);
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();

    private Cart NewCart() => new(new SaleContext(catalog, new MoneySettings(3, RoundingMethod.Bankers, 0.25m),
        "Standard Selling", "Stores - AAML", null, Vat, () => new DateOnly(2026, 10, 6)));

    private void Item(string code, string name, string stockUom, string price, params (string Barcode, string? Uom)[] barcodes)
    {
        catalog.Items.Add(new Item(code, name, "Food", null, stockUom, false, true));
        catalog.Prices.Add(new ItemPrice("P-" + code, code, stockUom, M(price), null, null));
        foreach (var (barcode, uom) in barcodes) catalog.Barcodes.Add(new ItemBarcode(barcode, code, uom));
    }

    [Fact]
    public void Cucumber_label_resolves_by_the_six_digit_code_and_uses_the_weight()
    {
        Item("000089", "CUCUMBER/KIYAR", "Kg", "3.50", ("000089", "Kg"));
        var cart = NewCart();

        var result = cart.AddBarcode("2000089007400");

        Assert.Equal(AddOutcome.Added, result.Outcome);
        var line = Assert.Single(cart.Lines);
        Assert.Equal("000089", line.Item.ItemCode);
        Assert.Equal(M("0.740"), line.Qty);
        Assert.True(line.FromScaleLabel);
        Assert.Equal("2000089007400", line.Barcode);
        Assert.Equal(M("2.590"), cart.Totals().GrandTotal);
    }

    [Fact]
    public void Curry_leaves_label_on_a_piece_item_reads_the_value_as_a_count()
    {
        Item("000060", "CURRY LEAVES/PCS", "PCS", "1.50", ("000060", "PCS"), ("2000060", "PCS"));
        var cart = NewCart();

        cart.AddBarcode("2000060000017");
        cart.AddBarcode(Ean("200006000003"));

        Assert.Equal(new[] { 1m, 3m }, cart.Lines.Select(l => l.Qty));
        Assert.All(cart.Lines, l => Assert.True(l.FromScaleLabel));
        Assert.Equal(M("6.000"), cart.Totals().GrandTotal);
    }

    [Fact]
    public void Label_resolves_by_the_seven_digit_barcode()
    {
        Item("000088", "TOMATO", "Kg", "4.00", ("2000088", "Kg"));
        var cart = NewCart();

        cart.AddBarcode(Ean("200008800450"));

        Assert.Equal(M("0.450"), Assert.Single(cart.Lines).Qty);
    }

    [Fact]
    public void Seven_digit_key_wins_over_six_digit_key()
    {
        Item("SEVEN", "Seven", "Kg", "1.00", ("2000077", "Kg"));
        Item("SIX", "Six", "Kg", "1.00", ("000077", "Kg"));
        var cart = NewCart();

        cart.AddBarcode(Ean("200007700100"));

        Assert.Equal("SEVEN", Assert.Single(cart.Lines).Item.ItemCode);
    }

    [Fact]
    public void A_13_digit_barcode_stored_on_an_item_is_a_normal_scan()
    {
        var fixedCode = Ean("212345678901");
        Item("FIXED", "Fixed pack", "PCS", "5.00", (fixedCode, null));
        var cart = NewCart();

        cart.AddBarcode(fixedCode);

        var line = Assert.Single(cart.Lines);
        Assert.Equal(1m, line.Qty);
        Assert.False(line.FromScaleLabel);
    }

    [Fact]
    public void Two_labels_of_the_same_item_stay_separate_lines()
    {
        Item("000089", "CUCUMBER/KIYAR", "Kg", "3.50", ("000089", "Kg"));
        var cart = NewCart();

        cart.AddBarcode("2000089007400");
        cart.AddBarcode(Ean("200008900500"));

        Assert.Equal(2, cart.Lines.Count);
    }

    [Fact]
    public void Label_with_a_wrong_check_digit_is_a_misscan()
    {
        Item("000089", "CUCUMBER/KIYAR", "Kg", "3.50", ("000089", "Kg"));
        var cart = NewCart();

        Assert.Equal(AddOutcome.InvalidScaleLabel, cart.AddBarcode("2000089007401").Outcome);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void Label_for_an_unknown_item_is_an_unknown_barcode() =>
        Assert.Equal(AddOutcome.UnknownBarcode, NewCart().AddBarcode("2000089007400").Outcome);

    [Fact]
    public void Barcode_unit_missing_on_the_item_is_sold_in_the_stock_unit()
    {
        Item("003497", "YELLOW DAMIATY CHEESE", "PCS", "16.00", ("003497", "Kg"));
        var cart = NewCart();

        Assert.Equal(AddOutcome.Added, cart.AddBarcode("003497").Outcome);

        var line = Assert.Single(cart.Lines);
        Assert.Equal("PCS", line.Uom);
        Assert.Equal("Kg", line.UomFallbackFrom);
        Assert.Equal(M("16.000"), line.Rate);
    }

    [Fact]
    public void Weighed_lines_cannot_be_incremented()
    {
        Item("000089", "CUCUMBER/KIYAR", "Kg", "3.50", ("000089", "Kg"));
        var cart = NewCart();
        var id = cart.AddBarcode("2000089007400").Line!.Id;

        Assert.Throws<InvalidOperationException>(() => cart.Increment(id));
        Assert.Throws<InvalidOperationException>(() => cart.Decrement(id));
        Assert.Equal(M("0.740"), cart.Lines[0].Qty);
    }
}
