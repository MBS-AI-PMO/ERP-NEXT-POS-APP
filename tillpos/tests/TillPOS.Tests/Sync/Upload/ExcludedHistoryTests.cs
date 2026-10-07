using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;
using TillPOS.Data;
using TillPOS.Sync.Upload;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Sync.Upload;

/// <summary>Live never uploads old history by default (FakeErp only).</summary>
public sealed class ExcludedHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Day1 = new(2026, 10, 4, 8, 0, 0, TimeSpan.FromHours(4));
    private readonly TempDb temp = new();
    private readonly ShiftStore shifts;
    private readonly ReceiptStore receipts;
    private readonly ApprovalStore approvals;
    private readonly CatalogStore kv;
    private readonly FakeErp erp = new();
    private readonly DateTimeOffset goLive = Day1.AddDays(2);   // between the two old shifts and the new one

    public ExcludedHistoryTests()
    {
        shifts = new ShiftStore(temp.Db);
        receipts = new ReceiptStore(temp.Db);
        approvals = new ApprovalStore(temp.Db);
        kv = new CatalogStore(temp.Db);
        UploadTestData.ErpComputesTheTillsTotals(erp, receipts);
        Shift("OLD1", Day1, close: true);
        Shift("OLD2", Day1.AddDays(1), close: true);
        approvals.Add(new ApprovalRecord("old-login", ApprovalAction.SettingsChange, "", "sup1", "", null, null, 0m, null, Day1));
    }

    public void Dispose() => temp.Dispose();

    private void Shift(string id, DateTimeOffset opened, bool close)
    {
        shifts.Open(new ShiftOpening(id, "cashier1", "Al Ain Counter 1", opened, [new ReceiptPayment("Cash Counter 1", 100m)]));
        receipts.Save(UploadTestData.Sale(id + "-BILL", id, opened.AddHours(1)));
        approvals.Add(new ApprovalRecord(id + "-VOID", ApprovalAction.LineVoid, "cashier1", "sup1", id, null, "RICE5", 1m, null, opened.AddHours(1)));
        if (close) shifts.Close(new ShiftClosing(id, opened.AddHours(2), [], 1, 0, 10.5m, 10m, 0.5m));
    }

    private Uploader New() => new(erp, erp, UploadMode.Live, shifts, receipts, approvals, _ => PayloadTests.Counter1, "TILL2", "till2@shop.local",
        () => goLive.AddHours(5), (_, _) => { })
    {
        LiveSince = () => UploadHistory.LiveSince(kv),
    };

    private IEnumerable<string> SentIds => erp.Inserted.Select(i =>
        i.Doc.TryGetProperty("posa_client_request_id", out var c) ? c.GetString()! : i.Doc.GetProperty("custom_offline_id").GetString()!);

    [Fact]
    public async Task Switching_to_live_uploads_only_what_comes_after()
    {
        Assert.Equal(2, UploadHistory.EarlierShifts(shifts, kv, goLive));

        Assert.True(UploadHistory.SwitchToLive(shifts, kv, goLive, includeHistory: false));
        Assert.False(UploadHistory.SwitchToLive(shifts, kv, goLive.AddDays(1), includeHistory: false));   // later switches change nothing
        Shift("NEW", goLive.AddHours(1), close: true);
        var report = await New().RunOnceAsync();

        Assert.Equal(["NEW", "NEW-BILL", "NEW", "NEW-VOID"], SentIds);
        Assert.Equal(0, report.Waiting);   // excluded documents are not counted
        Assert.Equal(0, report.Failed);
        Assert.Equal(ReceiptSyncStatus.Excluded, receipts.SyncInfo("OLD1-BILL").Status);
        Assert.Equal(UploadStatus.Excluded, shifts.SyncInfo("OLD2")!.ClosingStatus);
        Assert.Empty(approvals.Outbox());   // old-login and the old voids are excluded
    }

    [Fact]
    public async Task Including_history_uploads_everything()
    {
        UploadHistory.SwitchToLive(shifts, kv, goLive, includeHistory: true);
        Shift("NEW", goLive.AddHours(1), close: true);

        await New().RunOnceAsync();

        Assert.Equal(Day1, UploadHistory.LiveSince(kv));
        Assert.Equal(["OLD1", "OLD1-BILL", "OLD1", "OLD2", "OLD2-BILL", "OLD2", "NEW", "NEW-BILL", "NEW", "old-login", "OLD1-VOID", "OLD2-VOID",
            "NEW-VOID"], SentIds);
    }

    [Fact]
    public void The_first_switch_to_live_is_refused_while_a_shift_is_open()
    {
        Shift("OPEN", goLive.AddHours(-1), close: false);

        Assert.True(UploadHistory.MustCloseShiftFirst(shifts, kv));
        var refused = Assert.Throws<InvalidOperationException>(() => UploadHistory.SwitchToLive(shifts, kv, goLive, includeHistory: false));
        Assert.Equal("Close the shift first, then switch to Live.", refused.Message);
        Assert.Null(UploadHistory.LiveSince(kv));
        Assert.Equal(UploadStatus.Pending, shifts.SyncInfo("OLD1")!.OpeningStatus);   // nothing was excluded

        shifts.Close(new ShiftClosing("OPEN", goLive.AddMinutes(-1), [], 1, 0, 10.5m, 10m, 0.5m));
        Assert.False(UploadHistory.MustCloseShiftFirst(shifts, kv));
        Assert.True(UploadHistory.SwitchToLive(shifts, kv, goLive, includeHistory: false));
        Assert.False(UploadHistory.MustCloseShiftFirst(shifts, kv));   // once Live, shifts open as usual
    }

    [Fact]
    public async Task Once_live_an_open_shift_is_normal_and_nothing_is_excluded_by_the_upload()
    {
        UploadHistory.SwitchToLive(shifts, kv, goLive, includeHistory: false);
        Shift("NEW", goLive.AddHours(1), close: false);
        Assert.False(UploadHistory.MustCloseShiftFirst(shifts, kv));

        await New().RunOnceAsync();

        Assert.Equal(["NEW", "NEW-BILL"], SentIds.Take(2));
        Assert.Equal(UploadStatus.Excluded, shifts.SyncInfo("OLD1")!.OpeningStatus);
        Assert.Equal(ReceiptSyncStatus.Synced, receipts.SyncInfo("NEW-BILL").Status);
    }

    [Fact]
    public void Nothing_created_at_or_after_the_switch_is_excluded()
    {
        // A bill stamped after the moment of going Live (clock change) in a shift closed before it stays in the queue.
        receipts.Save(UploadTestData.Sale("OLD2-LATE", "OLD2", goLive.AddMinutes(1)));
        approvals.Add(new ApprovalRecord("late", ApprovalAction.LineVoid, "cashier1", "sup1", "OLD2", null, null, 0m, null, goLive));

        UploadHistory.SwitchToLive(shifts, kv, goLive, includeHistory: false);

        Assert.Equal(ReceiptSyncStatus.Excluded, receipts.SyncInfo("OLD2-BILL").Status);
        Assert.Equal(ReceiptSyncStatus.Pending, receipts.SyncInfo("OLD2-LATE").Status);
        Assert.Contains(approvals.Outbox(), a => a.Record.Id == "late");
    }

    [Fact]
    public async Task Approvals_made_since_going_live_upload_even_when_their_shift_never_will()
    {
        UploadHistory.SwitchToLive(shifts, kv, goLive, includeHistory: false);
        // Made after the switch but naming an excluded shift (and its excluded bill), e.g. a late supervisor action.
        approvals.Add(new ApprovalRecord("late", ApprovalAction.ReturnOldReceipt, "cashier1", "sup1", "OLD1", "OLD1-BILL", null, 1m, null,
            goLive.AddHours(1)));

        await New().RunOnceAsync();

        var sent = Assert.Single(erp.Inserted).Doc;
        Assert.Equal("late", sent.GetProperty("custom_offline_id").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, sent.GetProperty("shift").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, sent.GetProperty("invoice").ValueKind);
        Assert.Equal(UploadStatus.Excluded, Assert.Single(approvals.Problems(), p => p.Id == "OLD1-VOID").Status);   // older: stays out
    }

    [Fact]
    public async Task Approvals_of_a_handled_shift_made_since_going_live_link_only_what_is_in_erpnext()
    {
        UploadHistory.SwitchToLive(shifts, kv, goLive, includeHistory: false);
        Shift("NEW", goLive.AddHours(1), close: true);
        shifts.MarkFailed("NEW", ShiftDocument.Opening, "bad", null);
        shifts.MarkHandled("NEW", ShiftDocument.Opening, "Handled by sup: opened by hand");
        receipts.MarkSynced("NEW-BILL", "ACC-SYNCED");
        approvals.Add(new ApprovalRecord("late", ApprovalAction.ReturnOverLimit, "cashier1", "sup1", "NEW", "NEW-BILL", null, 1m, null,
            goLive.AddHours(2)));

        await New().RunOnceAsync();

        var docs = erp.Inserted.Where(i => i.Doctype == "TillPOS Approval").Select(i => i.Doc).ToList();
        var late = docs.Single(d => d.GetProperty("custom_offline_id").GetString() == "late");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, late.GetProperty("shift").ValueKind);
        Assert.Equal("ACC-SYNCED", late.GetProperty("invoice").GetString());
    }

    [Fact]
    public void A_failing_exclusion_leaves_the_till_not_live()
    {
        using (var c = temp.Db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "CREATE TRIGGER boom BEFORE UPDATE ON shift BEGIN SELECT RAISE(ABORT, 'boom'); END;";
            cmd.ExecuteNonQuery();
        }

        Assert.ThrowsAny<Exception>(() => UploadHistory.SwitchToLive(shifts, kv, goLive, includeHistory: false));

        Assert.Null(UploadHistory.LiveSince(kv));
        Assert.Equal(UploadStatus.Pending, shifts.SyncInfo("OLD1")!.OpeningStatus);
    }

    [Fact]
    public void Live_from_the_settings_file_waits_for_the_open_shift_and_logs_the_refusal_once()
    {
        Shift("OPEN", goLive.AddHours(-1), close: false);

        Assert.Equal((UploadMode.Off, UploadHistory.CloseShiftFirst), UploadHistory.ModeAtStart(UploadMode.Live, shifts, kv, approvals, goLive));
        Assert.Equal((UploadMode.Off, UploadHistory.CloseShiftFirst), UploadHistory.ModeAtStart(UploadMode.Live, shifts, kv, approvals, goLive));
        var refusal = Assert.Single(approvals.Outbox(), a => a.Record.Action == ApprovalAction.UploadModeChange).Record;
        Assert.Equal(("", "Live requested in settings.json while a shift was open — stayed Off"), (refusal.SupervisorId, refusal.Reason));
        Assert.Null(UploadHistory.LiveSince(kv));

        shifts.Close(new ShiftClosing("OPEN", goLive.AddMinutes(-1), [], 1, 0, 10.5m, 10m, 0.5m));
        Assert.Equal((UploadMode.Live, (string?)null), UploadHistory.ModeAtStart(UploadMode.Live, shifts, kv, approvals, goLive));
        Assert.Equal(goLive, UploadHistory.LiveSince(kv));
        Assert.Single(approvals.Outbox(), a => a.Record.Reason == "from settings file");

        // Once Live, a later start with a shift open is normal and logs nothing more.
        Shift("NEXT", goLive.AddHours(1), close: false);
        Assert.Equal((UploadMode.Live, (string?)null), UploadHistory.ModeAtStart(UploadMode.Live, shifts, kv, approvals, goLive.AddHours(2)));
        Assert.Equal(2, approvals.Outbox().Count(a => a.Record.Action == ApprovalAction.UploadModeChange));
    }

    [Fact]
    public void The_refusal_is_logged_again_after_the_mode_changed()
    {
        Shift("OPEN", goLive.AddHours(-1), close: false);
        UploadHistory.ModeAtStart(UploadMode.Live, shifts, kv, approvals, goLive);
        Assert.Equal((UploadMode.DryRun, (string?)null), UploadHistory.ModeAtStart(UploadMode.DryRun, shifts, kv, approvals, goLive));
        UploadHistory.ModeAtStart(UploadMode.Live, shifts, kv, approvals, goLive);

        Assert.Equal(2, approvals.Outbox().Count(a => a.Record.Reason?.StartsWith("Live requested", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task An_excluded_shift_can_be_put_back()
    {
        UploadHistory.SwitchToLive(shifts, kv, goLive, includeHistory: false);
        shifts.Include("OLD2");

        await New().RunOnceAsync();

        Assert.Equal(["OLD2", "OLD2-BILL", "OLD2", "OLD2-VOID"], SentIds);
        Assert.Equal(UploadStatus.Excluded, shifts.SyncInfo("OLD1")!.OpeningStatus);
    }
}
