using System.Text.Json;
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

/// <summary>A bill ERPNext refuses never holds the queue: its shift still closes, later shifts and bills still go, and the bill is
/// sent later (Retry) with the counter's current open shift, keeping its sale date.</summary>
public sealed class QueueNeverHeldTests : IDisposable
{
    private static readonly TimeSpan Uae = TimeSpan.FromHours(4);
    private static readonly DateTimeOffset Morning = new(2026, 10, 13, 8, 0, 0, Uae);
    private const string Shift1 = "TILL1-SHIFT-1";
    private const string Shift2 = "TILL1-SHIFT-2";

    private readonly TempDb temp = new();
    private readonly ShiftStore shifts;
    private readonly ReceiptStore receipts;
    private readonly ApprovalStore approvals;
    private readonly FakeErp erp = new();
    private DateTimeOffset clock = new(2026, 10, 13, 20, 0, 0, Uae);

    public QueueNeverHeldTests()
    {
        shifts = new ShiftStore(temp.Db);
        receipts = new ReceiptStore(temp.Db);
        approvals = new ApprovalStore(temp.Db);
        UploadTestData.ErpComputesTheTillsTotals(erp, receipts);
    }

    public void Dispose() => temp.Dispose();

    private Uploader New() =>
        new(erp, erp, UploadMode.Live, shifts, receipts, approvals,
            profile => profile is "" or "Al Ain Counter 1" ? PayloadTests.Counter1 : null, "TILL1", "till1@shop.local", () => clock, (_, _) => { });

    private void Open(string id, int hour) =>
        shifts.Open(new ShiftOpening(id, "cashier1", "Al Ain Counter 1", Morning.AddHours(hour), [new ReceiptPayment("Cash Counter 1", 200m)]));

    private void Close(string id, int hour, decimal taxes = 1m) =>
        shifts.Close(new ShiftClosing(id, Morning.AddHours(hour), [new ShiftModeSummary("Cash Counter 1", 200m, 221m, 221m, 0m)],
            2, 0, 21m, 20m, taxes));

    private Receipt Sale(string id, string shift, int minute)
    {
        var r = new Receipt(id, ReceiptKind.Sale, null, shift, "cashier1", Morning.AddMinutes(minute),
            [new ReceiptLine(1, "RICE5", "RICE 5KG", null, "Nos", 1m, 1m, M("10.500"), M("10.500"), M("10.500"), null, null, false, null)],
            M("10.500"), M("10.000"), M("0.500"), M("10.500"), false, 0m, 0m, [new ReceiptPayment("Credit Card", M("10.500"))], 0m, 0m, null);
        receipts.Save(r);
        return r;
    }

    private void Refuse(string clientId) =>
        erp.RejectInsert = (doctype, doc) => doctype == "POS Invoice" && Str(doc, "posa_client_request_id") == clientId
            ? new ErpException(417, "Item RICE5 is disabled", "ValidationError")
            : null;

    private static string Str(JsonElement e, string p) => e.GetProperty(p).GetString()!;
    private List<JsonElement> Invoices => erp.Inserted.Where(i => i.Doctype == "POS Invoice").Select(i => i.Doc).ToList();
    private JsonElement ClosingOf(string shiftId) =>
        erp.Inserted.Single(i => i.Doctype == "POS Closing Shift" && Str(i.Doc, "custom_offline_id") == shiftId).Doc;

    [Fact]
    public async Task A_refused_bill_does_not_hold_its_shifts_closing_or_the_next_shift()
    {
        Open(Shift1, 0);
        Sale("TILL1-A", Shift1, 30);
        Sale("TILL1-B", Shift1, 40);
        Close(Shift1, 3);
        Open(Shift2, 4);
        Sale("TILL1-C", Shift2, 300);
        Refuse("TILL1-A");

        var report = await New().RunOnceAsync();

        Assert.Equal(ReceiptSyncStatus.Failed, receipts.SyncInfo("TILL1-A").Status);
        Assert.Equal(UploadStatus.Synced, shifts.SyncInfo(Shift1)!.ClosingStatus);
        Assert.Equal(UploadStatus.Synced, shifts.SyncInfo(Shift2)!.OpeningStatus);
        Assert.Equal(ReceiptSyncStatus.Synced, receipts.SyncInfo("TILL1-B").Status);
        Assert.Equal(ReceiptSyncStatus.Synced, receipts.SyncInfo("TILL1-C").Status);
        Assert.Contains(report.Problems, p => p.Message.Contains($"Closing of shift {Shift1} goes without 1 failed bill(s)"));
        // The closing lists what is in ERPNext, and carries the till's totals and count for every paid bill.
        var closing = ClosingOf(Shift1);
        Assert.Equal([receipts.SyncInfo("TILL1-B").ErpName], closing.GetProperty("pos_transactions").EnumerateArray().Select(t => Str(t, "pos_invoice")));
        Assert.Equal(21m, closing.GetProperty("grand_total").GetDecimal());
        Assert.Equal(2m, closing.GetProperty("total_quantity").GetDecimal());
        Assert.Equal(221m, closing.GetProperty("payment_reconciliation")[0].GetProperty("closing_amount").GetDecimal());
    }

    [Fact]
    public async Task A_bill_without_an_answer_still_holds_the_closing()
    {
        Open(Shift1, 0);
        Sale("TILL1-A", Shift1, 30);
        Close(Shift1, 3);
        erp.LoseInsertAnswer = (doctype, _) => doctype == "POS Invoice" ? new HttpRequestException("timeout") : null;

        await New().RunOnceAsync();

        Assert.Equal(ReceiptSyncStatus.Pending, receipts.SyncInfo("TILL1-A").Status);   // in flight: it may be in ERPNext already
        Assert.DoesNotContain(erp.Inserted, i => i.Doctype == "POS Closing Shift");
    }

    [Fact]
    public async Task A_retried_bill_goes_with_the_current_shift_keeping_its_sale_date_and_is_in_that_shifts_closing()
    {
        Open(Shift1, 0);
        Sale("TILL1-A", Shift1, 30);
        Close(Shift1, 3);
        Open(Shift2, 4);
        Refuse("TILL1-A");
        await New().RunOnceAsync();

        erp.RejectInsert = null;
        Assert.True(receipts.Retry("TILL1-A"));
        await New().RunOnceAsync();

        var shift2 = shifts.SyncInfo(Shift2)!.ErpOpeningName!;
        var late = Invoices.Last(i => Str(i, "posa_client_request_id") == "TILL1-A");
        Assert.Equal(shift2, Str(late, "posa_pos_opening_shift"));
        Assert.Equal("2026-10-13", Str(late, "posting_date"));
        Assert.Equal("08:30:00", Str(late, "posting_time"));
        Assert.Equal(ReceiptSyncStatus.Synced, receipts.SyncInfo("TILL1-A").Status);

        // Shift 2's closing lists the late bill once, so ERPNext consolidates it with that shift.
        Close(Shift2, 8);
        await New().RunOnceAsync();
        Assert.Equal([receipts.SyncInfo("TILL1-A").ErpName],
            ClosingOf(Shift2).GetProperty("pos_transactions").EnumerateArray().Select(t => Str(t, "pos_invoice")));
    }

    [Fact]
    public async Task A_retried_bill_waits_while_the_counter_has_no_open_shift_in_ERPNext()
    {
        Open(Shift1, 0);
        Sale("TILL1-A", Shift1, 30);
        Close(Shift1, 3);
        Refuse("TILL1-A");
        await New().RunOnceAsync();
        erp.RejectInsert = null;
        receipts.Retry("TILL1-A");

        var report = await New().RunOnceAsync();

        Assert.Equal(ReceiptSyncStatus.Pending, receipts.SyncInfo("TILL1-A").Status);
        Assert.Contains(report.Problems, p => p.Message.Contains("Bill TILL1-A waits for the next shift of Al Ain Counter 1 to open in ERPNext"));
        Assert.Single(Invoices);   // only the refused first try

        // The next shift's opening goes first; the bill follows on the next run (seconds later), with that shift.
        Open(Shift2, 4);
        await New().RunOnceAsync();
        await New().RunOnceAsync();
        Assert.Equal(ReceiptSyncStatus.Synced, receipts.SyncInfo("TILL1-A").Status);
        Assert.Equal(shifts.SyncInfo(Shift2)!.ErpOpeningName, Str(Invoices.Last(), "posa_pos_opening_shift"));
    }

    [Fact]
    public async Task A_draft_left_in_ERPNext_is_rewritten_from_the_bill_then_submitted_never_inserted_twice()
    {
        Open(Shift1, 0);
        Sale("TILL1-A", Shift1, 30);
        Close(Shift1, 3);
        Open(Shift2, 4);
        // The first try left a draft that could not be submitted (e.g. ERPNext rounded it differently).
        erp.RejectSubmit = (doctype, _) => doctype == "POS Invoice" ? new ErpException(417, "Partial Payment in POS Invoice is not allowed.", "ValidationError") : null;
        await New().RunOnceAsync();
        Assert.Equal(ReceiptSyncStatus.Failed, receipts.SyncInfo("TILL1-A").Status);
        Assert.Equal(UploadStatus.Synced, shifts.SyncInfo(Shift1)!.ClosingStatus);   // the closing did not wait for it

        erp.RejectSubmit = null;
        receipts.Retry("TILL1-A");
        await New().RunOnceAsync();

        Assert.Single(Invoices);                                  // no second insert
        var (_, draftName, sent) = Assert.Single(erp.Updated);
        Assert.Equal(shifts.SyncInfo(Shift2)!.ErpOpeningName, Str(sent, "posa_pos_opening_shift"));
        Assert.False(sent.TryGetProperty("docstatus", out _));
        Assert.Equal(ReceiptSyncStatus.Synced, receipts.SyncInfo("TILL1-A").Status);
        Assert.Equal(draftName, receipts.SyncInfo("TILL1-A").ErpName);
    }

    [Fact]
    public async Task A_refused_rewrite_is_a_failure_with_ERPNexts_message()
    {
        Open(Shift1, 0);
        Sale("TILL1-A", Shift1, 30);
        erp.RejectSubmit = (doctype, _) => doctype == "POS Invoice"
            ? new ErpException(417, "Partial Payment in POS Invoice is not allowed.", "ValidationError")
            : null;
        await New().RunOnceAsync();
        receipts.Retry("TILL1-A");
        erp.RejectUpdate = (_, _) => new ErpException(417, "Item RICE5 is disabled", "ValidationError");

        await New().RunOnceAsync();

        var info = receipts.SyncInfo("TILL1-A");
        Assert.Equal(ReceiptSyncStatus.Failed, info.Status);
        Assert.Contains("could not be updated: Item RICE5 is disabled", info.LastError);
    }

    [Fact]
    public void The_closing_vat_is_the_whole_shifts_including_bills_not_in_ERPNext()
    {
        var opening = new ShiftOpening(Shift1, "cashier1", "Al Ain Counter 1", Morning, []);
        var closing = new ShiftClosing(Shift1, Morning.AddHours(3), [], 2, 0, 21m, 20m, 1m);
        var uploadedOnly = Sale("TILL1-B", Shift1, 40);
        var template = new SalesTaxTemplate("VAT", [new TaxRow(1, "VAT 5% - AAML", "VAT", 5m, true)], null);

        var doc = ClosingShiftPayload.Build(opening, closing, "OPEN-1", [("ACC-1", uploadedOnly)], "Al Ain Counter 1", "Co", "u", "Walk-in",
            template, "AED", [uploadedOnly, Sale("TILL1-A", Shift1, 30)],
            [new LinkedInvoice("ACC-1", "2026-10-13", null, 10.5m), new LinkedInvoice("ACC-LATE", "2026-10-12", null, 7m)]);

        var taxes = (List<Dictionary<string, object?>>)doc["taxes"]!;
        Assert.Equal(1m, taxes.Single()["amount"]);                    // the closing's VAT (both bills), not only the uploaded one's
        Assert.Equal(2m, doc["total_quantity"]);
        var listed = ((List<Dictionary<string, object?>>)doc["pos_transactions"]!).Select(t => (string)t["pos_invoice"]!);
        Assert.Equal(["ACC-1", "ACC-LATE"], listed);                    // ACC-1 once, plus the late bill linked to this shift
    }
}
