using System.Text.Json;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Sync.Upload;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Sync.Upload;

/// <summary>The upload engine against FakeErp (never a real ERPNext).</summary>
public sealed class UploaderTests : IDisposable
{
    private static readonly TimeSpan Uae = TimeSpan.FromHours(4);
    private static readonly DateTimeOffset Morning = new(2026, 10, 6, 8, 0, 0, Uae);
    private const string ShiftId = "TILL2-SHIFT-20261006080000";

    private readonly TempDb temp = new();
    private readonly ShiftStore shifts;
    private readonly ReceiptStore receipts;
    private readonly ApprovalStore approvals;
    private readonly FakeErp erp = new();
    private readonly List<(string Name, string Json)> previews = [];
    private DateTimeOffset clock = new(2026, 10, 6, 12, 0, 0, Uae);
    /// <summary>What ERPNext's grand total differs from the till's by (the total-check tests).</summary>
    private decimal erpDifference;

    public UploaderTests()
    {
        shifts = new ShiftStore(temp.Db);
        receipts = new ReceiptStore(temp.Db);
        approvals = new ApprovalStore(temp.Db);
        // ERPNext computes the invoice totals: here the till's own, plus erpDifference.
        UploadTestData.ErpComputesTheTillsTotals(erp, receipts, () => erpDifference);
    }

    public void Dispose() => temp.Dispose();

    private Uploader New(UploadMode mode = UploadMode.Live) =>
        new(erp, mode == UploadMode.Live ? erp : null, mode, shifts, receipts, approvals,
            profile => profile is "" or "Al Ain Counter 1" ? PayloadTests.Counter1 : null, "TILL2", "till2@shop.local", () => clock,
            (name, json) => previews.Add((name, json)));

    private void OpenShift(string id = ShiftId, DateTimeOffset? at = null) =>
        shifts.Open(new ShiftOpening(id, "cashier1", "Al Ain Counter 1", at ?? Morning, [new ReceiptPayment("Cash Counter 1", 200m)]));

    private void CloseShift(string id = ShiftId, DateTimeOffset? at = null) =>
        shifts.Close(new ShiftClosing(id, at ?? Morning.AddHours(3), [new ShiftModeSummary("Cash Counter 1", 200m, 200m, 200m, 0m)],
            2, 1, 10.5m, 10m, 0.5m));

    private Receipt Sale(string id, int minute, string shift = ShiftId)
    {
        var r = new Receipt(id, ReceiptKind.Sale, null, shift, "cashier1", Morning.AddMinutes(60 + minute),
            [new ReceiptLine(1, "RICE5", "RICE 5KG", null, "Nos", 1m, 1m, M("10.500"), M("10.500"), M("10.500"), null, null, false, null)],
            M("10.500"), M("10.000"), M("0.500"), M("10.500"), false, 0m, 0m, [new ReceiptPayment("Credit Card", M("10.500"))], 0m, 0m, null)
        {
            CashierUser = "cashier1@shop.local",
        };
        receipts.Save(r);
        return r;
    }

    private Receipt Return(string id, string? against, int minute, string shift = ShiftId)
    {
        var r = new Receipt(id, ReceiptKind.Return, against, shift, "cashier1", Morning.AddMinutes(60 + minute),
            [new ReceiptLine(1, "RICE5", "RICE 5KG", null, "Nos", 1m, -1m, M("10.500"), M("10.500"), M("-10.500"), null, null, false, null)],
            M("-10.500"), M("-10.000"), M("-0.500"), M("-10.500"), false, 0m, 0m, [new ReceiptPayment("Credit Card", M("-10.500"))], 0m, 0m,
            "sup1");
        receipts.Save(r);
        return r;
    }

    private IEnumerable<string> InsertedDoctypes => erp.Inserted.Select(i => i.Doctype);
    private List<JsonElement> InsertedInvoices => erp.Inserted.Where(i => i.Doctype == "POS Invoice").Select(i => i.Doc).ToList();
    private static string Str(JsonElement e, string p) => e.GetProperty(p).GetString()!;

    [Fact]
    public async Task Live_uploads_the_opening_then_bills_oldest_first_then_the_closing_then_approvals()
    {
        OpenShift();
        Sale("TILL2-B", 5);
        Sale("TILL2-A", 1);
        Return("TILL2-R", "TILL2-A", 9);
        approvals.Add(new ApprovalRecord("ap1", ApprovalAction.ReturnOverLimit, "cashier1", "sup1", ShiftId, "TILL2-A", null, M("10.5"), "x",
            Morning.AddMinutes(69)));
        CloseShift();

        var report = await New().RunOnceAsync();

        Assert.Equal(["POS Opening Shift", "POS Invoice", "POS Invoice", "POS Invoice", "POS Closing Shift", "TillPOS Approval"], InsertedDoctypes);
        var invoices = InsertedInvoices;
        Assert.Equal(["TILL2-A", "TILL2-B", "TILL2-R"], invoices.Select(i => Str(i, "posa_client_request_id")));
        var openingName = shifts.SyncInfo(ShiftId)!.ErpOpeningName!;
        Assert.All(invoices, i => Assert.Equal(openingName, Str(i, "posa_pos_opening_shift")));
        var saleA = receipts.SyncInfo("TILL2-A").ErpName!;
        Assert.Equal(saleA, Str(invoices[2], "return_against"));
        Assert.Equal("TILL2", Str(invoices[0], "custom_till"));
        Assert.Equal("cashier1@shop.local", Str(invoices[0], "posa_cashier"));
        Assert.Equal("Al Ain Counter 1", Str(invoices[0], "pos_profile"));

        var closing = erp.Inserted.Single(i => i.Doctype == "POS Closing Shift").Doc;
        Assert.Equal(openingName, Str(closing, "pos_opening_shift"));
        Assert.Equal(3, closing.GetProperty("pos_transactions").GetArrayLength());
        var approval = erp.Inserted.Single(i => i.Doctype == "TillPOS Approval").Doc;
        Assert.Equal(openingName, Str(approval, "shift"));
        Assert.Equal(saleA, Str(approval, "invoice"));

        Assert.Equal((6, 0, 0), (report.Uploaded, report.Waiting, report.Failed));
        Assert.Empty(report.Problems);
        Assert.Equal(UploadStatus.Synced, shifts.SyncInfo(ShiftId)!.ClosingStatus);
        Assert.Empty(approvals.Outbox());
        Assert.Equal(0, receipts.CountPending());
    }

    [Fact]
    public async Task Shifts_upload_oldest_first()
    {
        OpenShift("TILL2-SHIFT-1", Morning);
        Sale("TILL2-1", 1, "TILL2-SHIFT-1");
        CloseShift("TILL2-SHIFT-1", Morning.AddHours(1));
        OpenShift("TILL2-SHIFT-2", Morning.AddHours(2));
        Sale("TILL2-2", 130, "TILL2-SHIFT-2");

        await New().RunOnceAsync();

        Assert.Equal(["TILL2-SHIFT-1", "TILL2-1", "TILL2-SHIFT-1", "TILL2-SHIFT-2", "TILL2-2"],
            erp.Inserted.Select(i => i.Doc.TryGetProperty("posa_client_request_id", out var c) ? c.GetString() : Str(i.Doc, "custom_offline_id")));
        Assert.Equal(UploadStatus.Pending, shifts.SyncInfo("TILL2-SHIFT-2")!.ClosingStatus); // still open
    }

    [Fact]
    public async Task A_lost_answer_is_found_by_its_client_id_and_never_inserted_twice()
    {
        OpenShift();
        Sale("TILL2-A", 1);
        erp.LoseInsertAnswer = (doctype, _) => doctype == "POS Invoice" ? new HttpRequestException("connection reset") : null;

        var first = await New().RunOnceAsync();

        Assert.Contains(first.Problems, p => p.Message.Contains("connection reset"));
        // The in-flight marker stays: Pending, no attempt counted, left alone for 5 minutes.
        Assert.Equal(new ReceiptSyncInfo(ReceiptSyncStatus.Pending, null, "upload in progress; no answer: connection reset", 0, clock.AddMinutes(5), 1),
            receipts.SyncInfo("TILL2-A"));
        Assert.Single(InsertedInvoices);

        erp.LoseInsertAnswer = null;
        var calls = erp.ListCalls.Count;
        clock = clock.AddMinutes(4);
        var early = await New().RunOnceAsync();
        Assert.Equal(calls, erp.ListCalls.Count);   // not even looked up yet
        Assert.Contains(early.Problems, p => p.Message.Contains("upload in progress"));
        Assert.Single(InsertedInvoices);

        clock = clock.AddMinutes(1);
        var second = await New().RunOnceAsync();

        Assert.Single(InsertedInvoices); // adopted, not inserted again
        Assert.Equal("POS-Invoice-00002", receipts.SyncInfo("TILL2-A").ErpName);
        Assert.Equal(1, second.Uploaded);
        Assert.Contains(erp.ListCalls, q => q.Doctype == "POS Invoice" && (string)q.Filters[0][0] == "posa_client_request_id"
            && (string)q.Filters[0][2] == "TILL2-A");
    }

    [Fact]
    public async Task Documents_already_in_erpnext_are_adopted_without_any_insert()
    {
        OpenShift();
        Sale("TILL2-A", 1);
        erp.AddRow("POS Opening Shift", new() { ["name"] = "POS-OPE-7", ["docstatus"] = 1, ["custom_offline_id"] = ShiftId });
        erp.AddRow("POS Invoice", new()
        {
            ["name"] = "ACC-PSINV-9", ["docstatus"] = 1, ["posa_client_request_id"] = "TILL2-A", ["grand_total"] = M("10.500"), ["rounded_total"] = 0m,
            ["paid_amount"] = M("10.500"), ["change_amount"] = 0m, ["outstanding_amount"] = 0m,
        });

        var report = await New().RunOnceAsync();

        Assert.Empty(erp.Inserted);
        Assert.Equal("POS-OPE-7", shifts.SyncInfo(ShiftId)!.ErpOpeningName);
        Assert.Equal("ACC-PSINV-9", receipts.SyncInfo("TILL2-A").ErpName);
        Assert.Equal(2, report.Uploaded);
    }

    [Fact]
    public async Task A_cancelled_copy_in_erpnext_is_a_failure_not_a_second_insert()
    {
        OpenShift();
        Sale("TILL2-A", 1);
        erp.AddRow("POS Invoice", new() { ["name"] = "ACC-PSINV-9", ["docstatus"] = 2, ["posa_client_request_id"] = "TILL2-A" });

        var report = await New().RunOnceAsync();

        Assert.Empty(InsertedInvoices);
        var info = receipts.SyncInfo("TILL2-A");
        Assert.Equal(ReceiptSyncStatus.Failed, info.Status);
        Assert.Contains("ACC-PSINV-9", info.LastError);
        Assert.Contains("cancelled", info.LastError);
        Assert.Equal(1, report.Failed);
    }

    [Fact]
    public async Task A_return_waits_while_its_original_sale_is_not_uploaded()
    {
        OpenShift();
        Sale("TILL2-A", 1);
        Return("TILL2-R", "TILL2-A", 5);
        CloseShift();
        erp.RejectInsert = (doctype, doc) => doctype == "POS Invoice" && Str(doc, "posa_client_request_id") == "TILL2-A"
            ? new ErpException(417, "Item RICE5 is disabled", "ValidationError")
            : null;

        var report = await New().RunOnceAsync();

        Assert.Equal(["TILL2-A"], InsertedInvoices.Select(i => Str(i, "posa_client_request_id")));   // the return was not sent
        Assert.Equal(ReceiptSyncStatus.Pending, receipts.SyncInfo("TILL2-R").Status);
        Assert.Contains(report.Problems, p => p.Message.Contains("Return TILL2-R waits for its original sale TILL2-A"));
        Assert.Contains(report.Problems, p => p.Message.Contains("Closing of shift") && p.Message.Contains("2 bill(s)"));
        Assert.DoesNotContain("POS Closing Shift", InsertedDoctypes);
        Assert.Equal(1, report.Failed);
        Assert.Equal(2, report.Waiting); // the return and the closing

        // Once the original goes in, the return follows with return_against, then the closing.
        erp.RejectInsert = null;
        clock = clock.AddMinutes(1);
        await New().RunOnceAsync();
        var returned = InsertedInvoices.Single(i => Str(i, "posa_client_request_id") == "TILL2-R");
        Assert.Equal(receipts.SyncInfo("TILL2-A").ErpName, Str(returned, "return_against"));
        Assert.Contains("POS Closing Shift", InsertedDoctypes);
    }

    [Fact]
    public async Task A_return_of_another_tills_bill_goes_at_once_against_its_ERPNext_name()
    {
        OpenShift();
        Return("TILL2-R", "ACC-PSINV-2026-00042", 5);                  // the original was downloaded from ERPNext (Task 5)
        approvals.Add(new ApprovalRecord("ap1", ApprovalAction.ReturnOldReceipt, "cashier1", "sup1", ShiftId, "ACC-PSINV-2026-00042", null,
            M("10.5"), "x", Morning.AddMinutes(65)));

        var report = await New().RunOnceAsync();

        var returned = InsertedInvoices.Single();
        Assert.Equal("ACC-PSINV-2026-00042", Str(returned, "return_against"));
        Assert.Equal(ReceiptSyncStatus.Synced, receipts.SyncInfo("TILL2-R").Status);
        Assert.Equal("ACC-PSINV-2026-00042", Str(erp.Inserted.Single(i => i.Doctype == "TillPOS Approval").Doc, "invoice"));
        Assert.Empty(report.Problems);
    }

    [Fact]
    public async Task A_return_against_a_till_number_that_is_not_on_this_till_waits()
    {
        OpenShift();
        Return("TILL2-R", "TILL7-20261001100000-000001", 5);

        var report = await New().RunOnceAsync();

        Assert.Empty(InsertedInvoices);
        Assert.Equal(ReceiptSyncStatus.Pending, receipts.SyncInfo("TILL2-R").Status);
        Assert.Contains(report.Problems, p => p.Message.Contains("Return TILL2-R waits for its original sale TILL7-20261001100000-000001"));
    }

    [Fact]
    public async Task A_return_without_a_receipt_does_not_wait()
    {
        OpenShift();
        Sale("TILL2-A", 1);
        Return("TILL2-R", null, 5);
        erp.RejectInsert = (doctype, doc) => doctype == "POS Invoice" && Str(doc, "posa_client_request_id") == "TILL2-A"
            ? new ErpException(417, "Item RICE5 is disabled", "ValidationError")
            : null;

        await New().RunOnceAsync();

        var returned = InsertedInvoices.Single(i => Str(i, "posa_client_request_id") == "TILL2-R");
        Assert.False(returned.TryGetProperty("return_against", out _));
        Assert.Equal(1, returned.GetProperty("is_return").GetInt32());
        Assert.Equal(ReceiptSyncStatus.Synced, receipts.SyncInfo("TILL2-R").Status);
    }

    [Fact]
    public async Task Bills_taken_later_in_an_open_shift_upload_on_the_next_run()
    {
        OpenShift();
        Sale("TILL2-A", 1);
        var uploader = New();
        await uploader.RunOnceAsync();

        Sale("TILL2-B", 2);
        var report = await uploader.RunOnceAsync();

        Assert.Equal(["TILL2-A", "TILL2-B"], InsertedInvoices.Select(i => Str(i, "posa_client_request_id")));
        Assert.Single(erp.Inserted, i => i.Doctype == "POS Opening Shift");
        Assert.Equal(1, report.Uploaded);
    }

    [Fact]
    public async Task The_closing_waits_while_the_shift_is_open()
    {
        OpenShift();
        Sale("TILL2-A", 1);

        await New().RunOnceAsync();
        Assert.DoesNotContain("POS Closing Shift", InsertedDoctypes);

        CloseShift();
        await New().RunOnceAsync();
        Assert.Equal(1, erp.Inserted.Count(i => i.Doctype == "POS Closing Shift"));
        Assert.Single(InsertedInvoices);
    }

    [Fact]
    public async Task A_failed_document_backs_off_30_s_doubling_and_a_retry_resets_it()
    {
        OpenShift();
        Sale("TILL2-A", 1);
        erp.RejectInsert = (doctype, _) => doctype == "POS Invoice" ? new ErpException(417, "Item RICE5 is disabled", "ValidationError") : null;
        var uploader = New();
        var start = clock;

        await uploader.RunOnceAsync();                       // attempt 1 fails → next at +30 s
        Assert.Single(InsertedInvoices);
        Assert.Equal(start.AddSeconds(30), receipts.SyncInfo("TILL2-A").NextAttemptAt);

        clock = start.AddSeconds(29);
        var waiting = await uploader.RunOnceAsync();
        Assert.Single(InsertedInvoices);
        Assert.Contains(waiting.Problems, p => p.Message.Contains("Item RICE5 is disabled") && p.Message.Contains("next try"));

        clock = start.AddSeconds(30);
        await uploader.RunOnceAsync();                       // attempt 2 fails → next at +60 s
        Assert.Equal(2, InsertedInvoices.Count);
        Assert.Equal(clock.AddSeconds(60), receipts.SyncInfo("TILL2-A").NextAttemptAt);

        clock = clock.AddSeconds(59);
        await uploader.RunOnceAsync();
        Assert.Equal(2, InsertedInvoices.Count);

        receipts.Retry("TILL2-A");                           // supervisor retry: at once
        Assert.Equal(new ReceiptSyncInfo(ReceiptSyncStatus.Pending, null, "Item RICE5 is disabled", 0), receipts.SyncInfo("TILL2-A"));
        erp.RejectInsert = null;
        await uploader.RunOnceAsync();
        Assert.Equal(ReceiptSyncStatus.Synced, receipts.SyncInfo("TILL2-A").Status);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 300)]
    [InlineData(50, 300)]
    public void Backoff_doubles_from_30_s_to_at_most_5_min(int attempts, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), Uploader.Backoff(attempts));

    [Fact]
    public async Task A_refusal_records_erpnext_s_message_trimmed_to_300_characters()
    {
        OpenShift();
        Sale("TILL2-A", 1);
        Sale("TILL2-B", 2);
        var body = JsonDocument.Parse("""{"exc_type":"ValidationError","_server_messages":"[\"{\\\"message\\\": \\\"Item <b>RICE5</b> is disabled\\\"}\"]"}""");
        erp.RejectInsert = (doctype, doc) => doctype != "POS Invoice" ? null
            : Str(doc, "posa_client_request_id") == "TILL2-A" ? ErpException.From(417, body.RootElement, "")
            : new ErpException(417, new string('x', 400), null);

        var report = await New().RunOnceAsync();

        Assert.Equal("Item RICE5 is disabled", receipts.SyncInfo("TILL2-A").LastError);
        Assert.Equal(new string('x', 300), receipts.SyncInfo("TILL2-B").LastError);
        Assert.Equal(1, receipts.SyncInfo("TILL2-A").Attempts);
        Assert.Equal(2, report.Failed);
        Assert.Contains(report.Problems, p => p.Message == "Bill TILL2-A: Item RICE5 is disabled");
    }

    [Fact]
    public async Task Erpnext_totals_beyond_the_write_off_limit_fail_and_are_never_inserted_again()
    {
        OpenShift();
        Sale("TILL2-A", 1);
        erpDifference = M("0.10");                            // limit 0.05

        var report = await New().RunOnceAsync();

        var info = receipts.SyncInfo("TILL2-A");
        Assert.Equal(ReceiptSyncStatus.Failed, info.Status);
        Assert.Contains("write-off limit", info.LastError);
        Assert.Contains("10.600", info.LastError);
        Assert.Equal(1, report.Failed);

        clock = clock.AddMinutes(10);                         // after the backoff: looked up again, still different, no insert
        await New().RunOnceAsync();
        Assert.Single(InsertedInvoices);
        Assert.Equal(ReceiptSyncStatus.Failed, receipts.SyncInfo("TILL2-A").Status);
    }

    [Fact]
    public async Task Erpnext_totals_within_the_write_off_limit_are_accepted()
    {
        OpenShift();
        Sale("TILL2-A", 1);
        erpDifference = M("0.01");

        await New().RunOnceAsync();

        Assert.Equal(ReceiptSyncStatus.Synced, receipts.SyncInfo("TILL2-A").Status);
    }

    [Fact]
    public async Task Dry_run_previews_every_document_once_writes_nothing_and_marks_nothing()
    {
        OpenShift();
        Sale("TILL2-A", 1);
        Return("TILL2-R", "TILL2-A", 5);
        approvals.Add(new ApprovalRecord("ap1", ApprovalAction.ReturnOverLimit, "cashier1", "sup1", ShiftId, "TILL2-A", null, 10m, "x", Morning));
        CloseShift();
        var uploader = New(UploadMode.DryRun);

        var report = await uploader.RunOnceAsync();

        Assert.Empty(erp.Inserted);
        var payloads = previews.Where(p => p.Name != Uploader.SummaryFile).ToList();
        Assert.Equal([$"{ShiftId}-opening.json", "TILL2-A.json", "TILL2-R.json", $"{ShiftId}-closing.json", "APPROVAL-ap1.json"],
            payloads.Select(p => p.Name));
        Assert.Equal(Uploader.SummaryFile, previews[^1].Name);
        using (var ret = JsonDocument.Parse(payloads[2].Json))
            Assert.Equal("(new: TILL2-A)", ret.RootElement.GetProperty("return_against").GetString());
        Assert.Contains(erp.ListCalls, q => q.Doctype == "POS Invoice");   // the read-only lookups ran
        Assert.Equal(0, report.Uploaded);
        Assert.Equal(5, report.Waiting);
        Assert.Equal(ReceiptSyncStatus.Pending, receipts.SyncInfo("TILL2-A").Status);
        Assert.Equal(UploadStatus.Pending, shifts.SyncInfo(ShiftId)!.OpeningStatus);

        var lookups = erp.ListCalls.Count;
        await uploader.RunOnceAsync();
        Assert.Equal(5, previews.Count(p => p.Name != Uploader.SummaryFile));   // once per session
        Assert.Equal(2, previews.Count(p => p.Name == Uploader.SummaryFile));   // a summary per run
        Assert.Equal(lookups, erp.ListCalls.Count);
        Assert.Empty(erp.Inserted);
    }

    [Fact]
    public async Task A_bill_handled_by_hand_is_skipped_and_the_closing_goes_without_it()
    {
        OpenShift();
        Sale("TILL2-A", 1);
        Sale("TILL2-B", 2);
        CloseShift();
        receipts.MarkFailed("TILL2-A", "Item disabled", clock.AddHours(1));
        receipts.MarkHandled("TILL2-A", "Handled by sup: fixed by hand");

        var report = await New().RunOnceAsync();

        Assert.Equal(["TILL2-B"], InsertedInvoices.Select(i => Str(i, "posa_client_request_id")));
        var closing = erp.Inserted.Single(i => i.Doctype == "POS Closing Shift").Doc;
        Assert.Equal(1, closing.GetProperty("pos_transactions").GetArrayLength());
        Assert.Equal((0, 0), (report.Waiting, report.Failed));
    }

    [Fact]
    public async Task A_shift_whose_opening_was_handled_by_hand_waits_and_says_why()
    {
        OpenShift();
        Sale("TILL2-A", 1);
        shifts.MarkFailed(ShiftId, ShiftDocument.Opening, "POS Profile not found", clock);
        shifts.MarkHandled(ShiftId, ShiftDocument.Opening, "Handled by sup: opened by hand");

        var report = await New().RunOnceAsync();

        Assert.Empty(erp.Inserted);
        Assert.Contains(report.Problems, p => p.Message.Contains("was handled by hand"));
        Assert.Equal(1, report.Waiting);   // the bill
    }

    [Fact]
    public async Task A_handled_closing_is_final()
    {
        OpenShift();
        Sale("TILL2-A", 1);
        CloseShift();
        shifts.MarkFailed(ShiftId, ShiftDocument.Closing, "bad", clock);
        shifts.MarkHandled(ShiftId, ShiftDocument.Closing, "Handled by sup: closed by hand");

        await New().RunOnceAsync();

        Assert.DoesNotContain("POS Closing Shift", InsertedDoctypes);
        Assert.Equal(UploadStatus.Handled, shifts.SyncInfo(ShiftId)!.ClosingStatus);
    }

    [Fact]
    public async Task A_bill_marked_handled_during_the_run_is_not_sent()
    {
        OpenShift();
        Sale("TILL2-A", 1);
        receipts.MarkFailed("TILL2-A", "Item disabled", null);
        // The supervisor marks it handled after this run read the outbox, just before its insert.
        erp.Fail = q =>
        {
            if (q.Doctype == "POS Invoice") receipts.MarkHandled("TILL2-A", "Handled by sup: fixed by hand");
            return null;
        };

        var report = await New().RunOnceAsync();

        Assert.Empty(InsertedInvoices);
        Assert.Equal(ReceiptSyncStatus.Handled, receipts.SyncInfo("TILL2-A").Status);
        Assert.Contains(report.Problems, p => p.Message.Contains("changed on the till meanwhile"));
    }

    [Fact]
    public async Task Off_does_nothing_at_all()
    {
        OpenShift();
        Sale("TILL2-A", 1);

        var report = await New(UploadMode.Off).RunOnceAsync();

        Assert.Empty(erp.ListCalls);
        Assert.Empty(erp.Inserted);
        Assert.Empty(previews);
        Assert.Equal((0, 2, 0), (report.Uploaded, report.Waiting, report.Failed));
    }

    [Fact]
    public void Only_live_mode_may_hold_a_writer()
    {
        Assert.Throws<ArgumentException>(() => new Uploader(erp, erp, UploadMode.DryRun, shifts, receipts, approvals, _ => null, "TILL2", null,
            () => clock, (_, _) => { }));
        Assert.Throws<ArgumentException>(() => new Uploader(erp, erp, UploadMode.Off, shifts, receipts, approvals, _ => null, "TILL2", null,
            () => clock, (_, _) => { }));
        Assert.Throws<ArgumentNullException>(() => new Uploader(erp, null, UploadMode.Live, shifts, receipts, approvals, _ => null, "TILL2",
            null, () => clock, (_, _) => { }));
    }

    [Fact]
    public async Task Approvals_wait_for_the_shift_they_name_and_go_without_links_when_they_name_none()
    {
        approvals.Add(new ApprovalRecord("login", ApprovalAction.SettingsChange, "", "sup1", "", null, null, 0m, "Change till settings", Morning));
        approvals.Add(new ApprovalRecord("void", ApprovalAction.LineVoid, "cashier1", "sup1", ShiftId, null, "RICE5", 1m, "Remove", Morning));
        OpenShift();
        erp.RejectInsert = (doctype, _) => doctype == "POS Opening Shift" ? new ErpException(417, "POS Profile not found", null) : null;

        await New().RunOnceAsync();

        var sent = Assert.Single(erp.Inserted, i => i.Doctype == "TillPOS Approval").Doc;
        Assert.Equal("login", Str(sent, "custom_offline_id"));
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("shift").ValueKind);
        Assert.Equal("void", Assert.Single(approvals.Outbox()).Record.Id);
    }

    [Fact]
    public async Task Unreachable_erpnext_stops_the_run_and_marks_nothing_failed()
    {
        OpenShift();
        Sale("TILL2-A", 1);
        erp.Fail = _ => new HttpRequestException("No such host is known.");

        var report = await New().RunOnceAsync();

        Assert.Contains(report.Problems, p => p.Message == "Upload stopped: No such host is known.");
        Assert.Equal(0, report.Failed);
        Assert.Equal(2, report.Waiting);
        Assert.Empty(erp.Inserted);
    }

    [Fact]
    public async Task A_shift_whose_counter_settings_are_missing_waits()
    {
        shifts.Open(new ShiftOpening(ShiftId, "cashier1", "Al Ain Counter 9", Morning, []));

        var report = await New().RunOnceAsync();

        Assert.Empty(erp.Inserted);
        Assert.Contains(report.Problems, p => p.Message.Contains("Al Ain Counter 9"));
    }

    [Fact]
    public async Task The_erpnext_user_is_asked_once_when_not_configured()
    {
        OpenShift();
        var uploader = new Uploader(erp, erp, UploadMode.Live, shifts, receipts, approvals, _ => PayloadTests.Counter1, "TILL2", null,
            () => clock, (_, _) => { });

        await uploader.RunOnceAsync();

        Assert.Equal("till1@shop.local", Str(erp.Inserted.Single().Doc, "user"));
    }
}
