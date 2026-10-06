using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class ReturnBuilderTests
{
    private static readonly SalesTaxTemplate Vat = new("UAE VAT 5% - AAML", [new TaxRow(1, "VAT 5% - AAML", "VAT 5%", 5m, true)], null);
    private static readonly MoneySettings Money = new(3, RoundingMethod.Bankers, 0.25m);
    private static readonly TenderModes Modes = new("Cash Counter 2", "Credit Card");
    private static readonly DateTimeOffset At = new(2026, 10, 6, 16, 0, 0, TimeSpan.FromHours(4));
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();
    private readonly InMemoryReceiptStore store = new();
    private readonly SaleContext ctx;

    public ReturnBuilderTests()
    {
        catalog.Items.Add(new Item("MILK", "Full Cream Milk 1L", "Dairy", null, "PCS", false, true));
        catalog.Prices.Add(new ItemPrice("P-MILK", "MILK", "PCS", M("6.79"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("111", "MILK", null));
        catalog.Items.Add(new Item("TV", "Television", "Household", null, "PCS", false, true));
        catalog.Prices.Add(new ItemPrice("P-TV", "TV", "PCS", M("899"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("999", "TV", null));
        ctx = new SaleContext(catalog, Money, "Standard Selling", "Stores - AAML", null, Vat, () => new DateOnly(2026, 10, 6));
    }

    private Receipt SellMilk(int count)
    {
        var cart = new Cart(ctx);
        for (var i = 0; i < count; i++) cart.AddBarcode("111");
        var plan = new PaymentCalculator(Money).Plan(cart.Totals().GrandTotal, Tender.Card());
        return new SaleRecorder(store, 2, Modes, () => At.AddHours(-1)).CompleteSale(cart, plan, "cashier", "S1");
    }

    private ReturnBuilder Builder() => new(store, ctx, 2, Modes, () => At);

    [Fact]
    public void Returns_part_of_a_line_with_a_rounded_cash_refund()
    {
        var sale = SellMilk(2);

        var ret = Builder().Build(sale, [new ReturnLineRequest(1, 1m)], TenderKind.Cash, "cashier", "S2", null);

        Assert.Equal(ReceiptKind.Return, ret.Kind);
        Assert.Equal(sale.ClientId, ret.ReturnAgainst);
        Assert.Equal(-1m, Assert.Single(ret.Lines).Qty);
        Assert.Equal(M("-6.790"), ret.GrandTotal);
        Assert.Equal(M("-6.750"), ret.RoundedTotal);              // -6.79 % 0.25 → 0.21 > .125 → toward +∞ … ERPNext rule
        Assert.Equal(new[] { new ReceiptPayment("Cash Counter 2", M("-6.750")) }, ret.Payments);
        Assert.Equal(1m, Builder().Returnable(sale, 1));
    }

    [Fact]
    public void Second_return_can_only_take_what_is_left()
    {
        var sale = SellMilk(2);
        Builder().Build(sale, [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", null);

        Assert.Throws<InvalidOperationException>(() =>
            Builder().Build(sale, [new ReturnLineRequest(1, 2m)], TenderKind.Card, "c", "S2", null));
        Builder().Build(sale, [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", null);
        Assert.Equal(0m, Builder().Returnable(sale, 1));
    }

    [Fact]
    public void Card_refund_is_exact()
    {
        var ret = Builder().Build(SellMilk(1), [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", null);
        Assert.False(ret.UsesErpRoundedTotal);
        Assert.Equal(new[] { new ReceiptPayment("Credit Card", M("-6.790")) }, ret.Payments);
    }

    [Fact]
    public void Refund_above_the_limit_needs_a_supervisor()
    {
        var cart = new Cart(ctx);
        cart.AddBarcode("999");
        var sale = new SaleRecorder(store, 2, Modes, () => At).CompleteSale(cart,
            new PaymentCalculator(Money).Plan(cart.Totals().GrandTotal, Tender.Card()), "c", "S1");

        Assert.Throws<ApprovalRequiredException>(() =>
            Builder().Build(sale, [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", null));
        var ret = Builder().Build(sale, [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", "SUP-1");
        Assert.Equal("SUP-1", ret.ApprovedBy);
    }

    [Fact]
    public void Return_without_receipt_always_needs_a_supervisor()
    {
        var cart = new Cart(ctx);
        cart.AddBarcode("111");

        Assert.Throws<ApprovalRequiredException>(() => Builder().BuildWithoutReceipt(cart, TenderKind.Cash, "c", "S2", null));
        Assert.Single(cart.Lines);

        var ret = Builder().BuildWithoutReceipt(cart, TenderKind.Cash, "c", "S2", "SUP-1");
        Assert.Null(ret.ReturnAgainst);
        Assert.Equal(-1m, ret.Lines[0].Qty);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void Invalid_requests_are_refused()
    {
        var sale = SellMilk(1);
        Assert.Throws<ArgumentException>(() => Builder().Build(sale, [], TenderKind.Card, "c", "S2", null));
        Assert.Throws<ArgumentException>(() => Builder().Build(sale, [new ReturnLineRequest(1, 0m)], TenderKind.Card, "c", "S2", null));
        Assert.Throws<ArgumentException>(() => Builder().Build(sale, [new ReturnLineRequest(9, 1m)], TenderKind.Card, "c", "S2", null));
        Assert.Throws<ArgumentException>(() =>
            Builder().Build(sale, [new ReturnLineRequest(1, M("0.5")), new ReturnLineRequest(1, M("0.5"))], TenderKind.Card, "c", "S2", null));
    }

    [Fact]
    public void A_return_cannot_be_returned()
    {
        var ret = Builder().Build(SellMilk(1), [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", null);
        Assert.Throws<InvalidOperationException>(() => Builder().Build(ret, [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", null));
    }
}
