using TillPOS.Core.Catalog;

namespace TillPOS.Core.Pricing;

/// <summary>Finds the Retail price list rate for an item in a UOM on a date, the way ERPNext does:
/// a price in that UOM if one exists, else the stock-UOM price times the conversion factor.</summary>
public sealed class PriceResolver(ICatalog catalog)
{
    public decimal? PriceListRate(Item item, string uom, decimal conversionFactor, DateOnly date)
    {
        var valid = catalog.PricesFor(item.ItemCode)
            .Where(p => (p.ValidFrom is null || p.ValidFrom <= date) && (p.ValidUpto is null || p.ValidUpto >= date))
            .ToList();

        var exact = Latest(valid.Where(p => UomOf(p, item) == uom));
        if (exact is not null) return exact.PriceListRate;
        if (uom == item.StockUom) return null;

        var stock = Latest(valid.Where(p => UomOf(p, item) == item.StockUom));
        return stock is null ? null : stock.PriceListRate * conversionFactor;
    }

    private static string UomOf(ItemPrice p, Item item) => string.IsNullOrEmpty(p.Uom) ? item.StockUom : p.Uom;

    private static ItemPrice? Latest(IEnumerable<ItemPrice> rows) =>
        rows.OrderByDescending(p => p.ValidFrom ?? DateOnly.MinValue).FirstOrDefault();
}
