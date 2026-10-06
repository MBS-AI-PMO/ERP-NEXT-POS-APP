using System.Globalization;

namespace TillPOS.Core.Sales;

public enum ReceiptKind { Sale, Return }

public sealed record ReceiptLine(
    int LineNo,
    string ItemCode,
    string ItemName,
    string? Barcode,
    string Uom,
    decimal ConversionFactor,
    decimal Qty,
    decimal PriceListRate,
    decimal Rate,
    decimal Amount,
    string? PricingRule,
    string? ItemTaxTemplate,
    bool FromScaleLabel,
    string? UomFallbackFrom);

public sealed record ReceiptPayment(string ModeOfPayment, decimal Amount);

/// <summary>A completed bill (sale or return) as stored on the till and later uploaded.
/// Cash payment rows hold the cash tendered; Change is what was handed back (ERPNext/POS Awesome convention).</summary>
public sealed record Receipt(
    string ClientId,
    ReceiptKind Kind,
    string? ReturnAgainst,
    string ShiftClientId,
    string Cashier,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ReceiptLine> Lines,
    decimal Total,
    decimal NetTotal,
    decimal TotalTaxes,
    decimal GrandTotal,
    bool UsesErpRoundedTotal,
    decimal RoundedTotal,
    decimal RoundingAdjustment,
    IReadOnlyList<ReceiptPayment> Payments,
    decimal Change,
    decimal RoundingDifference,
    string? ApprovedBy)
{
    /// <summary>The cashier's ERPNext user (uploaded as posa_cashier).</summary>
    public string? CashierUser { get; init; }
    /// <summary>Why the items were returned (returns only).</summary>
    public string? Reason { get; init; }
}

/// <summary>The POS Profile payment modes the till uses for cash and card.</summary>
public sealed record TenderModes(string Cash, string Card);

public static class ClientIds
{
    public static string Receipt(int till, DateTimeOffset at, long sequence) =>
        $"TILL{till.ToString(CultureInfo.InvariantCulture)}-{Stamp(at)}-{sequence.ToString("000000", CultureInfo.InvariantCulture)}";

    public static string Shift(int till, DateTimeOffset at) =>
        $"TILL{till.ToString(CultureInfo.InvariantCulture)}-SHIFT-{Stamp(at)}";

    private static string Stamp(DateTimeOffset at) => at.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
}

public interface IReceiptStore
{
    long NextSequence();
    void Save(Receipt receipt);
    Receipt? Get(string clientId);
    IReadOnlyList<Receipt> ReturnsAgainst(string clientId);
    IReadOnlyList<Receipt> ByShift(string shiftClientId);
}
