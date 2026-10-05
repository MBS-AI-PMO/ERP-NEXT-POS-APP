using TillPOS.Core.Catalog;
using TillPOS.Core.Money;

namespace TillPOS.Core.Tax;

/// <summary>Port of erpnext/controllers/taxes_and_totals.py for "On Net Total" rows:
/// determine_exclusive_rate → calculate_net_total → calculate_taxes →
/// adjust_grand_total_for_inclusive_tax → calculate_totals → set_rounded_total.</summary>
public sealed class TaxCalculator(MoneySettings money, Func<string, ItemTaxTemplate?> findItemTaxTemplate)
{
    private static readonly IReadOnlyDictionary<string, decimal> NoOverrides = new Dictionary<string, decimal>();

    public BillTotals Calculate(IReadOnlyList<TaxLineInput> lines, SalesTaxTemplate? template)
    {
        if (template?.UnsupportedReason is { } reason) throw new UnsupportedTaxSetupException(reason);

        var rows = template?.Rows.OrderBy(r => r.Idx).ToList() ?? [];
        var hasInclusive = rows.Any(r => r.IncludedInPrintRate);
        var overrides = lines
            .Select(l => l.ItemTaxTemplate is null
                ? NoOverrides
                : findItemTaxTemplate(l.ItemTaxTemplate)?.RatesByAccount
                  ?? throw new UnsupportedTaxSetupException($"item tax template '{l.ItemTaxTemplate}' is not in the local catalog"))
            .ToList();

        var n = lines.Count;
        var amount = new decimal[n];
        var netAmount = new decimal[n];
        var netRate = new decimal[n];
        for (var i = 0; i < n; i++)
        {
            amount[i] = R(lines[i].Rate * lines[i].Qty);
            netAmount[i] = amount[i];
            netRate[i] = lines[i].Rate;
        }

        // determine_exclusive_rate
        if (hasInclusive)
        {
            for (var i = 0; i < n; i++)
            {
                if (lines[i].Qty == 0) continue;
                var fraction = rows.Where(r => r.IncludedInPrintRate).Sum(r => RateFor(r, overrides[i]) / 100m);
                if (fraction == 0) continue;
                netAmount[i] = R(amount[i] / (1m + fraction));
                netRate[i] = R(netAmount[i] / lines[i].Qty);
            }
        }

        var total = R(amount.Sum());
        var netTotal = R(netAmount.Sum());

        // calculate_taxes: accumulate unrounded per item, round once at the end
        var taxAmount = new decimal[rows.Count];
        for (var i = 0; i < n; i++)
            for (var t = 0; t < rows.Count; t++)
                taxAmount[t] += RateFor(rows[t], overrides[i]) / 100m * netAmount[i];

        var rowTotal = new decimal[rows.Count];
        for (var t = 0; t < rows.Count; t++)
        {
            taxAmount[t] = R(taxAmount[t]);
            rowTotal[t] = R((t == 0 ? netTotal : rowTotal[t - 1]) + taxAmount[t]);
        }

        // adjust_grand_total_for_inclusive_tax
        var grandTotalDiff = 0m;
        if (hasInclusive)
        {
            var nonInclusive = Enumerable.Range(0, rows.Count).Where(t => !rows[t].IncludedInPrintRate).Sum(t => taxAmount[t]);
            var diff = R(total + nonInclusive - rowTotal[^1]);
            var limit = 5m / Pow10(money.Precision);
            if (diff != 0 && Math.Abs(diff) <= limit) grandTotalDiff = diff;
        }

        // calculate_totals
        var grandTotal = rows.Count > 0 ? R(rowTotal[^1] + grandTotalDiff) : netTotal;
        var totalTaxes = rows.Count > 0 ? R(grandTotal - netTotal - grandTotalDiff) : 0m;

        // set_rounded_total
        decimal roundedTotal = 0m, roundingAdjustment = 0m;
        if (!money.DisableRoundedTotal)
        {
            roundedTotal = Rounder.RoundToSmallestFraction(grandTotal, money);
            roundingAdjustment = R(roundedTotal - grandTotal);
        }

        return new BillTotals(
            total, netTotal, totalTaxes, grandTotal, roundingAdjustment, roundedTotal, money.DisableRoundedTotal,
            Enumerable.Range(0, n).Select(i => new TaxLineResult(amount[i], netRate[i], netAmount[i])).ToList(),
            Enumerable.Range(0, rows.Count).Select(t => new TaxRowResult(
                rows[t].AccountHead, rows[t].Description, rows[t].Rate, rows[t].IncludedInPrintRate, taxAmount[t], rowTotal[t])).ToList());
    }

    private decimal R(decimal value) => Rounder.Round(value, money);

    private static decimal RateFor(TaxRow row, IReadOnlyDictionary<string, decimal> overrides) =>
        overrides.TryGetValue(row.AccountHead, out var rate) ? rate : row.Rate;

    private static decimal Pow10(int p)
    {
        var result = 1m;
        for (var i = 0; i < p; i++) result *= 10m;
        return result;
    }
}
