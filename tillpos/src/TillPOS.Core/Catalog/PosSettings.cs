namespace TillPOS.Core.Catalog;

public sealed record PaymentMode(string ModeOfPayment, bool IsDefault);

/// <summary>Values synced from this till's POS Profile, its Company and Currency.</summary>
public sealed record PosSettings(
    string PosProfile,
    string Company,
    string CompanyName,
    string? TaxId,
    string? AddressText,
    string Currency,
    string Warehouse,
    string PriceList,
    string Customer,
    string? TaxesAndCharges,
    string? TaxCategory,
    bool DisableRoundedTotal,
    decimal SmallestCurrencyFraction,
    decimal WriteOffLimit,
    IReadOnlyList<PaymentMode> PaymentModes);
