using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class SaleRecorderTests
{
    private static readonly SalesTaxTemplate Vat = new("UAE VAT 5% - AAML", [new TaxRow(1, "VAT 5% - AAML", "VAT 5%", 5m, true)], null);
    private static readonly MoneySettings Money = new(3, RoundingMethod.Bankers, 0.25m);
    private static readonly TenderModes Modes = new("Cash Counter 2", "Credit Card");
    private static readonly DateTimeOffset At = new(2026, 10, 6, 15, 30, 0, TimeSpan.FromHours(4));
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();
    private readonly InMemoryReceiptStore store = new();
    private readonly PaymentCalculator payments = new(Money);

    public SaleRecorderTests()
    {
        catalog.Items.Add(new Item("MILK", "Full Cream Milk 1L", "Dairy", null, "PCS", false, true));
        catalog.Prices.Add(new ItemPrice("P-MILK", "MILK", "PCS", M("6.79"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("111", "MILK", null));
        catalog.Items.Add(new Item("000089", "CUCUMBER/KIYAR", "Food", null, "Kg", false, true));
        catalog.Prices.Add(new ItemPrice("P-CUC", "000089", "Kg", M("3.50"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("000089", "000089", "Kg"));
    }

    private Cart NewCart() => new(new SaleContext(catalog, Money, "Standard Selling", "Stores - AAML", null, Vat, () => new DateOnly(2026, 10, 6)));
    private SaleRecorder Recorder() => new(store, 2, Modes, () => At);

    [Fact]
    public void Cash_sale_is_stored_with_client_id_rounding_and_change_and_cart_is_cleared()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        cart.AddBarcode("2000089007400");
        var total = cart.Totals().GrandTotal;                      // 6.79 + 2.59 = 9.38
        var plan = payments.Plan(total, Tender.Cash(20m));

        var r = Recorder().CompleteSale(cart, plan, "p.simran@quickgroc.com", "TILL2-SHIFT-20261006080000");

        Assert.Equal("TILL2-20261006153000-000001", r.ClientId);
        Assert.Equal(ReceiptKind.Sale, r.Kind);
        Assert.Equal(M("9.380"), r.GrandTotal);
        Assert.True(r.UsesErpRoundedTotal);
        Assert.Equal(M("9.500"), r.RoundedTotal);                  // remainder .13 > .125 → up
        Assert.Equal(M("0.120"), r.RoundingAdjustment);
        Assert.Equal(new[] { new ReceiptPayment("Cash Counter 2", 20m) }, r.Payments);
        Assert.Equal(M("10.50"), r.Change);
        Assert.Equal(2, r.Lines.Count);
        Assert.True(r.Lines[1].FromScaleLabel);
        Assert.Equal("2000089007400", r.Lines[1].Barcode);
        Assert.Equal(M("2.590"), r.Lines[1].Amount);
        Assert.Same(r, store.Get(r.ClientId));
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void Card_sale_is_exact_and_not_rounded()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        var plan = payments.Plan(cart.Totals().GrandTotal, Tender.Card());

        var r = Recorder().CompleteSale(cart, plan, "cashier", "S1");

        Assert.False(r.UsesErpRoundedTotal);
        Assert.Equal(0m, r.RoundedTotal);
        Assert.Equal(0m, r.RoundingAdjustment);
        Assert.Equal(new[] { new ReceiptPayment("Credit Card", M("6.790")) }, r.Payments);
    }

    [Fact]
    public void Split_sale_records_both_payments()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        cart.AddBarcode("111");                                    // 13.58
        var plan = payments.Plan(cart.Totals().GrandTotal, Tender.Split(10m, 5m));

        var r = Recorder().CompleteSale(cart, plan, "cashier", "S1");

        Assert.Equal(new[] { new ReceiptPayment("Credit Card", 10m), new ReceiptPayment("Cash Counter 2", 5m) }, r.Payments);
        Assert.Equal(M("3.50"), plan.CashDue);                     // 3.58 → 3.50
        Assert.Equal(M("1.50"), r.Change);
    }

    [Fact]
    public void Sequence_increases_per_sale()
    {
        var recorder = Recorder();
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var cart = NewCart();
            cart.AddBarcode("111");
            recorder.CompleteSale(cart, payments.Plan(cart.Totals().GrandTotal, Tender.Card()), "c", "S1");
        }
        Assert.Equal(new[] { "TILL2-20261006153000-000001", "TILL2-20261006153000-000002" }, store.Saved.Select(r => r.ClientId));
    }

    [Fact]
    public void Incomplete_payment_is_refused_and_cart_is_kept()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        var plan = payments.Plan(cart.Totals().GrandTotal, Tender.Cash(1m));

        Assert.Throws<InvalidOperationException>(() => Recorder().CompleteSale(cart, plan, "c", "S1"));
        Assert.Empty(store.Saved);
        Assert.Single(cart.Lines);
    }

    [Fact]
    public void Stale_payment_plan_is_refused_and_nothing_is_saved()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        var plan = payments.Plan(cart.Totals().GrandTotal, Tender.Cash(50m));
        cart.AddBarcode("111");                                    // total changed after the plan

        Assert.Throws<InvalidOperationException>(() => Recorder().CompleteSale(cart, plan, "c", "S1"));
        Assert.Empty(store.Saved);
        Assert.Equal(2m, cart.Lines[0].Qty);
    }

    [Fact]
    public void Empty_bill_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => Recorder().CompleteSale(NewCart(), payments.Plan(0m, Tender.Card()), "c", "S1"));

    [Fact]
    public void Client_ids_use_the_gregorian_calendar_whatever_the_culture()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Assert.Equal("TILL2-20261006153000-000007", ClientIds.Receipt(2, At, 7));
            Assert.Equal("TILL2-SHIFT-20261006153000", ClientIds.Shift(2, At));
        }
        finally { Thread.CurrentThread.CurrentCulture = saved; }
    }

    [Fact]
    public void Sale_stores_the_cashier_user()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        var plan = payments.Plan(cart.Totals().GrandTotal, Tender.Card());

        var r = Recorder().CompleteSale(cart, plan, "Simran", "S1", "s@x");

        Assert.Equal("s@x", r.CashierUser);
    }
}
