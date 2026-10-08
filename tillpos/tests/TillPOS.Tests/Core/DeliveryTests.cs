using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Tax;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class DeliveryTests
{
    private static readonly SalesTaxTemplate Vat = new("UAE VAT 5% - AAML", [new TaxRow(1, "VAT 5% - AAML", "VAT 5%", 5m, true)], null);
    private static readonly MoneySettings Money = new(3, RoundingMethod.Bankers, 0.25m);
    private static readonly DateTimeOffset At = new(2026, 10, 8, 15, 10, 0, TimeSpan.FromHours(4));
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();

    public DeliveryTests()
    {
        catalog.Items.Add(new Item("MILK", "Full Cream Milk 1L", "Dairy", null, "PCS", false, true));
        catalog.Prices.Add(new ItemPrice("P-MILK", "MILK", "PCS", M("11.429"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("111", "MILK", null));
    }

    private Cart NewCart() => new(new SaleContext(catalog, Money, "Standard Selling", "Stores - AAML", null, Vat, () => new DateOnly(2026, 10, 8))
        { PosProfile = "Al Ain Counter 2" });

    private Delivery MakeOne(int milks = 1)
    {
        var cart = NewCart();
        for (var i = 0; i < milks; i++) cart.AddBarcode("111");
        return Deliveries.Make(cart, "TILL2-20261008151000-000042", At, "TILL2-SHIFT-1", "simran", "Simran", "Counter 2");
    }

    [Fact]
    public void A_delivery_is_the_unpaid_bill_with_both_amounts_to_collect()
    {
        var d = MakeOne();

        Assert.Equal(DeliveryStatus.Open, d.Status);
        Assert.Equal("TILL2-20261008151000-000042", d.Bill.ClientId);
        Assert.Empty(d.Bill.Payments);
        Assert.True(d.Bill.IsDelivery);
        Assert.Equal(M("11.429"), d.Bill.GrandTotal);
        Assert.Equal(M("11.50"), d.CashToCollect);              // cash rounded to 0.25
        Assert.Equal(M("11.43"), d.CardToCollect);              // card to 2 decimals
        Assert.Equal("Al Ain Counter 2", d.Bill.PosProfile);
        Assert.Equal("MILK", Assert.Single(d.Bill.Lines).ItemCode);
    }

    [Fact]
    public void Restoring_a_delivery_keeps_its_prices_even_after_a_price_change()
    {
        var d = MakeOne(2);
        catalog.Prices.Clear();
        catalog.Prices.Add(new ItemPrice("P-MILK", "MILK", "PCS", M("20.000"), null, null));

        var cart = NewCart();
        cart.RestoreFixed(d.Bill.Lines);

        var line = Assert.Single(cart.Lines);
        Assert.Equal(M("11.429"), line.Rate);
        Assert.Equal(2m, line.Qty);
        Assert.Equal(M("22.858"), cart.Totals().GrandTotal);
    }

    [Fact]
    public void Restoring_works_for_an_item_no_longer_in_the_catalog()
    {
        var d = MakeOne();
        catalog.Items.Clear();

        var cart = NewCart();
        cart.RestoreFixed(d.Bill.Lines);

        Assert.Equal("Full Cream Milk 1L", Assert.Single(cart.Lines).Item.ItemName);
    }

    [Fact]
    public void Restoring_onto_a_bill_with_lines_is_refused()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        Assert.Throws<InvalidOperationException>(() => cart.RestoreFixed(MakeOne().Bill.Lines));
    }

    [Fact]
    public void Rebill_after_removing_items_recalculates_the_bill_and_amounts()
    {
        var d = MakeOne(2);
        var cart = NewCart();
        cart.RestoreFixed(d.Bill.Lines);
        cart.Decrement(cart.Lines[0].Id);

        var changed = Deliveries.Rebill(d, cart);

        Assert.Equal(d.ClientId, changed.ClientId);
        Assert.Equal(M("11.429"), changed.Bill.GrandTotal);
        Assert.Equal(M("11.50"), changed.CashToCollect);
        Assert.Equal(M("11.43"), changed.CardToCollect);
        Assert.Equal(1m, Assert.Single(changed.Bill.Lines).Qty);
    }

    [Fact]
    public void Build_with_a_delivery_number_keeps_it_and_marks_the_receipt()
    {
        var d = MakeOne();
        var cart = NewCart();
        cart.RestoreFixed(d.Bill.Lines);
        var plan = new PaymentCalculator(Money).Plan(cart.Totals().GrandTotal, Tender.Cash(M("11.50")));
        var store = new InMemoryReceiptStore();
        var recorder = new SaleRecorder(store, 2, new TenderModes("Cash Counter 1", "Credit Card"), () => At.AddHours(2), "Test Counter");

        var receipt = recorder.Build(cart, plan, "simran", "TILL2-SHIFT-2", null, "Simran", d.ClientId);

        Assert.Equal(d.ClientId, receipt.ClientId);
        Assert.True(receipt.IsDelivery);
        Assert.Equal(At.AddHours(2), receipt.CreatedAt);                      // the payment time
        Assert.Equal("TILL2-SHIFT-2", receipt.ShiftClientId);
        Assert.Equal(new[] { new ReceiptPayment("Cash Counter 1", M("11.50")) }, receipt.Payments);
        Assert.Equal(0m, receipt.Change);
        Assert.Empty(store.Saved);                                               // Build never saves
        Assert.Single(cart.Lines);                                               // nor clears
    }
}
