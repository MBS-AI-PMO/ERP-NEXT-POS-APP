using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;
using TillPOS.Data;

namespace TillPOS.Tests.Data;

/// <summary>The upload state of shifts, bills and approvals (plan 2b outbox columns).</summary>
public sealed class UploadStateStoreTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 6, 8, 0, 0, TimeSpan.FromHours(4));
    private readonly TempDb temp = new();
    private readonly ShiftStore shifts;
    private readonly ReceiptStore receipts;
    private readonly ApprovalStore approvals;

    public UploadStateStoreTests()
    {
        shifts = new ShiftStore(temp.Db);
        receipts = new ReceiptStore(temp.Db);
        approvals = new ApprovalStore(temp.Db);
    }

    public void Dispose() => temp.Dispose();

    private void Open(string id, int hour) => shifts.Open(new ShiftOpening(id, "c", "", At.AddHours(hour), []));

    private void Close(string id, int hour) =>
        shifts.Close(new ShiftClosing(id, At.AddHours(hour), [], 0, 0, 0m, 0m, 0m));

    private void Bill(string id, string shift) =>
        receipts.Save(new Receipt(id, ReceiptKind.Sale, null, shift, "c", At, [], 1m, 1m, 0m, 1m, false, 0m, 0m, [], 0m, 0m, null));

    [Fact]
    public void A_new_shift_waits_with_its_opening_and_its_closing_counts_once_closed()
    {
        Open("S1", 0);
        Assert.Equal(new ShiftSyncInfo(UploadStatus.Pending, null, UploadStatus.Pending, null, null, 0, null), shifts.SyncInfo("S1"));
        Assert.Equal(1, shifts.CountPending());

        Close("S1", 1);
        Assert.Equal(2, shifts.CountPending());
        Assert.Null(shifts.SyncInfo("NOPE"));
    }

    [Fact]
    public void Unfinished_lists_shifts_with_anything_left_oldest_first()
    {
        Open("S1", 0);
        Bill("B1", "S1");
        Close("S1", 1);
        Open("S2", 2);

        Assert.Equal(["S1", "S2"], shifts.Unfinished().Select(s => s.Opening.ClientId));

        shifts.MarkSynced("S1", ShiftDocument.Opening, "POS-OPE-1");
        shifts.MarkSynced("S1", ShiftDocument.Closing, "POS-CLO-1");
        Assert.Equal(["S1", "S2"], shifts.Unfinished().Select(s => s.Opening.ClientId));   // bill B1 still waits

        receipts.MarkSynced("B1", "ACC-1");
        shifts.MarkSynced("S2", ShiftDocument.Opening, "POS-OPE-2");
        Assert.Empty(shifts.Unfinished());                                                  // S2 is open with nothing to send

        Bill("B2", "S2");
        var s2 = Assert.Single(shifts.Unfinished());
        Assert.Equal("S2", s2.Opening.ClientId);
        Assert.Null(s2.Closing);
        Assert.Equal("POS-OPE-2", s2.Sync.ErpOpeningName);
    }

    [Fact]
    public void A_failed_shift_document_keeps_its_error_and_backoff_until_retried()
    {
        Open("S1", 0);
        var next = At.AddMinutes(1);

        shifts.MarkFailed("S1", ShiftDocument.Opening, "POS Profile not found", next);
        shifts.MarkFailed("S1", ShiftDocument.Opening, "POS Profile not found", next.AddMinutes(1));

        Assert.Equal(new ShiftSyncInfo(UploadStatus.Failed, null, UploadStatus.Pending, null, "POS Profile not found", 2, next.AddMinutes(1)),
            shifts.SyncInfo("S1"));
        Assert.Equal(1, shifts.CountFailed());
        Assert.Equal(0, shifts.CountPending());

        shifts.Retry("S1");
        Assert.Equal(new ShiftSyncInfo(UploadStatus.Pending, null, UploadStatus.Pending, null, "POS Profile not found", 0, null), shifts.SyncInfo("S1"));

        shifts.MarkSynced("S1", ShiftDocument.Opening, "POS-OPE-1");
        Assert.Equal(new ShiftSyncInfo(UploadStatus.Synced, "POS-OPE-1", UploadStatus.Pending, null, null, 0, null), shifts.SyncInfo("S1"));
        shifts.MarkFailed("S1", ShiftDocument.Opening, "late", next);   // ignored: already uploaded
        Assert.Equal(UploadStatus.Synced, shifts.SyncInfo("S1")!.OpeningStatus);
        Assert.Throws<KeyNotFoundException>(() => shifts.MarkSynced("NOPE", ShiftDocument.Opening, "x"));
    }

    [Fact]
    public void Bills_carry_their_backoff_and_a_retry_resets_it()
    {
        Open("S1", 0);
        Bill("B1", "S1");
        var next = At.AddSeconds(30);

        receipts.MarkFailed("B1", "Item disabled", next);

        var entry = Assert.Single(receipts.Outbox("S1"));
        Assert.Equal(new ReceiptSyncInfo(ReceiptSyncStatus.Failed, null, "Item disabled", 1, next), entry.Sync);
        Assert.Equal(1, receipts.CountFailed());

        receipts.Retry("B1");
        Assert.Equal(new ReceiptSyncInfo(ReceiptSyncStatus.Pending, null, "Item disabled", 0, null), receipts.SyncInfo("B1"));
        Assert.Equal(0, receipts.CountFailed());
    }

    [Fact]
    public void Approvals_upload_state()
    {
        approvals.Add(new ApprovalRecord("a2", ApprovalAction.LineVoid, "c", "s", "S1", null, null, 0m, null, At.AddMinutes(1)));
        approvals.Add(new ApprovalRecord("a1", ApprovalAction.UploadModeChange, "", "s", "", null, null, 0m, "Upload mode Off → Live", At));
        Assert.Equal(["a1", "a2"], approvals.Outbox().Select(a => a.Record.Id));
        Assert.Equal(ApprovalAction.UploadModeChange, approvals.Outbox()[0].Record.Action);
        Assert.Equal(2, approvals.CountPending());

        approvals.MarkFailed("a1", "DocType TillPOS Approval not found", At.AddSeconds(30));
        var failed = approvals.Outbox()[0];
        Assert.Equal((UploadStatus.Failed, "DocType TillPOS Approval not found", 1, (DateTimeOffset?)At.AddSeconds(30)),
            (failed.Status, failed.LastError, failed.Attempts, failed.NextAttemptAt));
        Assert.Equal(1, approvals.CountFailed());

        approvals.Retry("a1");
        Assert.Equal((UploadStatus.Pending, 0, (DateTimeOffset?)null), (approvals.Outbox()[0].Status, approvals.Outbox()[0].Attempts,
            approvals.Outbox()[0].NextAttemptAt));

        approvals.MarkUploaded("a1", "TPA-0001");
        Assert.Equal(["a2"], approvals.Outbox().Select(a => a.Record.Id));
        Assert.Equal(["a2"], approvals.Unsynced().Select(a => a.Id));

        approvals.MarkSynced(["a2"]);
        Assert.Empty(approvals.Outbox());
        Assert.Throws<KeyNotFoundException>(() => approvals.MarkUploaded("NOPE", "x"));
    }

    [Fact]
    public void The_in_flight_marker_keeps_a_document_pending_and_leaves_synced_ones_alone()
    {
        Open("S1", 0);
        Bill("B1", "S1");
        approvals.Add(new ApprovalRecord("a1", ApprovalAction.LineVoid, "c", "s", "S1", null, null, 0m, null, At));
        var until = At.AddMinutes(5);
        receipts.MarkFailed("B1", "Item disabled", At);

        receipts.MarkInFlight("B1", until);
        shifts.MarkInFlight("S1", ShiftDocument.Opening, until);
        approvals.MarkInFlight("a1", until);

        Assert.Equal(new ReceiptSyncInfo(ReceiptSyncStatus.Pending, null, "upload in progress", 1, until), receipts.SyncInfo("B1"));
        Assert.Equal(new ShiftSyncInfo(UploadStatus.Pending, null, UploadStatus.Pending, null, "upload in progress", 0, until), shifts.SyncInfo("S1"));
        var a = Assert.Single(approvals.Outbox());
        Assert.Equal((UploadStatus.Pending, "upload in progress", (DateTimeOffset?)until), (a.Status, a.LastError, a.NextAttemptAt));

        receipts.MarkSynced("B1", "ACC-1");
        receipts.MarkInFlight("B1", until);
        Assert.Equal(ReceiptSyncStatus.Synced, receipts.SyncInfo("B1").Status);
        Assert.Null(receipts.SyncInfo("B1").NextAttemptAt);
    }

    [Fact]
    public void Saving_a_bill_or_opening_and_closing_a_shift_signals_the_upload()
    {
        var signals = 0;
        receipts.Saved += () => signals++;
        shifts.Changed += () => signals++;

        Open("S1", 0);
        Bill("B1", "S1");
        Close("S1", 1);

        Assert.Equal(3, signals);
    }
}
