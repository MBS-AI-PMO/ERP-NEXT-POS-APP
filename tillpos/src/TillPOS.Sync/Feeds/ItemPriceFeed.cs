using System.Globalization;
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

        // Every price list switch bumps a generation so the pager starts a fresh full pull (A -> B -> A must not reuse A's old watermark).
        var generation = int.TryParse(ctx.Store.GetValue("item_price_generation"), CultureInfo.InvariantCulture, out var g) ? g : 0;
        if (ctx.Store.GetValue("item_price_list") != settings.PriceList)
        {
            generation++;
            ctx.Store.SetValue("item_price_generation", generation.ToString(CultureInfo.InvariantCulture));
            ctx.Store.SetValue("item_price_list", settings.PriceList);
            ctx.Store.SetValue("reconciled_at", DateTimeOffset.MinValue.ToString("O", CultureInfo.InvariantCulture));
        }

        return ctx.Pager.PullAsync($"Item Price|{settings.PriceList}|{generation}", "Item Price", Fields, [], page =>
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
