using System.Text.Json;
using TillPOS.Core.Catalog;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>Changed items, 500 per page; for each page, three join queries fetch barcodes, UOMs and item taxes.
/// Saving an item in ERPNext updates the item's `modified` when any child row changes.</summary>
public sealed class ItemFeed(SyncContext ctx) : ISyncFeed
{
    private static readonly string[] Fields = ["item_name", "item_group", "brand", "stock_uom", "disabled", "is_sales_item"];

    public string Name => "Item";

    public Task<int> RunAsync(CancellationToken ct) =>
        ctx.Pager.PullAsync("Item", "Item", Fields, [], async page =>
        {
            var names = page.Select(r => r.Str("name")).ToList();
            var barcodes = await Children(names, ["`tabItem Barcode`.barcode as barcode", "`tabItem Barcode`.uom as barcode_uom"], ct);
            var uoms = await Children(names, ["`tabUOM Conversion Detail`.uom as uom", "`tabUOM Conversion Detail`.conversion_factor as conversion_factor"], ct);
            var taxes = await Children(names, ["`tabItem Tax`.item_tax_template as item_tax_template", "`tabItem Tax`.tax_category as tax_category",
                "`tabItem Tax`.valid_from as valid_from", "`tabItem Tax`.idx as idx"], ct);

            ctx.Store.UpsertItems(page.Select(r =>
            {
                var code = r.Str("name");
                return new ItemSnapshot(
                    CatalogMapper.Item(r),
                    barcodes[code].Select(CatalogMapper.Barcode).OfType<ItemBarcode>().ToList(),
                    uoms[code].Select(CatalogMapper.Uom).OfType<ItemUom>().ToList(),
                    taxes[code].Select(CatalogMapper.ItemTax).OfType<ItemTaxAssignment>().ToList());
            }));
        }, ct: ct);

    private async Task<ILookup<string, JsonElement>> Children(List<string> names, string[] fields, CancellationToken ct)
    {
        var rows = await ctx.Erp.GetListAsync(new ListQuery("Item", ["name", .. fields], [["name", "in", names]], "name asc", 0, 0), ct);
        return rows.ToLookup(r => r.Str("name"));
    }
}
