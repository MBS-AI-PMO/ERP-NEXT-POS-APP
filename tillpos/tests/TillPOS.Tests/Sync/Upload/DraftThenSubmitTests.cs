using TillPOS.Core.Catalog;
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Sync.Upload;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Sync.Upload;

/// <summary>Documents go in as drafts, are checked against the till, then submitted; error classes (FakeErp only).</summary>
public sealed class DraftThenSubmitTests : IDisposable
{
    private static readonly DateTimeOffset Morning = new(2026, 10, 6, 8, 0, 0, TimeSpan.FromHours(4));
    private const string ShiftId = "TILL2-SHIFT-20261006080000";
    private readonly TempDb temp = new();
    private readonly ShiftStore shifts;
    private readonly ReceiptStore receipts;
    private readonly ApprovalStore approvals;
    private readonly FakeErp erp = new();
    private readonly List<Exception> logged = [];
    private DateTimeOffset clock = new(2026, 10, 6, 12, 0, 0, TimeSpan.FromHours(4));
    private decimal difference;

    public DraftThenSubmitTests()
    {
        shifts = new ShiftStore(temp.Db);
        receipts = new ReceiptStore(temp.Db);
        approvals = new ApprovalStore(temp.Db);
        UploadTestData.ErpComputesTheTillsTotals(erp, receipts, () => difference);
        shifts.Open(new ShiftOpening(ShiftId, "cashier1", "Al Ain Counter 1", Morning, [new ReceiptPayment("Cash Counter 1", 200m)]));
        receipts.Save(UploadTestData.Sale("TILL2-A", ShiftId, Morning.AddHours(1)));
    }

    public void Dispose() => temp.Dispose();

    private Uploader New(Func<string, PosSettings?>? settings = null) =>
        new(erp, erp, UploadMode.Live, shifts, receipts, approvals, settings ?? (_ => PayloadTests.Counter1), "TILL2", "till2@shop.local",
            () => clock, (_, _) => { })
        {
            LogError = logged.Add,
        };

    private List<(string Doctype, System.Text.Json.JsonElement Doc)> Invoices => erp.Inserted.Where(i => i.Doctype == "POS Invoice").ToList();

    [Fact]
    public async Task Submittable_documents_are_inserted_as_drafts_and_submitted_after_the_check()
    {
        shifts.Close(new ShiftClosing(ShiftId, Morning.AddHours(3), [], 1, 0, 10.5m, 10m, 0.5m));

        await New().RunOnceAsync();

        Assert.All(erp.Inserted, i => Assert.Equal(0, i.Doc.GetProperty("docstatus").GetInt32()));
        Assert.Equal(["POS Opening Shift", "POS Invoice", "POS Closing Shift"], erp.Submitted.Select(s => s.Doctype));
        Assert.Equal(ReceiptSyncStatus.Synced, receipts.SyncInfo("TILL2-A").Status);
        Assert.Equal(UploadStatus.Synced, shifts.SyncInfo(ShiftId)!.ClosingStatus);
    }

    [Fact]
    public async Task A_draft_whose_totals_differ_is_left_unsubmitted_and_failed()
    {
        difference = M("0.10");

        var report = await New().RunOnceAsync();

        Assert.DoesNotContain(erp.Submitted, s => s.Doctype == "POS Invoice");
        var info = receipts.SyncInfo("TILL2-A");
        Assert.Equal(ReceiptSyncStatus.Failed, info.Status);
        Assert.StartsWith("Draft POS-Invoice-00002 created in ERPNext but totals differ — check and submit or delete it.", info.LastError);
        Assert.Equal(1, report.Failed);

        // The retry finds the same draft, checks again, and still neither submits nor inserts.
        clock = clock.AddMinutes(10);
        await New().RunOnceAsync();
        Assert.Single(Invoices);
        Assert.DoesNotContain(erp.Submitted, s => s.Doctype == "POS Invoice");
    }

    [Fact]
    public async Task What_was_paid_must_cover_the_amount_due()
    {
        erp.OnInsert = (doctype, doc) => doctype != "POS Invoice" ? null : new Dictionary<string, object?>
        {
            ["grand_total"] = M("10.500"), ["rounded_total"] = 0m, ["paid_amount"] = M("10.400"), ["change_amount"] = 0m,
        };

        await New().RunOnceAsync();

        var info = receipts.SyncInfo("TILL2-A");
        Assert.Equal(ReceiptSyncStatus.Failed, info.Status);
        Assert.Contains("paid 10.400 (after change) against 10.500 due", info.LastError);
    }

    [Fact]
    public async Task An_outstanding_amount_after_submit_fails_the_bill()
    {
        erp.OnSubmit = (doctype, _) => doctype == "POS Invoice" ? new Dictionary<string, object?> { ["outstanding_amount"] = M("0.500") } : null;

        await New().RunOnceAsync();

        var info = receipts.SyncInfo("TILL2-A");
        Assert.Equal(ReceiptSyncStatus.Failed, info.Status);
        Assert.Contains("0.500 is outstanding after submit", info.LastError);
    }

    [Fact]
    public async Task Our_own_draft_found_by_the_lookup_is_checked_and_submitted_not_inserted_again()
    {
        erp.AddRow("POS Invoice", new()
        {
            ["name"] = "ACC-PSINV-DRAFT", ["docstatus"] = 0m, ["posa_client_request_id"] = "TILL2-A", ["grand_total"] = M("10.500"),
            ["rounded_total"] = 0m, ["paid_amount"] = M("10.500"), ["change_amount"] = 0m, ["outstanding_amount"] = 0m,
        });

        await New().RunOnceAsync();

        Assert.Empty(Invoices);
        Assert.Contains(("POS Invoice", "ACC-PSINV-DRAFT"), erp.Submitted);
        Assert.Equal("ACC-PSINV-DRAFT", receipts.SyncInfo("TILL2-A").ErpName);
    }

    [Fact]
    public async Task Our_own_draft_with_other_totals_is_not_submitted()
    {
        erp.AddRow("POS Invoice", new()
        {
            ["name"] = "ACC-PSINV-DRAFT", ["docstatus"] = 0m, ["posa_client_request_id"] = "TILL2-A", ["grand_total"] = M("99"),
            ["rounded_total"] = 0m, ["paid_amount"] = M("99"), ["change_amount"] = 0m,
        });

        await New().RunOnceAsync();

        Assert.Empty(Invoices);
        Assert.DoesNotContain(erp.Submitted, s => s.Doctype == "POS Invoice");
        Assert.StartsWith("Draft ACC-PSINV-DRAFT created in ERPNext but totals differ", receipts.SyncInfo("TILL2-A").LastError);
    }

    [Fact]
    public async Task A_refused_submit_leaves_the_draft_and_the_retry_submits_it()
    {
        erp.RejectSubmit = (doctype, _) => doctype == "POS Invoice" ? new ErpException(417, "Stock not available for RICE5", "ValidationError") : null;

        await New().RunOnceAsync();

        var info = receipts.SyncInfo("TILL2-A");
        Assert.Equal(ReceiptSyncStatus.Failed, info.Status);
        Assert.Equal("Draft POS-Invoice-00002 is in ERPNext but could not be submitted: Stock not available for RICE5", info.LastError);

        erp.RejectSubmit = null;
        clock = clock.AddMinutes(1);
        await New().RunOnceAsync();
        Assert.Single(Invoices);
        Assert.Equal(ReceiptSyncStatus.Synced, receipts.SyncInfo("TILL2-A").Status);
        Assert.Equal("POS-Invoice-00002", receipts.SyncInfo("TILL2-A").ErpName);
    }

    [Fact]
    public async Task A_refused_lookup_fails_the_document_but_a_gateway_error_only_stops_the_run()
    {
        erp.Fail = q => q.Doctype == "POS Opening Shift" ? new ErpException(417, "Field not permitted in query: custom_offline_id", null) : null;
        var refused = await New().RunOnceAsync();
        Assert.Equal(UploadStatus.Failed, shifts.SyncInfo(ShiftId)!.OpeningStatus);
        Assert.Equal(1, shifts.SyncInfo(ShiftId)!.Attempts);
        Assert.Equal(1, refused.Failed);

        shifts.Retry(ShiftId);
        erp.Fail = q => q.Doctype == "POS Opening Shift" ? new ErpException(502, "Bad Gateway", null) : null;
        var stopped = await New().RunOnceAsync();
        Assert.Equal(new ShiftSyncInfo(UploadStatus.Pending, null, UploadStatus.Pending, null, "Field not permitted in query: custom_offline_id", 0, null),
            shifts.SyncInfo(ShiftId));
        Assert.Equal(0, stopped.Failed);
        Assert.Contains(stopped.Problems, p => p == "Upload stopped: Bad Gateway");
        Assert.Empty(logged);   // ERPNext's answers are not errors of the till
    }

    [Fact]
    public async Task Unexpected_exceptions_are_logged()
    {
        var report = await New(_ => throw new InvalidOperationException("settings broken")).RunOnceAsync();

        Assert.Equal("settings broken", Assert.Single(logged).Message);
        Assert.Contains(report.Problems, p => p == "Upload stopped: settings broken");
    }
}
