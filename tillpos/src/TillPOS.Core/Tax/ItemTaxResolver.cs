using TillPOS.Core.Catalog;

namespace TillPOS.Core.Tax;

/// <summary>Picks the Item Tax Template for an item: the item's own Taxes rows first, then its
/// item group and each parent group. Mirrors ERPNext _get_item_tax_template: rows whose valid_from
/// has started take precedence over undated rows (newest first); then the first row whose tax category
/// equals the bill's tax category exactly (a blank category matches only a bill without one).</summary>
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

    private string? Pick(IReadOnlyList<ItemTaxAssignment> rows, DateOnly date)
    {
        var dated = rows.Where(r => r.ValidFrom is not null && r.ValidFrom <= date)
            .OrderByDescending(r => r.ValidFrom).ThenBy(r => r.Idx).ToList();
        var candidates = dated.Count > 0 ? dated : rows.Where(r => r.ValidFrom is null).OrderBy(r => r.Idx).ToList();
        return candidates.FirstOrDefault(r => (r.TaxCategory ?? "") == (taxCategory ?? ""))?.ItemTaxTemplate;
    }
}
