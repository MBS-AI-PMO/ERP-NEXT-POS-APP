using TillPOS.Core.Catalog;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>Full refresh every pull: group trees are small, and ERPNext rebuilds lft/rgt without touching `modified`.</summary>
public sealed class ItemGroupFeed(SyncContext ctx) : ISyncFeed
{
    public string Name => "Item Group";

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var groups = await ctx.Erp.GetListAsync(new ListQuery("Item Group", ["name", "parent_item_group", "lft", "rgt"], [], "lft asc", 0, 0), ct);
        var taxes = (await ctx.Erp.GetListAsync(new ListQuery("Item Group",
            ["name", "`tabItem Tax`.item_tax_template as item_tax_template", "`tabItem Tax`.tax_category as tax_category",
             "`tabItem Tax`.valid_from as valid_from", "`tabItem Tax`.idx as idx"], [], "name asc", 0, 0), ct))
            .ToLookup(r => r.Str("name"));

        ctx.Store.ReplaceItemGroups(groups.Select(g => new ItemGroupSnapshot(
            CatalogMapper.Group(g),
            taxes[g.Str("name")].Select(CatalogMapper.ItemTax).OfType<ItemTaxAssignment>().ToList())));
        return groups.Count;
    }
}
