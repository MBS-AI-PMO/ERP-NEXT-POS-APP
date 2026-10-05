namespace TillPOS.Core.Catalog;

public sealed record Item(string ItemCode, string ItemName, string ItemGroup, string? Brand, string StockUom, bool Disabled, bool IsSalesItem);

public sealed record ItemBarcode(string Barcode, string ItemCode, string? Uom);

public sealed record ItemUom(string ItemCode, string Uom, decimal ConversionFactor);

/// <summary>An Item Price row of the Retail list. A null/blank Uom means the item's stock UOM.</summary>
public sealed record ItemPrice(string Name, string ItemCode, string? Uom, decimal PriceListRate, DateOnly? ValidFrom, DateOnly? ValidUpto);

/// <summary>Item Group tree node with nested-set bounds (a group contains every node whose lft/rgt lie inside its own).</summary>
public sealed record ItemGroupNode(string Name, string? Parent, int Lft, int Rgt);

/// <summary>A row of an Item's or Item Group's "Taxes" table.</summary>
public sealed record ItemTaxAssignment(string ItemTaxTemplate, string? TaxCategory, DateOnly? ValidFrom, int Idx);

/// <summary>Item Tax Template: overrides the tax rate per tax account.</summary>
public sealed record ItemTaxTemplate(string Name, IReadOnlyDictionary<string, decimal> RatesByAccount);

public sealed record TaxRow(int Idx, string AccountHead, string Description, decimal Rate, bool IncludedInPrintRate);

/// <summary>Sales Taxes and Charges Template. UnsupportedReason is set when a row is not "On Net Total".</summary>
public sealed record SalesTaxTemplate(string Name, IReadOnlyList<TaxRow> Rows, string? UnsupportedReason);
