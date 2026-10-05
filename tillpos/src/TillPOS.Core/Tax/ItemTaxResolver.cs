using TillPOS.Core.Catalog;

namespace TillPOS.Core.Tax;

/// <summary>Picks the Item Tax Template for an item: the item's own Taxes rows first, then its
/// item group and each parent group. Rows must match the tax category (or have none) and have started.</summary>
public sealed class ItemTaxResolver(ICatalog catalog, string? taxCategory)
{
    public string? TemplateFor(Item item, DateOnly date)
    {
        var own = Pick(catalog.ItemTaxes(item.ItemCode), date);
        if (own is not null) return own;

        for (var group = catalog.FindGroup(item.ItemGroup); group is not null;
             group = group.Parent is null ? null : catalog.FindGroup(group.Parent))
        {
            var inherited = Pick(catalog.ItemGroupTaxes(group.Name), date);
            if (inherited is not null) return inherited;
        }
        return null;
    }

    private string? Pick(IReadOnlyList<ItemTaxAssignment> rows, DateOnly date) =>
        rows.Where(r => r.ValidFrom is null || r.ValidFrom <= date)
            .Where(r => string.IsNullOrEmpty(r.TaxCategory) || r.TaxCategory == taxCategory)
            .OrderByDescending(r => r.ValidFrom ?? DateOnly.MinValue)
            .ThenBy(r => r.Idx)
            .Select(r => r.ItemTaxTemplate)
            .FirstOrDefault();
}
