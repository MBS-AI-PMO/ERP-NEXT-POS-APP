using System.Net;
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Sync.Upload;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Sync.Upload;

/// <summary>No duplicate on an unknown outcome: the in-flight marker, ERPNext's duplicate answers, and which errors fail a
/// document versus stop the run (FakeErp only).</summary>
public sealed class UnknownOutcomeTests : IDisposable
{
    private static readonly DateTimeOffset Morning = new(2026, 10, 6, 8, 0, 0, TimeSpan.FromHours(4));
    private const string ShiftId = "TILL2-SHIFT-20261006080000";
    private readonly TempDb temp = new();
    private readonly ShiftStore shifts;
    private readonly ReceiptStore receipts;
    private readonly ApprovalStore approvals;
    private readonly FakeErp erp = new();
    private DateTimeOffset clock = new(2026, 10, 6, 12, 0, 0, TimeSpan.FromHours(4));

    public UnknownOutcomeTests()
    {
        shifts = new ShiftStore(temp.Db);
        receipts = new ReceiptStore(temp.Db);
        approvals = new ApprovalStore(temp.Db);
        UploadTestData.ErpComputesTheTillsTotals(erp, receipts);
        shifts.Open(new ShiftOpening(ShiftId, "cashier1", "Al Ain Counter 1", Morning, [new ReceiptPayment("Cash Counter 1", 200m)]));
    }

    public void Dispose() => temp.Dispose();

    private Uploader New() => new(erp, erp, UploadMode.Live, shifts, receipts, approvals, _ => PayloadTests.Counter1, "TILL2",
        "till2@shop.local", () => clock, (_, _) => { });

    private void Sale(string id, int minute) => receipts.Save(UploadTestData.Sale(id, ShiftId, Morning.AddMinutes(60 + minute)));

    private int InvoiceInserts => erp.Inserted.Count(i => i.Doctype == "POS Invoice");

    [Theory]
    [InlineData(504)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(401)]
    [InlineData(500)]
    public async Task A_gateway_timeout_or_auth_error_leaves_the_marker_and_stops_the_run(int status)
    {
        Sale("TILL2-A", 1);
        Sale("TILL2-B", 2);
        erp.LoseInsertAnswer = (doctype, _) => doctype == "POS Invoice" ? new ErpException(status, "Gateway Time-out", null) : null;

        var report = await New().RunOnceAsync();

        Assert.Equal(1, InvoiceInserts);   // B was not tried: the run stopped
        Assert.Equal(new ReceiptSyncInfo(ReceiptSyncStatus.Pending, null, "upload in progress", 0, clock.Add(Uploader.InFlightHold)),
            receipts.SyncInfo("TILL2-A"));
        Assert.Equal(ReceiptSyncStatus.Pending, receipts.SyncInfo("TILL2-B").Status);
        Assert.Equal(0, report.Failed);
        Assert.Contains(report.Problems, p => p.Message.StartsWith("Upload stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_timeout_is_an_unknown_outcome_too()
    {
        Sale("TILL2-A", 1);
        erp.LoseInsertAnswer = (doctype, _) => doctype == "POS Invoice" ? new TaskCanceledException("The request timed out.") : null;

        await New().RunOnceAsync();

        Assert.Equal("upload in progress", receipts.SyncInfo("TILL2-A").LastError);
        clock = clock.Add(Uploader.InFlightHold);
        erp.LoseInsertAnswer = null;
        await New().RunOnceAsync();
        Assert.Equal(1, InvoiceInserts);
        Assert.Equal(ReceiptSyncStatus.Synced, receipts.SyncInfo("TILL2-A").Status);
    }

    [Fact]
    public async Task An_answer_without_a_name_is_an_unknown_outcome()
    {
        Sale("TILL2-A", 1);
        erp.OnInsert = (doctype, _) => doctype == "POS Invoice" ? new Dictionary<string, object?> { ["name"] = null } : null;

        var report = await New().RunOnceAsync();

        Assert.Equal(ReceiptSyncStatus.Pending, receipts.SyncInfo("TILL2-A").Status);
        Assert.Equal("upload in progress", receipts.SyncInfo("TILL2-A").LastError);
        Assert.Equal(0, report.Failed);
    }

    [Theory]
    [InlineData("DuplicateEntryError", "Duplicate entry 'TILL2-A' for key 'posa_client_request_id'", 409)]
    [InlineData("UniqueValidationError", "posa_client_request_id must be unique", 417)]
    [InlineData(null, "POS Invoice with this client id already exists", 417)]
    public async Task A_duplicate_answer_adopts_the_document_already_in_erpnext(string? excType, string message, int status)
    {
        Sale("TILL2-A", 1);
        // ERPNext has the invoice (an earlier insert went through) and refuses the new one as a duplicate.
        erp.LoseInsertAnswer = (doctype, _) => doctype == "POS Invoice" ? new ErpException(status, message, excType) : null;

        var report = await New().RunOnceAsync();

        Assert.Equal(1, InvoiceInserts);
        var info = receipts.SyncInfo("TILL2-A");
        Assert.Equal(ReceiptSyncStatus.Synced, info.Status);
        Assert.Equal("POS-Invoice-00002", info.ErpName);
        Assert.Equal(2, report.Uploaded);   // opening + adopted invoice
        Assert.Equal(0, report.Failed);
    }

    [Fact]
    public async Task A_duplicate_answer_with_nothing_to_adopt_fails_the_document()
    {
        Sale("TILL2-A", 1);
        erp.RejectInsert = (doctype, _) => doctype == "POS Invoice"
            ? new ErpException(409, "Duplicate entry 'X' for key 'name'", "DuplicateEntryError")
            : null;

        var report = await New().RunOnceAsync();

        var info = receipts.SyncInfo("TILL2-A");
        Assert.Equal(ReceiptSyncStatus.Failed, info.Status);
        Assert.Contains("duplicate", info.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, report.Failed);
    }

    [Theory]
    [InlineData(417, "ValidationError", Uploader.ErpOutcome.Refused)]
    [InlineData(417, "MandatoryError", Uploader.ErpOutcome.Refused)]
    [InlineData(417, "LinkValidationError", Uploader.ErpOutcome.Refused)]
    [InlineData(403, "PermissionError", Uploader.ErpOutcome.Refused)]
    [InlineData(417, null, Uploader.ErpOutcome.Refused)]
    [InlineData(409, "DuplicateEntryError", Uploader.ErpOutcome.Duplicate)]
    [InlineData(403, null, Uploader.ErpOutcome.Unknown)]
    [InlineData(401, "AuthenticationError", Uploader.ErpOutcome.Unknown)]
    [InlineData(500, "TypeError", Uploader.ErpOutcome.Unknown)]
    [InlineData(502, null, Uploader.ErpOutcome.Unknown)]
    [InlineData(429, null, Uploader.ErpOutcome.Unknown)]
    public void Errors_are_classified_by_what_they_say_about_the_document(int status, string? excType, Uploader.ErpOutcome expected) =>
        Assert.Equal(expected, Uploader.Classify(new ErpException(status, "message", excType)));

    [Fact]
    public void Non_erpnext_errors_are_unknown_outcomes()
    {
        Assert.Equal(Uploader.ErpOutcome.Unknown, Uploader.Classify(new HttpRequestException("reset", null, HttpStatusCode.BadGateway)));
        Assert.Equal(Uploader.ErpOutcome.Unknown, Uploader.Classify(new TaskCanceledException()));
    }

}

/// <summary>Shared receipts and ERPNext behaviour for the uploader tests.</summary>
internal static class UploadTestData
{
    public static Receipt Sale(string id, string shift, DateTimeOffset at) =>
        new(id, ReceiptKind.Sale, null, shift, "cashier1", at,
            [new ReceiptLine(1, "RICE5", "RICE 5KG", null, "Nos", 1m, 1m, M("10.500"), M("10.500"), M("10.500"), null, null, false, null)],
            M("10.500"), M("10.000"), M("0.500"), M("10.500"), false, 0m, 0m, [new ReceiptPayment("Credit Card", M("10.500"))], 0m, 0m, null)
        {
            CashierUser = "cashier1@shop.local",
        };

    /// <summary>ERPNext computes the invoice totals: the till's own (grand, rounded, paid, change).</summary>
    public static void ErpComputesTheTillsTotals(FakeErp erp, ReceiptStore receipts, Func<decimal>? difference = null) =>
        erp.OnInsert = (doctype, doc) =>
        {
            if (doctype != "POS Invoice") return null;
            var r = receipts.Get(doc.GetProperty("posa_client_request_id").GetString()!)!;
            var d = difference?.Invoke() ?? 0m;
            return new Dictionary<string, object?>
            {
                ["grand_total"] = r.GrandTotal + d,
                ["rounded_total"] = r.UsesErpRoundedTotal ? r.RoundedTotal + d : 0m,
                ["paid_amount"] = r.Payments.Sum(p => p.Amount),
                ["change_amount"] = r.Change,
                ["outstanding_amount"] = 0m,
            };
        };
}
