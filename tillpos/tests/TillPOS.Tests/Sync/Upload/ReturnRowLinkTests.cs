using System.Text.Json;
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Sync.Upload;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Sync.Upload;

/// <summary>ERPNext refuses a return line it cannot link to its row on the original POS Invoice: each return line carries
/// pos_invoice_item, read from the original (read-only) before the insert (FakeErp only).</summary>
public sealed class ReturnRowLinkTests : IDisposable
{
    private static readonly DateTimeOffset Morning = new(2026, 10, 6, 8, 0, 0, TimeSpan.FromHours(4));
    private const string ShiftId = "TILL2-SHIFT-20261006080000";
    private const string Original = "ACC-PSINV-2026-00042";
    private readonly TempDb temp = new();
    private readonly ShiftStore shifts;
    private readonly ReceiptStore receipts;
    private readonly ApprovalStore approvals;
    private readonly FakeErp erp = new();
    private readonly List<(string Name, string Text)> previews = [];
    private readonly DateTimeOffset clock = new(2026, 10, 6, 12, 0, 0, TimeSpan.FromHours(4));

    public ReturnRowLinkTests()
    {
        shifts = new ShiftStore(temp.Db);
        receipts = new ReceiptStore(temp.Db);
        approvals = new ApprovalStore(temp.Db);
        UploadTestData.ErpComputesTheTillsTotals(erp, receipts);
        shifts.Open(new ShiftOpening(ShiftId, "cashier1", "Al Ain Counter 1", Morning, [new ReceiptPayment("Cash Counter 1", 200m)]));
    }

    public void Dispose() => temp.Dispose();

    private Uploader New(UploadMode mode = UploadMode.Live) =>
        new(erp, mode == UploadMode.Live ? erp : null, mode, shifts, receipts, approvals, _ => PayloadTests.Counter1, "TILL2", "till2@shop.local",
            () => clock, (name, text) => previews.Add((name, text)));

    /// <summary>The original invoice in ERPNext with these item rows (name, item, posa_row_id).</summary>
    private void OriginalHas(params (string Name, string Item, string RowId)[] rows) =>
        erp.Docs[("POS Invoice", Original)] = new Dictionary<string, object?>
        {
            ["name"] = Original,
            ["items"] = rows.Select(r => new Dictionary<string, object?> { ["name"] = r.Name, ["item_code"] = r.Item, ["posa_row_id"] = r.RowId, ["qty"] = 1 })
                .ToList(),
        };

    /// <summary>A return against <paramref name="against"/> with RICE5 on the given line numbers.</summary>
    private void ReturnOf(string against, params int[] lineNos) =>
        receipts.Save(new Receipt("TILL2-R", ReceiptKind.Return, against, ShiftId, "cashier1", Morning.AddHours(2),
            lineNos.Select(n => new ReceiptLine(n, "RICE5", "RICE 5KG", null, "Nos", 1m, -1m, M("10.500"), M("10.500"), M("-10.500"), null, null,
                false, null)).ToList(),
            -10.5m * lineNos.Length, -10m * lineNos.Length, -0.5m * lineNos.Length, -10.5m * lineNos.Length, false, 0m, 0m,
            [new ReceiptPayment("Credit Card", -10.5m * lineNos.Length)], 0m, 0m, "sup1"));

    private List<JsonElement> ReturnLines() =>
        erp.Inserted.Single(i => i.Doctype == "POS Invoice").Doc.GetProperty("items").EnumerateArray().ToList();

    [Fact]
    public async Task A_return_line_links_to_the_original_row_with_its_line_number()
    {
        OriginalHas(("row-a", "RICE5", "7"), ("row-b", "RICE5", "1"));
        ReturnOf(Original, 1);

        await New().RunOnceAsync();

        Assert.Equal("row-b", Assert.Single(ReturnLines()).GetProperty("pos_invoice_item").GetString());
        Assert.Equal(ReceiptSyncStatus.Synced, receipts.SyncInfo("TILL2-R").Status);
        Assert.Contains(("POS Invoice", Original), erp.DocCalls);
    }

    [Fact]
    public async Task Without_a_matching_line_number_the_items_rows_are_used_in_order()
    {
        OriginalHas(("row-a", "MILK", "x1"), ("row-b", "RICE5", "x2"), ("row-c", "RICE5", "x3"));
        ReturnOf(Original, 4, 5);

        await New().RunOnceAsync();

        Assert.Equal(["row-b", "row-c"], ReturnLines().Select(l => l.GetProperty("pos_invoice_item").GetString()));
    }

    [Fact]
    public async Task A_return_line_that_is_not_on_the_original_fails_and_is_never_inserted()
    {
        OriginalHas(("row-a", "MILK", "1"));
        ReturnOf(Original, 1);

        var report = await New().RunOnceAsync();

        Assert.DoesNotContain(erp.Inserted, i => i.Doctype == "POS Invoice");
        var info = receipts.SyncInfo("TILL2-R");
        Assert.Equal(ReceiptSyncStatus.Failed, info.Status);
        Assert.Equal($"Line 1 (RICE5) is not on {Original} in ERPNext — check before retrying", info.LastError);
        Assert.Equal(1, report.Failed);
    }

    [Fact]
    public async Task An_original_erpnext_does_not_have_fails_the_return()
    {
        ReturnOf(Original, 1);   // no such invoice in FakeErp: 404

        await New().RunOnceAsync();

        Assert.DoesNotContain(erp.Inserted, i => i.Doctype == "POS Invoice");
        Assert.StartsWith($"Original {Original} could not be read in ERPNext", receipts.SyncInfo("TILL2-R").LastError);
    }

    [Fact]
    public async Task A_network_error_reading_the_original_leaves_the_return_pending()
    {
        OriginalHas(("row-a", "RICE5", "1"));
        ReturnOf(Original, 1);
        erp.FailDoc = (doctype, _) => doctype == "POS Invoice" ? new HttpRequestException("connection reset") : null;

        var report = await New().RunOnceAsync();

        Assert.DoesNotContain(erp.Inserted, i => i.Doctype == "POS Invoice");
        Assert.Equal(new ReceiptSyncInfo(ReceiptSyncStatus.Pending, null, null, 0), receipts.SyncInfo("TILL2-R"));
        Assert.Contains(report.Problems, p => p.Message == "Upload stopped: connection reset");
        Assert.Equal(0, report.Failed);
    }

    [Fact]
    public async Task A_return_against_this_tills_own_sale_links_to_the_row_erpnext_created()
    {
        receipts.Save(UploadTestData.Sale("TILL2-A", ShiftId, Morning.AddHours(1)));
        ReturnOf("TILL2-A", 1);

        await New().RunOnceAsync();

        var sale = receipts.SyncInfo("TILL2-A").ErpName!;
        var ret = erp.Inserted.Last(i => i.Doctype == "POS Invoice").Doc;
        Assert.Equal(sale, ret.GetProperty("return_against").GetString());
        Assert.Equal($"{sale}-items-1", ret.GetProperty("items")[0].GetProperty("pos_invoice_item").GetString());
    }

    [Fact]
    public async Task A_return_without_a_receipt_reads_nothing_and_links_nothing()
    {
        ReturnOf(null!, 1);

        await New().RunOnceAsync();

        Assert.Empty(erp.DocCalls);
        Assert.False(Assert.Single(ReturnLines()).TryGetProperty("pos_invoice_item", out _));
    }

    [Fact]
    public async Task Dry_run_reports_an_unmatched_line_and_writes_nothing()
    {
        OriginalHas(("row-a", "MILK", "1"));
        ReturnOf(Original, 1);

        var report = await New(UploadMode.DryRun).RunOnceAsync();

        Assert.Empty(erp.Inserted);
        Assert.Contains(report.Problems, p => p.DocId == "TILL2-R" && p.Field == "items[1].pos_invoice_item"
            && p.Message.EndsWith($"Line 1 (RICE5) is not on {Original} in ERPNext — check before retrying", StringComparison.Ordinal));
        Assert.Equal(ReceiptSyncStatus.Pending, receipts.SyncInfo("TILL2-R").Status);
        Assert.Contains(previews, p => p.Name == "TILL2-R.json");
    }

    [Fact]
    public async Task Dry_run_previews_the_linked_rows()
    {
        OriginalHas(("row-b", "RICE5", "1"));
        ReturnOf(Original, 1);

        await New(UploadMode.DryRun).RunOnceAsync();

        Assert.Contains("\"pos_invoice_item\": \"row-b\"", previews.Single(p => p.Name == "TILL2-R.json").Text);
    }
}
