using System.Globalization;
using System.Text.Json;
using TillPOS.Data;
using TillPOS.Erp;
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
    private const string Doctype = "POS Invoice";

    private static readonly string[] Fields =
    [
        "docstatus", "posa_client_request_id", "pos_profile", "posting_date", "posting_time", "customer", "grand_total", "rounded_total",
        "net_total", "total_taxes_and_charges", "is_return", "return_against", "discount_amount", "additional_discount_percentage",
    ];

    private static readonly string[] ItemFields = Child("POS Invoice Item",
        "item_code", "item_name", "qty", "uom", "conversion_factor", "rate", "price_list_rate", "amount", "barcode", "item_tax_template",
        "posa_row_id", "idx");

    private static readonly string[] PaymentFields = Child("Sales Invoice Payment", "mode_of_payment", "amount", "idx");

    public string Name => "Recent bills of other tills";

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var company = ctx.Store.LoadPosSettings()?.Company
            ?? throw new InvalidOperationException("POS Profile has not been synced yet, so the company is unknown.");
        var today = DateOnly.FromDateTime(now().DateTime);
        var from = today.AddDays(-Days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var own = $"TILL{tillNumber.ToString(CultureInfo.InvariantCulture)}-";

        var rows = await ctx.Pager.PullAsync(Key, Doctype, Fields,
            [["company", "=", company], ["docstatus", "in", new List<string> { "1", "2" }], ["posting_date", ">=", from]],
            async page =>
            {
                var names = page.Select(r => r.Str("name")).ToList();
                var items = await Children(names, ItemFields, ct);
                var payments = await Children(names, PaymentFields, ct);
                var fetched = now();
                foreach (var row in page)
                {
                    var name = row.Str("name");
                    if (row.Int("docstatus") != 1)
                    {
                        remote.Delete(name);                                    // cancelled: its quantities are free again
                        continue;
                    }
                    if (row.StrOrNull("posa_client_request_id") is { } id && id.StartsWith(own, StringComparison.OrdinalIgnoreCase)) continue;
                    remote.Upsert(Map(row, items[name], payments[name]), fetched);
                }
            }, ct: ct);
        remote.DeleteOlderThan(Days, today);
        return rows;
    }

    private static RemoteReceipt Map(JsonElement row, IEnumerable<JsonElement> items, IEnumerable<JsonElement> payments)
    {
        var clientId = row.StrOrNull("posa_client_request_id");
        var lines = items.OrderBy(i => i.Int("idx")).Select(i => new RemoteLine(
            i.StrOrNull("posa_row_id"), i.Str("item_code"), i.StrOrNull("item_name") ?? i.Str("item_code"), i.Dec("qty"),
            i.StrOrNull("uom") ?? "", i.Dec("conversion_factor") is var cf && cf != 0m ? cf : 1m, i.Dec("rate"), i.Dec("price_list_rate"),
            i.Dec("amount"), i.StrOrNull("barcode"), i.StrOrNull("item_tax_template"))).ToList();
        var paid = payments.OrderBy(p => p.Int("idx")).Where(p => p.Dec("amount") != 0m)
            .Select(p => new RemotePayment(p.Str("mode_of_payment"), p.Dec("amount"))).ToList();
        return new RemoteReceipt(row.Str("name"), clientId, TillOf(clientId), row.StrOrNull("pos_profile"), Posting(row),
            row.StrOrNull("customer"), row.Dec("grand_total"), row.Dec("rounded_total"), row.Dec("net_total"), row.Dec("total_taxes_and_charges"),
            row.Bool("is_return"), row.StrOrNull("return_against"), lines, paid)
        {
            DiscountAmount = row.Dec("discount_amount"),
            AdditionalDiscountPercentage = row.Dec("additional_discount_percentage"),
        };
    }

    /// <summary>"TILL3" for a TillPOS client id ("TILL3-…"), else null (e.g. a POS Awesome counter's bill).</summary>
    private static string? TillOf(string? clientId) =>
        clientId is { } id && id.StartsWith("TILL", StringComparison.OrdinalIgnoreCase) && id.IndexOf('-', StringComparison.Ordinal) is > 4 and var dash
            ? id[..dash].ToUpperInvariant()
            : null;

    /// <summary>posting_date plus posting_time ("9:05:03.123456" or "09:05:03"), to the second.</summary>
    private static DateTime Posting(JsonElement row)
    {
        var date = row.Date("posting_date") ?? throw new FormatException($"POS Invoice {row.Str("name")} has no posting date.");
        var time = row.StrOrNull("posting_time") is { } text && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var t) ? t : TimeSpan.Zero;
        return date.ToDateTime(TimeOnly.MinValue).AddSeconds(Math.Floor(time.TotalSeconds));
    }

    private async Task<ILookup<string, JsonElement>> Children(List<string> names, string[] fields, CancellationToken ct)
    {
        var rows = await ctx.Erp.GetListAsync(new ListQuery(Doctype, ["name", .. fields], [["name", "in", names]], "name asc", 0, 0), ct);
        return rows.ToLookup(r => r.Str("name"));
    }

    private static string[] Child(string table, params string[] fields) => fields.Select(f => $"`tab{table}`.{f} as {f}").ToArray();
}
