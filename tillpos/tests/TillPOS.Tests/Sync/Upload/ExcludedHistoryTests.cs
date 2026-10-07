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
        () => goLive.AddHours(5), (_, _) => { });

    private IEnumerable<string> SentIds => erp.Inserted.Select(i =>
        i.Doc.TryGetProperty("posa_client_request_id", out var c) ? c.GetString()! : i.Doc.GetProperty("custom_offline_id").GetString()!);

    [Fact]
    public async Task Switching_to_live_uploads_only_what_comes_after()
    {
        Assert.Equal(2, UploadHistory.EarlierShifts(shifts, kv, goLive));

        Assert.Equal(2, UploadHistory.SwitchToLive(shifts, kv, goLive, includeHistory: false));
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
    public async Task Bills_taken_later_in_an_excluded_open_shift_are_excluded_too()
    {
        Shift("OPEN", goLive.AddHours(-1), close: false);
        UploadHistory.SwitchToLive(shifts, kv, goLive, includeHistory: false);
        receipts.Save(UploadTestData.Sale("OPEN-LATER", "OPEN", goLive.AddHours(1)));

        var report = await New().RunOnceAsync();

        Assert.Empty(erp.Inserted);
        Assert.Equal(ReceiptSyncStatus.Excluded, receipts.SyncInfo("OPEN-LATER").Status);
        Assert.Equal(0, report.Waiting);
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
