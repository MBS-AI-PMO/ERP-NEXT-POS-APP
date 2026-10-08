using TillPOS.Core.Sales;
using TillPOS.Data;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Data;

public sealed class DeliveryStoreTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 8, 15, 0, 0, TimeSpan.FromHours(4));
    private readonly TempDb temp = new();
    private readonly ReceiptStore receipts;
    private readonly DeliveryStore store;

    public DeliveryStoreTests()
    {
        receipts = new ReceiptStore(temp.Db);
        store = new DeliveryStore(temp.Db, receipts);
    }

    public void Dispose() => temp.Dispose();

    private static Receipt Bill(string id, decimal grand) => new(id, ReceiptKind.Sale, null, "S1", "simran", At,
        [new ReceiptLine(1, "MILK", "Milk", "111", "PCS", 1m, 1m, grand, grand, grand, null, null, false, null)],
        grand, grand, 0m, grand, false, 0m, 0m, [], 0m, 0m, null) { IsDelivery = true };

    private static Delivery Open(string id, DateTimeOffset at, decimal grand = 11.429m) =>
        new(id, DeliveryStatus.Open, at, "S1", "simran", "Simran", "Counter 2", Bill(id, grand), 11.50m, 11.43m);

    [Fact]
    public void Added_deliveries_are_listed_oldest_first_and_read_back_whole()
    {
        store.Add(Open("TILL2-B", At.AddMinutes(5)));
        store.Add(Open("TILL2-A", At));

        Assert.Equal(new[] { "TILL2-A", "TILL2-B" }, store.Open().Select(d => d.ClientId));
        var a = store.Get("TILL2-A")!;
        Assert.Equal(M("11.50"), a.CashToCollect);
        Assert.True(a.Bill.IsDelivery);
        Assert.Equal("MILK", Assert.Single(a.Bill.Lines).ItemCode);
    }

    [Fact]
    public void Open_deliveries_survive_a_new_store_instance()
    {
        store.Add(Open("TILL2-A", At));
        Assert.Single(new DeliveryStore(temp.Db, new ReceiptStore(temp.Db)).Open());
    }

    [Fact]
    public void Update_changes_an_open_delivery_only()
    {
        store.Add(Open("TILL2-A", At));
        Assert.True(store.Update(Open("TILL2-A", At, 5m) with { CashToCollect = 5m }));
        Assert.Equal(5m, store.Get("TILL2-A")!.CashToCollect);

        Assert.True(store.Cancel("TILL2-A", At.AddHours(1), "sup", "Refused"));
        Assert.False(store.Update(Open("TILL2-A", At, 1m)));
    }

    [Fact]
    public void Cancel_closes_it_with_reason_and_supervisor()
    {
        store.Add(Open("TILL2-A", At));
        Assert.True(store.Cancel("TILL2-A", At.AddHours(1), "sup", "Refused"));

        var d = store.Get("TILL2-A")!;
        Assert.Equal(DeliveryStatus.Cancelled, d.Status);
        Assert.Equal(("sup", "Refused", At.AddHours(1)), (d.ClosedBy, d.Reason, d.ClosedAt!.Value));
        Assert.Empty(store.Open());
        Assert.Null(receipts.Get("TILL2-A"));
    }

    [Fact]
    public void Pay_saves_the_receipt_and_closes_the_delivery_together()
    {
        store.Add(Open("TILL2-A", At));
        var saved = 0;
        receipts.Saved += () => saved++;
        var paid = Bill("TILL2-A", 11.429m) with { Payments = [new ReceiptPayment("Cash Counter 2", 11.50m)], CreatedAt = At.AddHours(2) };

        Assert.True(store.Pay(paid, At.AddHours(2), "simran"));

        Assert.Equal(DeliveryStatus.Paid, store.Get("TILL2-A")!.Status);
        Assert.Equal(At.AddHours(2), receipts.Get("TILL2-A")!.CreatedAt);
        Assert.Empty(store.Open());
        Assert.Equal(1, saved);                                  // the upload is nudged like any sale
    }

    [Fact]
    public void Pay_rolls_back_when_the_receipt_cannot_be_inserted()
    {
        store.Add(Open("TILL2-A", At));
        receipts.Save(Bill("TILL2-A", 11.429m) with { Payments = [new ReceiptPayment("Cash Counter 2", 11.50m)] });   // same ClientId already stored
        var paid = Bill("TILL2-A", 11.429m) with { Payments = [new ReceiptPayment("Cash Counter 2", 11.50m)] };

        try { Assert.False(store.Pay(paid, At, "simran")); } catch (Exception) { /* a failed insert may throw */ }

        Assert.Equal(DeliveryStatus.Open, store.Get("TILL2-A")!.Status);
        Assert.Single(store.Open());
        Assert.Single(receipts.ListPending(10));
    }

    [Fact]
    public void Pay_is_refused_once_paid_or_cancelled_and_saves_no_receipt()
    {
        store.Add(Open("TILL2-A", At));
        store.Add(Open("TILL2-B", At));
        var paidA = Bill("TILL2-A", 11.429m) with { Payments = [new ReceiptPayment("Cash Counter 2", 11.50m)] };
        Assert.True(store.Pay(paidA, At, "simran"));
        Assert.False(store.Pay(paidA, At, "simran"));            // paid twice

        store.Cancel("TILL2-B", At, "sup", "Refused");
        var paidB = Bill("TILL2-B", 11.429m) with { Payments = [new ReceiptPayment("Cash Counter 2", 11.50m)] };
        Assert.False(store.Pay(paidB, At, "simran"));            // paid after cancel
        Assert.Null(receipts.Get("TILL2-B"));
        Assert.Single(receipts.ListPending(10));
    }
}
