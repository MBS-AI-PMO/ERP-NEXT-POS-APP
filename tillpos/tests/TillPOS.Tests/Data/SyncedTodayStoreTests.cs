using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;
using TillPOS.Data;

namespace TillPOS.Tests.Data;

/// <summary>When each document reached ERPNext (synced_at), and the waiting lists, for the header and the Sync status window.</summary>
public sealed class SyncedTodayStoreTests : IDisposable
{
    private static readonly DateTimeOffset Midnight = new(2026, 10, 7, 0, 0, 0, TimeSpan.FromHours(4));
    private readonly TempDb temp = new();
    private readonly ShiftStore shifts;
    private readonly ReceiptStore receipts;
    private readonly ApprovalStore approvals;

    public SyncedTodayStoreTests()
    {
        shifts = new ShiftStore(temp.Db);
        receipts = new ReceiptStore(temp.Db);
        approvals = new ApprovalStore(temp.Db);
    }

    public void Dispose() => temp.Dispose();

    private void Shift(string id)
    {
        shifts.Open(new ShiftOpening(id, "c", "Test Counter", Midnight.AddHours(-20), [new ReceiptPayment("Cash Counter 2", 150m)]));
        shifts.Close(new ShiftClosing(id, Midnight.AddHours(-12), [], 3, 0, 75.5m, 71.9m, 3.6m));
    }

    private void Bill(string id, string shift, decimal total, ReceiptKind kind = ReceiptKind.Sale) =>
        receipts.Save(new Receipt(id, kind, kind == ReceiptKind.Return ? "B0" : null, shift, "c", Midnight.AddHours(-19), [], total, total, 0m, total,
            false, 0m, 0m, [], 0m, 0m, null));

    private void Approval(string id, decimal amount) =>
        approvals.Add(new ApprovalRecord(id, ApprovalAction.LineVoid, "c", "sup", "S1", null, "MILK", amount, null, Midnight.AddHours(-19)));

    [Fact]
    public void Documents_synced_since_midnight_are_counted_and_listed_newest_first()
    {
        Shift("S1");
        Bill("B1", "S1", 12.5m);
        Bill("R1", "S1", -6.79m, ReceiptKind.Return);
        Approval("A1", 6.79m);

        shifts.MarkSynced("S1", ShiftDocument.Opening, "POSA-OS-1", Midnight.AddMinutes(-5));     // yesterday: not counted
        receipts.MarkSynced("B1", "ACC-PSINV-1", Midnight.AddHours(9));
        receipts.MarkSynced("R1", "ACC-PSINV-2", Midnight.AddHours(9).AddMinutes(1));
        shifts.MarkSynced("S1", ShiftDocument.Closing, "POSA-CS-1", Midnight.AddHours(9).AddMinutes(2));
        approvals.MarkUploaded("A1", "TILLPOS-APP-1", Midnight.AddHours(9).AddMinutes(3));

        Assert.Equal(2, receipts.CountSyncedSince(Midnight));
        Assert.Equal(1, shifts.CountSyncedSince(Midnight));
        Assert.Equal(1, approvals.CountSyncedSince(Midnight));
        Assert.Equal(2, shifts.CountSyncedSince(Midnight.AddHours(-1)));

        var all = new[] { receipts.SyncedSince(Midnight), shifts.SyncedSince(Midnight), approvals.SyncedSince(Midnight) }
            .SelectMany(x => x).OrderByDescending(d => d.SyncedAt).ToList();
        Assert.Equal(
        [
            new SyncedDocument(OutboxKind.Approval, "A1", "TILLPOS-APP-1", Midnight.AddHours(9).AddMinutes(3), 6.79m),
            new SyncedDocument(OutboxKind.Closing, "S1", "POSA-CS-1", Midnight.AddHours(9).AddMinutes(2), 75.5m),
            new SyncedDocument(OutboxKind.Bill, "R1", "ACC-PSINV-2", Midnight.AddHours(9).AddMinutes(1), -6.79m, IsReturn: true),
            new SyncedDocument(OutboxKind.Bill, "B1", "ACC-PSINV-1", Midnight.AddHours(9), 12.5m),
        ], all);
        Assert.Equal(new SyncedDocument(OutboxKind.Opening, "S1", "POSA-OS-1", Midnight.AddMinutes(-5), 150m),
            Assert.Single(shifts.SyncedSince(Midnight.AddHours(-1)), d => d.Kind == OutboxKind.Opening));
    }

    [Fact]
    public void Synced_at_defaults_to_now()
    {
        Shift("S1");
        Bill("B1", "S1", 1m);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        receipts.MarkSynced("B1", "ACC-1");

        Assert.InRange(Assert.Single(receipts.SyncedSince(before)).SyncedAt, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void Documents_synced_before_the_column_existed_are_not_counted()
    {
        Shift("S1");
        Bill("B1", "S1", 1m);
        receipts.MarkSynced("B1", "ACC-1", Midnight.AddHours(1));
        using (var c = temp.Db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE receipt SET synced_at = NULL";
            cmd.ExecuteNonQuery();
        }

        Assert.Equal(0, receipts.CountSyncedSince(Midnight));
        Assert.Empty(receipts.SyncedSince(Midnight));
    }

    [Fact]
    public void Waiting_lists_pending_documents_with_their_reason()
    {
        shifts.Open(new ShiftOpening("S1", "c", "Test Counter", Midnight.AddHours(8), []));
        Bill("B1", "S1", 1m);
        Bill("B2", "S1", 2m);
        Approval("A1", 0m);
        receipts.MarkInFlight("B2", Midnight.AddHours(9));
        shifts.MarkWaiting("S1", ShiftDocument.Opening, "Waits for the previous shift of Test Counter to close in ERPNext.");
        receipts.MarkSynced("B1", "ACC-1", Midnight.AddHours(9));

        Assert.Equal([("B2", UploadStatus.Pending, ReceiptStore.InFlight)], receipts.Waiting().Select(w => (w.Id, w.Status, w.Error)));
        var opening = Assert.Single(shifts.Waiting());
        Assert.Equal((OutboxKind.Opening, "S1", "Waits for the previous shift of Test Counter to close in ERPNext."), (opening.Kind, opening.Id, opening.Error));
        Assert.Equal([("A1", (string?)null)], approvals.Waiting().Select(w => (w.Id, w.Error)));

        shifts.Close(new ShiftClosing("S1", Midnight.AddHours(10), [], 0, 0, 0m, 0m, 0m));
        Assert.Equal([OutboxKind.Closing, OutboxKind.Opening], shifts.Waiting().Select(w => w.Kind));      // newest first
        Assert.Equal([OutboxKind.Closing], shifts.Waiting(limit: 1).Select(w => w.Kind));
    }

    [Fact]
    public void The_migration_adds_the_synced_at_columns()
    {
        using var c = temp.Db.Open();
        foreach (var (table, column) in new[] { ("receipt", "synced_at"), ("shift", "opening_synced_at"), ("shift", "closing_synced_at"), ("approval_log", "synced_at") })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
    }
}
