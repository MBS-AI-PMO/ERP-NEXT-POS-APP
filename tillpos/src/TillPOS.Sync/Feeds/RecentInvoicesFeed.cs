using System.Globalization;
using TillPOS.Data;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>Read-only download of the company's submitted POS Invoices of the last <see cref="Days"/> days, with their items and
/// payments, into the remote receipts, so a bill from another till can be returned here and every till's returns count. Changed
/// invoices only (keyset on modified); for each page, two join queries fetch the item and payment rows (like ItemFeed). This
/// till's own invoices (client id "TILL{n}-…") are skipped: they are on the till already. An invoice cancelled in ERPNext is
/// removed; invoices posted before the window are pruned after every run.</summary>
public sealed class RecentInvoicesFeed(SyncContext ctx, RemoteReceiptStore remote, int tillNumber, Func<DateTimeOffset> now) : ISyncFeed
{
    public const int Days = 30;
    public const string Key = "Recent POS Invoice";

    public string Name => "Recent bills of other tills";

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var company = ctx.Store.LoadPosSettings()?.Company
            ?? throw new InvalidOperationException("POS Profile has not been synced yet, so the company is unknown.");
        var today = DateOnly.FromDateTime(now().DateTime);
        var from = today.AddDays(-Days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var rows = await ctx.Pager.PullAsync(Key, InvoiceDownload.Doctype, InvoiceDownload.Fields,
            [["company", "=", company], ["docstatus", "in", new List<string> { "1", "2" }], ["posting_date", ">=", from]],
            async page =>
            {
                // Cancelled: its quantities are free again. This till's own bills are on the till already.
                var cancelled = page.Where(r => r.Int("docstatus") != 1).Select(r => r.Str("name")).ToList();
                var others = page.Where(r => r.Int("docstatus") == 1 && !InvoiceDownload.IsOwn(r, tillNumber)).ToList();
                var stored = await InvoiceDownload.WithLinesAsync(ctx.Erp, others, ct);
                remote.UpsertMany(stored, now(), cancelled);                    // the whole page, or nothing
            }, ct: ct);
        remote.DeleteOlderThan(Days, today);
        return rows;
    }
}
