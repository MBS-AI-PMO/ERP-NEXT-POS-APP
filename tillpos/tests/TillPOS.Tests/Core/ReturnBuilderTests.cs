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
        catalog.Items.Add(new Item("000089", "CUCUMBER/KIYAR", "Food", null, "Kg", false, true));
        catalog.Prices.Add(new ItemPrice("P-CUC", "000089", "Kg", M("3.50"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("000089", "000089", "Kg"));
        ctx = new SaleContext(catalog, Money, "Standard Selling", "Stores - AAML", null, Vat, () => new DateOnly(2026, 10, 6));
    }

    private Receipt SellMilk(int count, int daysAgo = 0) => Sell("111", count, daysAgo);

    private Receipt Sell(string barcode, int count, int daysAgo = 0)
    {
        var cart = new Cart(ctx);
        for (var i = 0; i < count; i++) cart.AddBarcode(barcode);
        var plan = new PaymentCalculator(Money).Plan(cart.Totals().GrandTotal, Tender.Card());
        return new SaleRecorder(store, 2, Modes, () => At.AddHours(-1).AddDays(-daysAgo)).CompleteSale(cart, plan, "cashier", "S1");
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

    [Fact]
    public void Splitting_a_refund_into_small_returns_still_needs_a_supervisor()
    {
        var sale = SellMilk(10);                                   // 10 × 6.79 = 67.90
        Builder().Build(sale, [new ReturnLineRequest(1, 7m)], TenderKind.Card, "c", "S2", null);   // 47.53 — allowed
        Assert.Throws<ApprovalRequiredException>(() =>
            Builder().Build(sale, [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", null)); // total 54.32 > 50
        Builder().Build(sale, [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", "SUP-1");
    }

    [Fact]
    public void Whole_unit_lines_cannot_be_returned_in_fractions() =>
        Assert.Throws<ArgumentException>(() =>
            Builder().Build(SellMilk(2), [new ReturnLineRequest(1, M("0.5"))], TenderKind.Card, "c", "S2", null));

    [Fact]
    public void Blank_supervisor_id_is_not_an_approval()
    {
        var cart = new Cart(ctx);
        cart.AddBarcode("111");
        Assert.Throws<ApprovalRequiredException>(() => Builder().BuildWithoutReceipt(cart, TenderKind.Cash, "c", "S2", "  "));
    }

    [Fact]
    public void A_weighed_line_sold_at_whole_kilos_can_still_be_returned_in_part()
    {
        var cart = new Cart(ctx);
        cart.AddBarcode(ScaleLabelTests.Ean("200008901000"));
        var sale = new SaleRecorder(store, 2, Modes, () => At.AddHours(-1)).CompleteSale(cart,
            new PaymentCalculator(Money).Plan(cart.Totals().GrandTotal, Tender.Card()), "c", "S1");
        Assert.Equal(1m, sale.Lines[0].Qty);

        var ret = Builder().Build(sale, [new ReturnLineRequest(1, M("0.400"))], TenderKind.Card, "c", "S2", null);

        Assert.Equal(M("-0.400"), Assert.Single(ret.Lines).Qty);
    }

    [Fact]
    public void Return_stores_reason_and_cashier_user()
    {
        var ret = Builder().Build(SellMilk(1), [new ReturnLineRequest(1, 1m)], TenderKind.Card, "c", "S2", null, "Damaged", "s@x");
        Assert.Equal("Damaged", ret.Reason);
        Assert.Equal("s@x", ret.CashierUser);

        var cart = new Cart(ctx);
        cart.AddBarcode("111");
        var noReceipt = Builder().BuildWithoutReceipt(cart, TenderKind.Cash, "c", "S2", "SUP-1", "Expired", "s@x");
        Assert.Equal("Expired", noReceipt.Reason);
        Assert.Equal("s@x", noReceipt.CashierUser);
    }

    [Fact]
    public void Preview_gives_the_build_totals_and_saves_nothing()
    {
        var sale = SellMilk(3);
        var saved = store.Saved.Count;

        var preview = Builder().Preview(sale, [new ReturnLineRequest(1, 2m)]);

        Assert.Equal(saved, store.Saved.Count);
        Assert.Equal(3m, Builder().Returnable(sale, 1));
        var ret = Builder().Build(sale, [new ReturnLineRequest(1, 2m)], TenderKind.Cash, "c", "S2", null);
        Assert.Equal(ret.GrandTotal, preview.GrandTotal);
        Assert.Equal(ret.TotalTaxes, preview.TotalTaxes);
        Assert.Equal(ret.RoundedTotal, preview.RefundDue);
        Assert.Equal(ret.RoundingDifference, preview.RoundingDifference);
        Assert.Equal(M("-13.580"), preview.GrandTotal);
        Assert.Equal(M("-13.500"), preview.RefundDue);
        Assert.Empty(preview.Needs);
    }

    [Fact]
    public void Preview_refund_due_is_the_rounded_cash_amount()
    {
        var preview = Builder().Preview(SellMilk(1), [new ReturnLineRequest(1, 1m)]);
        Assert.Equal(M("-6.790"), preview.GrandTotal);
        Assert.Equal(M("-6.750"), preview.RefundDue);
        Assert.Equal(M("0.040"), preview.RoundingDifference);
    }

    [Fact]
    public void Preview_without_receipt_keeps_the_cart_and_always_needs_a_supervisor()
    {
        var cart = new Cart(ctx);
        cart.AddBarcode("111");

        var preview = Builder().PreviewWithoutReceipt(cart);

        Assert.Single(cart.Lines);
        Assert.Empty(store.Saved);
        Assert.Equal(new[] { ApprovalAction.ReturnWithoutReceipt }, preview.Needs);
        var ret = Builder().BuildWithoutReceipt(cart, TenderKind.Cash, "c", "S2", "SUP-1");
        Assert.Equal(ret.GrandTotal, preview.GrandTotal);
        Assert.Equal(ret.TotalTaxes, preview.TotalTaxes);
        Assert.Equal(ret.RoundedTotal, preview.RefundDue);
    }

    [Fact]
    public void Preview_refuses_what_build_refuses()
    {
        var sale = SellMilk(1);
        Assert.Throws<ArgumentException>(() => Builder().Preview(sale, []));
        Assert.Throws<ArgumentException>(() => Builder().Preview(sale, [new ReturnLineRequest(1, M("0.5"))]));
        Assert.Throws<InvalidOperationException>(() => Builder().Preview(sale, [new ReturnLineRequest(1, 2m)]));
        Assert.Throws<InvalidOperationException>(() => Builder().PreviewWithoutReceipt(new Cart(ctx)));
        Assert.Single(store.Saved);
    }

    [Fact]
    public void Preview_lists_every_approval_the_return_needs()
    {
        Assert.Empty(Builder().Preview(SellMilk(1), [new ReturnLineRequest(1, 1m)]).Needs);
        Assert.Equal(new[] { ApprovalAction.ReturnOverLimit },
            Builder().Preview(Sell("999", 1), [new ReturnLineRequest(1, 1m)]).Needs);
        Assert.Equal(new[] { ApprovalAction.ReturnOldReceipt },
            Builder().Preview(SellMilk(1, daysAgo: 8), [new ReturnLineRequest(1, 1m)]).Needs);
        Assert.Equal(new[] { ApprovalAction.ReturnOverLimit, ApprovalAction.ReturnOldReceipt },
            Builder().Preview(Sell("999", 1, daysAgo: 8), [new ReturnLineRequest(1, 1m)]).Needs);
    }

    [Fact]
    public void A_receipt_exactly_seven_days_old_needs_no_supervisor() =>
        Assert.Empty(Builder().Preview(SellMilk(1, daysAgo: 7), [new ReturnLineRequest(1, 1m)]).Needs);

    [Fact]
    public void The_age_limit_can_be_changed()
    {
        var builder = new ReturnBuilder(store, ctx, 2, Modes, () => At, maxAgeDays: 2);
        Assert.Equal(new[] { ApprovalAction.ReturnOldReceipt }, builder.Preview(SellMilk(1, daysAgo: 3), [new ReturnLineRequest(1, 1m)]).Needs);
    }

    [Fact]
    public void An_old_receipt_needs_a_supervisor()
    {
        var sale = SellMilk(1, daysAgo: 8);

        Assert.Throws<ApprovalRequiredException>(() =>
            Builder().Build(sale, [new ReturnLineRequest(1, 1m)], TenderKind.Cash, "c", "S2", null));
        Assert.Single(store.Saved);

        var ret = Builder().Build(sale, [new ReturnLineRequest(1, 1m)], TenderKind.Cash, "c", "S2", "SUP-1");
        Assert.Equal("SUP-1", ret.ApprovedBy);
    }

    [Fact]
    public void A_need_that_appears_after_the_approval_is_refused_and_nothing_is_saved()
    {
        var today = new DateOnly(2026, 10, 6);
        var builder = new ReturnBuilder(store, ctx with { Today = () => today }, 2, Modes, () => At);
        var sale = Sell("999", 1, daysAgo: 7);                     // 899 seven days ago: over the limit, not yet old
        IReadOnlyList<ReturnLineRequest> all = [new ReturnLineRequest(1, 1m)];
        var approved = builder.Preview(sale, all).Needs;
        Assert.Equal(new[] { ApprovalAction.ReturnOverLimit }, approved);

        today = today.AddDays(1);                                   // midnight passes while the supervisor types the PIN

        var ex = Assert.Throws<ApprovalRequiredException>(() =>
            builder.Build(sale, all, TenderKind.Cash, "c", "S2", "SUP-1", approvedNeeds: approved));
        Assert.Contains("older than 7 days", ex.Message);
        Assert.Empty(store.ReturnsAgainst(sale.ClientId));

        var ret = builder.Build(sale, all, TenderKind.Cash, "c", "S2", "SUP-1",
            approvedNeeds: [ApprovalAction.ReturnOverLimit, ApprovalAction.ReturnOldReceipt]);
        Assert.Equal("SUP-1", ret.ApprovedBy);
    }

    [Fact]
    public void Approved_needs_that_cover_the_return_or_are_not_given_build_as_before()
    {
        var sale = Sell("999", 1);
        Builder().Build(sale, [new ReturnLineRequest(1, 1m)], TenderKind.Cash, "c", "S2", "SUP-1",
            approvedNeeds: [ApprovalAction.ReturnOverLimit]);

        var cart = new Cart(ctx);
        cart.AddBarcode("111");
        Assert.Throws<ApprovalRequiredException>(() =>
            Builder().BuildWithoutReceipt(cart, TenderKind.Cash, "c", "S2", "SUP-1", approvedNeeds: []));
        Assert.Single(cart.Lines);                                  // refused before anything was saved or cleared
        Builder().BuildWithoutReceipt(cart, TenderKind.Cash, "c", "S2", "SUP-1");
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void Returns_store_the_cashier_name()
    {
        var ret = Builder().Build(SellMilk(1), [new ReturnLineRequest(1, 1m)], TenderKind.Cash, "c", "S2", null, "Damaged", "s@x",
            "Simran K");
        Assert.Equal("Simran K", ret.CashierName);

        var cart = new Cart(ctx);
        cart.AddBarcode("111");
        var noReceipt = Builder().BuildWithoutReceipt(cart, TenderKind.Cash, "c", "S2", "SUP-1", "Expired", "s@x", "Simran K");
        Assert.Equal("Simran K", noReceipt.CashierName);
    }

    // ---- Bills and returns from other tills (downloaded from ERPNext) ----

    private sealed class OtherTills(params Receipt[] returns) : IOtherTillReturns
    {
        public IReadOnlyList<Receipt> ReturnsAgainst(Receipt original) => returns.Where(r => r.ReturnAgainst == original.ClientId).ToList();
    }

    /// <summary>A return taken on another till against <paramref name="against"/>: <paramref name="qty"/> of line 1 (milk).</summary>
    private static Receipt OtherTillReturn(string name, string against, decimal qty) => new(
        name, ReceiptKind.Return, against, "", "", At.AddHours(-2),
        [new ReceiptLine(1, "MILK", "Full Cream Milk 1L", "111", "PCS", 1m, -qty, M("6.79"), M("6.79"), -qty * M("6.79"), null, null, false, null)],
        -qty * M("6.79"), 0m, 0m, -qty * M("6.79"), false, 0m, 0m, [], 0m, 0m, null);

    /// <summary>A sale of another till as downloaded from ERPNext: its ERPNext name stands for its number.</summary>
    private static Receipt RemoteSale(string erpName, decimal qty, int daysAgo) => new(
        erpName, ReceiptKind.Sale, null, "", "", At.AddDays(-daysAgo),
        [new ReceiptLine(1, "MILK", "Full Cream Milk 1L", "111", "PCS", 1m, qty, M("6.79"), M("6.79"), qty * M("6.79"), null, null, false, null)],
        qty * M("6.79"), 0m, 0m, qty * M("6.79"), false, 0m, 0m, [new ReceiptPayment("Cash Counter 1", qty * M("6.79"))], 0m, 0m, null);

    [Fact]
    public void Returns_taken_on_other_tills_count_against_what_is_left()
    {
        var sale = SellMilk(3);
        var builder = new ReturnBuilder(store, ctx, 2, Modes, () => At, otherTills: new OtherTills(OtherTillReturn("ACC-PSINV-RET-1", sale.ClientId, 2m)));

        Assert.Equal(1m, builder.Returnable(sale, 1));
        Assert.Equal(2m, builder.Returned(sale, 1));
        Assert.Throws<InvalidOperationException>(() => builder.Build(sale, [new ReturnLineRequest(1, 2m)], TenderKind.Cash, "c", "S2", null));
        builder.Build(sale, [new ReturnLineRequest(1, 1m)], TenderKind.Cash, "c", "S2", null);
        Assert.Equal(0m, builder.Returnable(sale, 1));
    }

    [Fact]
    public void A_bill_from_another_till_is_returned_against_its_erpnext_name()
    {
        var original = RemoteSale("ACC-PSINV-2026-00042", 2m, daysAgo: 1);
        var builder = new ReturnBuilder(store, ctx, 2, Modes, () => At, otherTills: new OtherTills());

        var ret = builder.Build(original, [new ReturnLineRequest(1, 1m)], TenderKind.Cash, "c", "S2", null);

        Assert.Equal("ACC-PSINV-2026-00042", ret.ReturnAgainst);
        Assert.Equal(ret, Assert.Single(store.ReturnsAgainst("ACC-PSINV-2026-00042")));
        Assert.Equal(1m, builder.Returnable(original, 1));
    }

    [Fact]
    public void A_return_keeps_the_customer_of_its_original_bill()
    {
        var original = RemoteSale("ACC-PSINV-2026-00043", 2m, daysAgo: 1) with { Customer = "Ahmed Trading" };
        var builder = new ReturnBuilder(store, ctx, 2, Modes, () => At, otherTills: new OtherTills());

        Assert.Equal("Ahmed Trading", builder.Build(original, [new ReturnLineRequest(1, 1m)], TenderKind.Cash, "c", "S2", null).Customer);
        Assert.Null(Builder().Build(SellMilk(1), [new ReturnLineRequest(1, 1m)], TenderKind.Cash, "c", "S2", null).Customer);
    }

    [Fact]
    public void A_bill_from_another_till_follows_the_same_approval_rules()
    {
        var old = RemoteSale("ACC-PSINV-2026-00007", 2m, daysAgo: 8);
        var builder = new ReturnBuilder(store, ctx, 2, Modes, () => At, otherTills: new OtherTills());
        Assert.Equal(new[] { ApprovalAction.ReturnOldReceipt }, builder.Preview(old, [new ReturnLineRequest(1, 1m)]).Needs);

        // 7 of 8 already refunded on other tills (47.53): one more (7.13 with VAT) goes over the 50 limit.
        var recent = RemoteSale("ACC-PSINV-2026-00008", 8m, daysAgo: 0);
        var refunded = new ReturnBuilder(store, ctx, 2, Modes, () => At,
            otherTills: new OtherTills(OtherTillReturn("ACC-PSINV-RET-2", "ACC-PSINV-2026-00008", 7m)));
        Assert.Equal(new[] { ApprovalAction.ReturnOverLimit }, refunded.Preview(recent, [new ReturnLineRequest(1, 1m)]).Needs);
        Assert.Empty(builder.Preview(recent, [new ReturnLineRequest(1, 1m)]).Needs);
    }
}
