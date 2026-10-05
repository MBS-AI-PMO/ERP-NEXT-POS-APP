using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>All changed Item Prices; keeps rows of the POS Profile's price list that are not customer- or
/// batch-specific, and deletes any other changed row locally (e.g. a price moved to another list).</summary>
public sealed class ItemPriceFeed(SyncContext ctx) : ISyncFeed
{
    private static readonly string[] Fields =
        ["item_code", "uom", "price_list_rate", "valid_from", "valid_upto", "price_list", "customer", "batch_no"];

    public string Name => "Item Price";

    public Task<int> RunAsync(CancellationToken ct)
    {
        var settings = ctx.Store.LoadPosSettings()
            ?? throw new InvalidOperationException("POS Profile has not been synced yet, so the price list is unknown.");

        return ctx.Pager.PullAsync("Item Price|" + settings.PriceList, "Item Price", Fields, [], page =>
        {
            var keep = page.Where(r => r.StrOrNull("price_list") == settings.PriceList
                                       && r.StrOrNull("customer") is null
                                       && r.StrOrNull("batch_no") is null).ToList();
            var keepNames = keep.Select(r => r.Str("name")).ToHashSet();
            ctx.Store.UpsertPrices(keep.Select(CatalogMapper.Price));
            foreach (var name in page.Select(r => r.Str("name")).Where(n => !keepNames.Contains(n)))
                ctx.Store.DeleteDocument("Item Price", name);
            return Task.CompletedTask;
        }, ct: ct);
    }
}
