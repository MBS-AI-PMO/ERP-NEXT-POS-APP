using TillPOS.Core.Pricing;

namespace TillPOS.Core.Catalog;

/// <summary>Read-only view of the local catalog used by pricing and the cart.</summary>
public interface ICatalog
{
    Item? FindItem(string itemCode);
    ItemBarcode? FindBarcode(string barcode);
    /// <summary>1 for the item's stock UOM; null when the UOM is not defined for the item.</summary>
    decimal? ConversionFactor(string itemCode, string uom);
    IReadOnlyList<ItemPrice> PricesFor(string itemCode);
    ItemGroupNode? FindGroup(string name);
    IReadOnlyList<PricingRule> PricingRules();
    IReadOnlyList<ItemTaxAssignment> ItemTaxes(string itemCode);
    IReadOnlyList<ItemTaxAssignment> ItemGroupTaxes(string itemGroup);
    ItemTaxTemplate? FindItemTaxTemplate(string name);
}
