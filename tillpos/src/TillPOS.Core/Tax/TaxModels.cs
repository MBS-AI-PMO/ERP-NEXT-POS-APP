namespace TillPOS.Core.Tax;

public sealed record TaxLineInput(decimal Qty, decimal Rate, string? ItemTaxTemplate);

public sealed record TaxLineResult(decimal Amount, decimal NetRate, decimal NetAmount);

public sealed record TaxRowResult(string AccountHead, string Description, decimal Rate, bool IncludedInPrintRate, decimal TaxAmount, decimal Total);

public sealed record BillTotals(
    decimal Total,
    decimal NetTotal,
    decimal TotalTaxes,
    decimal GrandTotal,
    decimal RoundingAdjustment,
    decimal RoundedTotal,
    bool RoundedTotalDisabled,
    IReadOnlyList<TaxLineResult> Lines,
    IReadOnlyList<TaxRowResult> Taxes)
{
    /// <summary>What the customer pays: ERPNext's rounded total, or the grand total when rounding is disabled.</summary>
    public decimal AmountDue => RoundedTotalDisabled ? GrandTotal : RoundedTotal;
}

public sealed class UnsupportedTaxSetupException(string reason)
    : Exception($"Tax setup not supported — contact admin ({reason}).");
