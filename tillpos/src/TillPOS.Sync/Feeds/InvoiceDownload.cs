using System.Globalization;
using System.Text.Json;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>Reading submitted POS Invoices with their item and payment rows (two child-table join queries per batch, like
/// ItemFeed) as remote receipts: shared by the RecentInvoicesFeed and the RemoteReturnsCheck. Read-only.</summary>
public static class InvoiceDownload
{
    public const string Doctype = "POS Invoice";

    public static readonly string[] Fields =
    [
        "docstatus", "posa_client_request_id", "pos_profile", "posting_date", "posting_time", "customer", "grand_total", "rounded_total",
        "net_total", "total_taxes_and_charges", "is_return", "return_against", "discount_amount", "additional_discount_percentage",
    ];

    public static readonly string[] ItemFields = Child("POS Invoice Item",
        "item_code", "item_name", "qty", "uom", "conversion_factor", "rate", "price_list_rate", "amount", "barcode", "item_tax_template",
        "posa_row_id", "idx");

    public static readonly string[] PaymentFields = Child("Sales Invoice Payment", "mode_of_payment", "amount", "idx");

    /// <summary>The invoices of <paramref name="rows"/> (rows of <see cref="Fields"/>) with their lines and payments; no query
    /// when there are none.</summary>
    public static async Task<List<RemoteReceipt>> WithLinesAsync(IErpClient erp, IReadOnlyList<JsonElement> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var names = rows.Select(r => r.Str("name")).ToList();
        var items = await Children(erp, names, ItemFields, ct);
        var payments = await Children(erp, names, PaymentFields, ct);
        return rows.Select(r => Map(r, items[r.Str("name")], payments[r.Str("name")])).ToList();
    }

    /// <summary>A bill of till <paramref name="tillNumber"/> itself (its client id is "TILL{n}-…"): it is on that till already.</summary>
    public static bool IsOwn(JsonElement row, int tillNumber) =>
        row.StrOrNull("posa_client_request_id") is { } id
        && id.StartsWith($"TILL{tillNumber.ToString(CultureInfo.InvariantCulture)}-", StringComparison.OrdinalIgnoreCase);

    /// <summary>One invoice. Item rows without an item code (null or blank: a deleted item, or the null row the child join gives
    /// for an invoice with no item rows) are left out and counted in <see cref="RemoteReceipt.LinesWithoutItemCode"/>; payment
    /// rows without a mode or amount are left out. Never throws for such rows.</summary>
    private static RemoteReceipt Map(JsonElement row, IEnumerable<JsonElement> items, IEnumerable<JsonElement> payments)
    {
        var clientId = row.StrOrNull("posa_client_request_id");
        var all = items.ToList();
        var withCode = all.Where(i => i.StrOrNull("item_code") is not null).ToList();
        var lines = withCode.OrderBy(i => i.Int("idx")).Select(i => new RemoteLine(
            i.StrOrNull("posa_row_id"), i.Str("item_code"), i.StrOrNull("item_name") ?? i.Str("item_code"), i.Dec("qty"),
            i.StrOrNull("uom") ?? "", i.Dec("conversion_factor") is var cf && cf != 0m ? cf : 1m, i.Dec("rate"), i.Dec("price_list_rate"),
            i.Dec("amount"), i.StrOrNull("barcode"), i.StrOrNull("item_tax_template"))).ToList();
        var paid = payments.OrderBy(p => p.Int("idx")).Where(p => p.Dec("amount") != 0m && p.StrOrNull("mode_of_payment") is not null)
            .Select(p => new RemotePayment(p.Str("mode_of_payment"), p.Dec("amount"))).ToList();
        return new RemoteReceipt(row.Str("name"), clientId, TillOf(clientId), row.StrOrNull("pos_profile"), Posting(row),
            row.StrOrNull("customer"), row.Dec("grand_total"), row.Dec("rounded_total"), row.Dec("net_total"), row.Dec("total_taxes_and_charges"),
            row.Bool("is_return"), row.StrOrNull("return_against"), lines, paid)
        {
            DiscountAmount = row.Dec("discount_amount"),
            AdditionalDiscountPercentage = row.Dec("additional_discount_percentage"),
            LinesWithoutItemCode = all.Count - withCode.Count,
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

    private static async Task<ILookup<string, JsonElement>> Children(IErpClient erp, List<string> names, string[] fields, CancellationToken ct)
    {
        var rows = await erp.GetListAsync(new ListQuery(Doctype, ["name", .. fields], [["name", "in", names]], "name asc", 0, 0), ct);
        return rows.ToLookup(r => r.Str("name"));
    }

    private static string[] Child(string table, params string[] fields) => fields.Select(f => $"`tab{table}`.{f} as {f}").ToArray();
}
