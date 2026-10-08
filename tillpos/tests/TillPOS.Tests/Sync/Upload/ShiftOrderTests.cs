using TillPOS.Core.Catalog;
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Sync.Upload;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Sync.Upload;

/// <summary>POS Awesome allows one open POS Opening Shift per user and POS Profile: a shift's opening waits until the previous
/// shift of the same counter is closed in ERPNext (FakeErp only).</summary>
public sealed class ShiftOrderTests : IDisposable
{
    private static readonly DateTimeOffset Morning = new(2026, 10, 6, 8, 0, 0, TimeSpan.FromHours(4));
    private const string Counter1 = "Al Ain Counter 1";
    private const string Counter2 = "Al Ain Counter 2";
    private readonly TempDb temp = new();
    private readonly ShiftStore shifts;
    private readonly ReceiptStore receipts;
    private readonly ApprovalStore approvals;
    private readonly FakeErp erp = new();
    private DateTimeOffset clock = new(2026, 10, 6, 20, 0, 0, TimeSpan.FromHours(4));

    public ShiftOrderTests()
    {
        shifts = new ShiftStore(temp.Db);
        receipts = new ReceiptStore(temp.Db);
        approvals = new ApprovalStore(temp.Db);
        UploadTestData.ErpComputesTheTillsTotals(erp, receipts);
    }

    public void Dispose() => temp.Dispose();

    private Uploader New() => new(erp, erp, UploadMode.Live, shifts, receipts, approvals,
        profile => profile switch
        {
            Counter1 or "" => PayloadTests.Counter1,
            Counter2 => PayloadTests.Counter1 with { PosProfile = Counter2 },
            _ => null as PosSettings,
        },
        "TILL2", "till2@shop.local", () => clock, (_, _) => { });

    private void Shift(string id, string counter, int hour, bool close)
    {
        shifts.Open(new ShiftOpening(id, "cashier1", counter, Morning.AddHours(hour), [new ReceiptPayment("Cash Counter 1", 100m)]));
        receipts.Save(UploadTestData.Sale(id + "-BILL", id, Morning.AddHours(hour).AddMinutes(10)));
        if (close) shifts.Close(new ShiftClosing(id, Morning.AddHours(hour + 1), [], 1, 0, 10.5m, 10m, 0.5m));
    }

    private int Openings(string id) =>
        erp.Inserted.Count(i => i.Doctype == "POS Opening Shift" && i.Doc.GetProperty("custom_offline_id").GetString() == id);

    [Fact]
    public async Task The_next_opening_waits_until_the_previous_shift_of_the_counter_is_closed_in_erpnext()
    {
        Shift("S1", Counter1, 0, close: true);
        Shift("S2", Counter1, 2, close: false);
        erp.RejectInsert = (doctype, _) => doctype == "POS Closing Shift" ? new ErpException(417, "Closing refused", "ValidationError") : null;

        var report = await New().RunOnceAsync();

        Assert.Equal(1, Openings("S1"));
        Assert.Equal(0, Openings("S2"));
        Assert.Equal(new ShiftSyncInfo(UploadStatus.Pending, null, UploadStatus.Pending, null, null, 0, null), shifts.SyncInfo("S2"));
        Assert.Contains(report.Problems, p => p.DocId == "S2" && p.Message == "Opening of shift S2 waits for S1 to close in ERPNext.");

        // Once S1's closing goes in (in the same run, before S2), S2's opening follows.
        erp.RejectInsert = null;
        clock = clock.AddMinutes(10);
        await New().RunOnceAsync();
        Assert.Equal(UploadStatus.Synced, shifts.SyncInfo("S1")!.ClosingStatus);
        Assert.Equal(1, Openings("S2"));
        Assert.Equal(ReceiptSyncStatus.Synced, receipts.SyncInfo("S2-BILL").Status);
    }

    [Fact]
    public async Task Different_counters_do_not_wait_for_each_other()
    {
        Shift("S1", Counter1, 0, close: true);
        Shift("S2", Counter2, 2, close: false);
        erp.RejectInsert = (doctype, _) => doctype == "POS Closing Shift" ? new ErpException(417, "Closing refused", "ValidationError") : null;

        await New().RunOnceAsync();

        Assert.Equal(1, Openings("S2"));
        Assert.Equal(UploadStatus.Synced, shifts.SyncInfo("S2")!.OpeningStatus);
    }

    [Fact]
    public async Task A_handled_or_excluded_previous_closing_does_not_block()
    {
        Shift("S1", Counter1, 0, close: true);
        Shift("S2", Counter1, 2, close: false);
        erp.RejectInsert = (doctype, _) => doctype == "POS Closing Shift" ? new ErpException(417, "Closing refused", "ValidationError") : null;
        await New().RunOnceAsync();
        shifts.MarkHandled("S1", ShiftDocument.Closing, "Handled by sup: closed on the website");

        await New().RunOnceAsync();

        Assert.Equal(1, Openings("S2"));
    }

    [Fact]
    public async Task An_opening_refused_because_the_previous_shift_is_open_waits_instead_of_failing()
    {
        Shift("S1", Counter1, 0, close: true);
        Shift("S2", Counter1, 2, close: false);
        erp.RejectInsert = (doctype, _) => doctype == "POS Closing Shift" ? new ErpException(417, "Closing refused", "ValidationError") : null;
        await New().RunOnceAsync();
        shifts.MarkFailed("S2", ShiftDocument.Opening,
            "User till2@shop.local already has an open POS shift POSA-OS-26-00001 for POS Profile Al Ain Counter 1. Close it before opening a new one.",
            clock.AddHours(1));

        var report = await New().RunOnceAsync();

        var sync = shifts.SyncInfo("S2")!;
        Assert.Equal((UploadStatus.Pending, 1, (DateTimeOffset?)null), (sync.OpeningStatus, sync.Attempts, sync.NextAttemptAt));
        Assert.Equal("Opening of shift S2 waits for S1 to close in ERPNext.", sync.OpeningError);
        Assert.Equal(1, report.Failed);   // only S1's closing: S2's opening is waiting, not failed
        Assert.Equal(0, Openings("S2"));
    }
}
