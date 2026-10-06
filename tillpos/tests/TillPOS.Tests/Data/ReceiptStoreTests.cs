using Microsoft.Data.Sqlite;
using TillPOS.Core.Sales;
using TillPOS.Data;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Data;

public sealed class ReceiptStoreTests : IDisposable
{
    private readonly TempDb temp = new();
    private readonly ReceiptStore store;

    public ReceiptStoreTests() => store = new ReceiptStore(temp.Db);

    public void Dispose() => temp.Dispose();

    private static Receipt Sale(string id, string shift = "S1", int minute = 0, string? returnAgainst = null, decimal qty = 1m) => new(
        id, returnAgainst is null ? ReceiptKind.Sale : ReceiptKind.Return, returnAgainst, shift, "cashier",
        new DateTimeOffset(2026, 10, 6, 15, minute, 0, TimeSpan.FromHours(4)),
        [new ReceiptLine(1, "000089", "CUCUMBER/KIYAR", "2000089007400", "Kg", 1m, qty * M("0.740"), M("3.50"), M("3.50"),
            qty * M("2.590"), null, null, true, null)],
        qty * M("2.590"), qty * M("2.467"), qty * M("0.123"), qty * M("2.590"), true, qty * M("2.500"), qty * M("-0.090"),
        [new ReceiptPayment("Cash Counter 2", 5m)], M("2.50"), M("-0.090"), null);

    [Fact]
    public void Receipt_round_trips_with_exact_decimals()
    {
        var r = Sale("TILL2-1");
        store.Save(r);
        var back = store.Get("TILL2-1")!;
        Assert.Equal(r with { Lines = back.Lines, Payments = back.Payments }, back);
        Assert.Equal(r.Lines, back.Lines);
        Assert.Equal(r.Payments, back.Payments);
        Assert.Equal(M("0.740"), back.Lines[0].Qty);
    }

    [Fact]
    public void Saving_the_same_client_id_twice_fails()
    {
        store.Save(Sale("TILL2-1"));
        Assert.Throws<SqliteException>(() => store.Save(Sale("TILL2-1")));
    }

    [Fact]
    public void Sequence_survives_a_new_store_instance()
    {
        Assert.Equal(1, store.NextSequence());
        Assert.Equal(2, store.NextSequence());
        Assert.Equal(3, new ReceiptStore(temp.Db).NextSequence());
    }

    [Fact]
    public void Pending_receipts_come_oldest_first_and_leave_the_outbox_when_synced()
    {
        store.Save(Sale("B", minute: 2));
        store.Save(Sale("A", minute: 1));

        Assert.Equal(new[] { "A", "B" }, store.ListPending(10).Select(r => r.ClientId));

        store.MarkSynced("A", "ACC-PSINV-2026-06001");
        Assert.Equal(new[] { "B" }, store.ListPending(10).Select(r => r.ClientId));
        Assert.Equal(new ReceiptSyncInfo(ReceiptSyncStatus.Synced, "ACC-PSINV-2026-06001", null, 1), store.SyncInfo("A"));
    }

    [Fact]
    public void Failed_receipts_wait_for_a_retry()
    {
        store.Save(Sale("A"));
        store.MarkFailed("A", "Item 000089 is disabled");

        Assert.Empty(store.ListPending(10));
        Assert.Equal("A", Assert.Single(store.ListFailed()).ClientId);
        Assert.Equal(new ReceiptSyncInfo(ReceiptSyncStatus.Failed, null, "Item 000089 is disabled", 1), store.SyncInfo("A"));

        store.Retry("A");
        Assert.Equal("A", Assert.Single(store.ListPending(10)).ClientId);
    }

    [Fact]
    public void Returns_and_shift_receipts_are_found()
    {
        store.Save(Sale("SALE", "S1"));
        store.Save(Sale("RET", "S2", returnAgainst: "SALE", qty: -1m));

        Assert.Equal("RET", Assert.Single(store.ReturnsAgainst("SALE")).ClientId);
        Assert.Equal("SALE", Assert.Single(store.ByShift("S1")).ClientId);
    }
}
