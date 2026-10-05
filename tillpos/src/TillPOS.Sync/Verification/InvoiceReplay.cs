using System.Globalization;
using System.Text.Json;
using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Tax;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Verification;

public enum ReplayOutcome { Match, Mismatch, Skipped }

public sealed record ReplayField(string Field, decimal Till, decimal Erp)
{
    public bool Ok => Till == Erp;
}

public sealed record ReplayResult(string Invoice, ReplayOutcome Outcome, IReadOnlyList<ReplayField> Fields, string? Reason);

/// <summary>Read-only tax/rounding check against live data: recalculates a submitted ERPNext invoice's
/// totals with the till engine, using the invoice's own lines (qty, rate, item_tax_rate) and tax rows,
/// and compares them with what ERPNext stored. Nothing is written anywhere.</summary>
public static class InvoiceReplay
{
    public static ReplayResult Replay(JsonElement invoice, MoneySettings money)
    {
        var name = invoice.Str("name");
        if (invoice.Dec("discount_amount") != 0 || invoice.Dec("additional_discount_percentage") != 0)
            return Skipped(name, "bill-level discount is not modelled by the till");

        var taxRows = invoice.Rows("taxes").ToList();
        var unsupported = taxRows.Select(t => t.StrOrNull("charge_type") ?? "(blank)").FirstOrDefault(c => c != "On Net Total");
        if (unsupported is not null) return Skipped(name, $"tax charge type '{unsupported}' is not supported");

        var template = taxRows.Count == 0
            ? null
            : new SalesTaxTemplate("(invoice)", taxRows.Select(t => new TaxRow(t.Int("idx"), t.Str("account_head"),
                t.StrOrNull("description") ?? t.Str("account_head"), t.Dec("rate"), t.Bool("included_in_print_rate"))).ToList(), null);

        // ERPNext applies each line's item_tax_rate map (account → rate) when computing tax; mirror it per line.
        var overrides = new Dictionary<string, ItemTaxTemplate>();
        var lines = new List<TaxLineInput>();
        foreach (var (item, index) in invoice.Rows("items").Select((item, index) => (item, index)))
        {
            string? key = null;
            if (item.StrOrNull("item_tax_rate") is { } json && ParseRates(json) is { Count: > 0 } rates)
            {
                key = $"line {index + 1}";
                overrides[key] = new ItemTaxTemplate(key, rates);
            }
            lines.Add(new TaxLineInput(item.Dec("qty"), item.Dec("rate"), key));
        }

        var invoiceMoney = money with { DisableRoundedTotal = invoice.Bool("disable_rounded_total") };
        var totals = new TaxCalculator(invoiceMoney, overrides.GetValueOrDefault).Calculate(lines, template);

        var fields = new List<ReplayField>
        {
            new("net_total", totals.NetTotal, invoice.Dec("net_total")),
            new("total_taxes_and_charges", totals.TotalTaxes, invoice.Dec("total_taxes_and_charges")),
            new("grand_total", totals.GrandTotal, invoice.Dec("grand_total")),
        };
        if (!invoiceMoney.DisableRoundedTotal)
            fields.Add(new("rounded_total", totals.RoundedTotal, invoice.Dec("rounded_total")));

        return new ReplayResult(name, fields.All(f => f.Ok) ? ReplayOutcome.Match : ReplayOutcome.Mismatch, fields, null);
    }

    private static ReplayResult Skipped(string name, string reason) => new(name, ReplayOutcome.Skipped, [], reason);

    private static Dictionary<string, decimal> ParseRates(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            return doc.RootElement.EnumerateObject().ToDictionary(
                p => p.Name,
                p => p.Value.ValueKind == JsonValueKind.Number
                    ? p.Value.GetDecimal()
                    : decimal.Parse(p.Value.GetString() ?? "0", NumberStyles.Number, CultureInfo.InvariantCulture));
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
