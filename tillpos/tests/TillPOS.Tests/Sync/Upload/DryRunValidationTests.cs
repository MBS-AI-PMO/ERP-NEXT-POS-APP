using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Sync.Upload;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Sync.Upload;

/// <summary>DryRun checks every reference of a payload read-only, reports problems, and writes nothing (FakeErp only).</summary>
public sealed class DryRunValidationTests : IDisposable
{
    private static readonly DateTimeOffset Morning = new(2026, 10, 6, 8, 0, 0, TimeSpan.FromHours(4));
    private const string ShiftId = "TILL2-SHIFT-20261006080000";
    private readonly TempDb temp = new();
    private readonly ShiftStore shifts;
    private readonly ReceiptStore receipts;
    private readonly ApprovalStore approvals;
    private readonly FakeErp erp = new();
    private readonly List<(string Name, string Text)> files = [];
    private readonly DateTimeOffset clock = new(2026, 10, 6, 12, 0, 0, TimeSpan.FromHours(4));

    public DryRunValidationTests()
    {
        shifts = new ShiftStore(temp.Db);
        receipts = new ReceiptStore(temp.Db);
        approvals = new ApprovalStore(temp.Db);
        shifts.Open(new ShiftOpening(ShiftId, "cashier1", "Al Ain Counter 1", Morning, [new ReceiptPayment("Cash Counter 1", 200m)]));

        // A live ERPNext where everything the payloads name exists.
        erp.Docs[("POS Profile", "Al Ain Counter 1")] = new Dictionary<string, object?>
        {
            ["name"] = "Al Ain Counter 1", ["account_for_change_amount"] = "Cash - AAML", ["write_off_account"] = "Write Off - AAML",
        };
        Item("RICE5", "Nos", ["Nos", "Box"]);
        foreach (var (doctype, name) in new[]
        {
            ("Customer", "Walk-in Customer"), ("Warehouse", "Stores - AAML"), ("Mode of Payment", "Cash Counter 1"),
            ("Mode of Payment", "Credit Card"), ("Sales Taxes and Charges Template", "UAE VAT 5% - AAML"),
        })
            erp.AddRow(doctype, new() { ["name"] = name });
    }

    public void Dispose() => temp.Dispose();

    private void Item(string code, string stockUom, string[] uoms, bool disabled = false) =>
        erp.Docs[("Item", code)] = new Dictionary<string, object?>
        {
            ["name"] = code, ["disabled"] = disabled ? 1 : 0, ["stock_uom"] = stockUom,
            ["uoms"] = uoms.Select(u => new Dictionary<string, object?> { ["uom"] = u, ["conversion_factor"] = 1 }).ToList(),
        };

    private Uploader New() => new(erp, null, UploadMode.DryRun, shifts, receipts, approvals, _ => PayloadTests.Counter1, "TILL2",
        "till2@shop.local", () => clock, (name, text) => files.Add((name, text)));

    private void Bill(string id, string code = "RICE5", string uom = "Nos", string mode = "Credit Card", decimal paid = 10.5m,
        decimal change = 0m, bool rounded = false, decimal roundedTotal = 0m) =>
        receipts.Save(UploadTestData.Sale(id, ShiftId, Morning.AddHours(1)) with
        {
            Lines = [new ReceiptLine(1, code, code, null, uom, 1m, 1m, M("10.500"), M("10.500"), M("10.500"), null, null, false, null)],
            Payments = [new ReceiptPayment(mode, paid)],
            Change = change,
            UsesErpRoundedTotal = rounded,
            RoundedTotal = roundedTotal,
        });

    private static IEnumerable<string> Lines(UploadReport report, string docId) =>
        report.Problems.Where(p => p.DocId == docId).Select(p => $"{p.Field}: {p.Message}");

    [Fact]
    public async Task A_clean_bill_has_no_problems_and_nothing_is_written()
    {
        Bill("TILL2-A");

        var report = await New().RunOnceAsync();

        Assert.Empty(report.Problems);
        Assert.Empty(erp.Inserted);
        Assert.Empty(erp.Submitted);
        Assert.Empty(erp.Calls);
        Assert.Contains(files, f => f.Name == "TILL2-A.json");
    }

    [Fact]
    public async Task Missing_or_disabled_items_and_units_are_reported_with_their_field()
    {
        Item("SOAP", "Nos", ["Nos"], disabled: true);
        Bill("TILL2-A", code: "NOPE");
        Bill("TILL2-B", code: "SOAP");
        Bill("TILL2-C", uom: "Kg");

        var report = await New().RunOnceAsync();

        Assert.Contains("items[1].item_code: Bill TILL2-A: items[1].item_code: Item NOPE does not exist", Lines(report, "TILL2-A"));
        Assert.Contains("items[1].item_code: Bill TILL2-B: items[1].item_code: Item SOAP is disabled", Lines(report, "TILL2-B"));
        Assert.Contains("items[1].uom: Bill TILL2-C: items[1].uom: UOM Kg is not set up on item RICE5", Lines(report, "TILL2-C"));
        Assert.Equal(3, files.Count(f => f.Name is "TILL2-A.json" or "TILL2-B.json" or "TILL2-C.json"));   // previews are still written
        Assert.Empty(erp.Inserted);
    }

    [Fact]
    public async Task A_unit_that_is_the_stock_unit_needs_no_uom_row()
    {
        Item("MILK", "PCS", []);
        Bill("TILL2-A", code: "MILK", uom: "pcs");

        var report = await New().RunOnceAsync();

        Assert.Empty(report.Problems);
    }

    [Fact]
    public async Task Missing_warehouse_customer_tax_template_and_payment_mode_are_reported()
    {
        erp.Override = q => q.Doctype is "Warehouse" or "Customer" or "Sales Taxes and Charges Template" ? [] : null;
        Bill("TILL2-A", mode: "Gift Card");

        var report = await New().RunOnceAsync();

        var lines = Lines(report, "TILL2-A").ToList();
        Assert.Contains(lines, l => l.StartsWith("set_warehouse:", StringComparison.Ordinal) && l.EndsWith("Warehouse Stores - AAML does not exist", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("customer:", StringComparison.Ordinal) && l.EndsWith("Customer Walk-in Customer does not exist", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("taxes_and_charges:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("payments[1].mode_of_payment:", StringComparison.Ordinal) && l.EndsWith("Mode of Payment Gift Card does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_pos_profile_must_exist_and_have_the_accounts_a_bill_needs()
    {
        erp.Docs[("POS Profile", "Al Ain Counter 1")] = new Dictionary<string, object?> { ["name"] = "Al Ain Counter 1" };
        Bill("TILL2-CHANGE", mode: "Cash Counter 1", paid: 20m, change: M("9.500"));               // gives change
        Bill("TILL2-WRITEOFF", mode: "Cash Counter 1", paid: M("10.25"), rounded: false);         // paid below the total

        var report = await New().RunOnceAsync();

        Assert.Contains(Lines(report, "TILL2-CHANGE"), l => l.StartsWith("account_for_change_amount:", StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(report, "TILL2-CHANGE"), l => l.StartsWith("write_off_account:", StringComparison.Ordinal));
        Assert.Contains(Lines(report, "TILL2-WRITEOFF"), l => l.StartsWith("write_off_account:", StringComparison.Ordinal) && l.Contains("0.250"));

        erp.Docs.Remove(("POS Profile", "Al Ain Counter 1"));
        var missing = await New().RunOnceAsync();
        Assert.Contains(Lines(missing, ShiftId), l => l == "pos_profile: Opening of shift TILL2-SHIFT-20261006080000: pos_profile: POS Profile Al Ain Counter 1 does not exist");
    }

    [Fact]
    public async Task A_document_already_in_erpnext_is_looked_up_once_per_session()
    {
        Bill("TILL2-A");
        erp.AddRow("POS Invoice", new() { ["name"] = "ACC-1", ["docstatus"] = 1m, ["posa_client_request_id"] = "TILL2-A" });
        var uploader = New();

        await uploader.RunOnceAsync();
        var lookups = erp.ListCalls.Count(q => q.Doctype == "POS Invoice");
        await uploader.RunOnceAsync();

        Assert.Equal(1, lookups);
        Assert.Equal(1, erp.ListCalls.Count(q => q.Doctype == "POS Invoice"));
        Assert.DoesNotContain(files, f => f.Name == "TILL2-A.json");
    }

    [Fact]
    public async Task Each_distinct_document_is_read_once_per_run()
    {
        Bill("TILL2-A");
        Bill("TILL2-B");
        Bill("TILL2-C");

        await New().RunOnceAsync();

        Assert.Equal(1, erp.DocCalls.Count(c => c == ("Item", "RICE5")));
        Assert.Equal(1, erp.DocCalls.Count(c => c == ("POS Profile", "Al Ain Counter 1")));
        Assert.Equal(1, erp.ListCalls.Count(q => q.Doctype == "Customer"));
        Assert.Equal(1, erp.ListCalls.Count(q => q.Doctype == "Mode of Payment" && (string)q.Filters[0][2] == "Credit Card"));
    }

    [Fact]
    public async Task A_refused_opening_lookup_is_only_reported_and_the_bills_are_still_previewed()
    {
        Bill("TILL2-A");
        erp.Fail = q => q.Doctype == "POS Opening Shift" ? new ErpException(417, "Field not permitted in query: custom_offline_id", null) : null;

        var report = await New().RunOnceAsync();

        Assert.Contains(report.Problems, p => p.DocId == ShiftId && p.Message.Contains("lookup failed: Field not permitted"));
        Assert.Equal(new ShiftSyncInfo(UploadStatus.Pending, null, UploadStatus.Pending, null, null, 0, null), shifts.SyncInfo(ShiftId));
        Assert.Equal(0, report.Failed);
        var bill = Assert.Single(files, f => f.Name == "TILL2-A.json");
        Assert.Contains($"\"posa_pos_opening_shift\": \"(new: {ShiftId})\"", bill.Text);
    }

    [Fact]
    public async Task Dry_run_ignores_backoff_and_never_marks_a_failure()
    {
        Bill("TILL2-A", code: "NOPE");
        receipts.MarkFailed("TILL2-A", "earlier live failure", clock.AddHours(1));

        await New().RunOnceAsync();

        Assert.Contains(files, f => f.Name == "TILL2-A.json");
        Assert.Equal(new ReceiptSyncInfo(ReceiptSyncStatus.Failed, null, "earlier live failure", 1, clock.AddHours(1)), receipts.SyncInfo("TILL2-A"));
    }

    [Fact]
    public async Task Every_run_writes_a_summary_and_keeps_the_session_problems()
    {
        Bill("TILL2-A", code: "NOPE");
        var uploader = New();

        await uploader.RunOnceAsync();
        var second = await uploader.RunOnceAsync();

        Assert.Contains(second.Problems, p => p.DocId == "TILL2-A" && p.Field == "items[1].item_code");
        var summaries = files.Where(f => f.Name == Uploader.SummaryFile).ToList();
        Assert.Equal(2, summaries.Count);
        Assert.StartsWith("TillPOS dry run 2026-10-06 12:00:00: nothing was sent to ERPNext.", summaries[1].Text);
        Assert.Contains("Waiting: 2  Failed: 0  Previewed this session: 2  Problems: 1", summaries[1].Text);
        Assert.Contains("- Bill TILL2-A: items[1].item_code: Item NOPE does not exist", summaries[1].Text);
    }
}
