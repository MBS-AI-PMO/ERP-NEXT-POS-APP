using System.Globalization;
using TillPOS.Erp;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>Once a day, compares local item codes and Retail price names with the server and removes
/// anything the server no longer has (catches renames, which leave no Deleted Document).</summary>
public sealed class ReconcileFeed(SyncContext ctx, Func<DateTimeOffset> now) : ISyncFeed
{
    private const string Key = "reconciled_at";

    public string Name => "Reconcile";

    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (ctx.Store.GetValue(Key) is { } last
            && now() - DateTimeOffset.Parse(last, CultureInfo.InvariantCulture) is var elapsed
            && elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromHours(24))
            return 0;

        var settings = ctx.Store.LoadPosSettings()
            ?? throw new InvalidOperationException("POS Profile has not been synced yet, so the price list is unknown.");

        var remoteItems = await Names(new ListQuery("Item", ["name"], [], "name asc", 0, 0), ct);
        var localItems = ctx.Store.AllItemCodes();
        if (remoteItems.Count == 0 && localItems.Count > 0)
            throw new InvalidOperationException("Server returned no items; refusing to delete the whole local catalog.");

        var removed = 0;
        foreach (var code in localItems.Where(c => !remoteItems.Contains(c)))
        {
            ctx.Store.DeleteDocument("Item", code);
            removed++;
        }

        var priceRows = await ctx.Erp.GetListAsync(new ListQuery("Item Price",
            ["name", "item_code", "uom", "price_list_rate", "valid_from", "valid_upto", "customer", "batch_no"],
            [["price_list", "=", settings.PriceList]], "name asc", 0, 0), ct);
        var kept = priceRows.Where(r => r.StrOrNull("customer") is null && r.StrOrNull("batch_no") is null).ToList();
        var localPrices = ctx.Store.AllPriceNames();
        if (kept.Count == 0 && localPrices.Count > 0)
            throw new InvalidOperationException("Server returned no prices; refusing to delete all local prices.");

        ctx.Store.UpsertPrices(kept.Select(CatalogMapper.Price));
        var keptNames = kept.Select(r => r.Str("name")).ToHashSet();
        foreach (var name in localPrices.Where(n => !keptNames.Contains(n)))
        {
            ctx.Store.DeleteDocument("Item Price", name);
            removed++;
        }

        ctx.Store.SetValue(Key, now().ToString("O", CultureInfo.InvariantCulture));
        return removed;
    }

    private async Task<HashSet<string>> Names(ListQuery q, CancellationToken ct) =>
        (await ctx.Erp.GetListAsync(q, ct)).Select(r => r.Str("name")).ToHashSet();
}
