using System.Diagnostics;
using TillPOS.Core.Catalog;
using TillPOS.Core.Pricing;
using TillPOS.Erp.Mapping;
using TillPOS.Sync.Feeds;

namespace TillPOS.Sync;

public sealed record FeedResult(string Feed, int Rows, TimeSpan Duration, string? Error);

public sealed record PullReport(IReadOnlyList<FeedResult> Feeds)
{
    public bool Ok => Feeds.All(f => f.Error is null);
}

/// <summary>Runs every feed in order. A failing feed is reported and the rest still run;
/// afterPull (e.g. SqliteCatalog.Reload) always runs at the end.</summary>
public sealed class CatalogPuller(IReadOnlyList<ISyncFeed> feeds, Action afterPull)
{
    public async Task<PullReport> RunAsync(CancellationToken ct = default)
    {
        var results = new List<FeedResult>();
        foreach (var feed in feeds)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                results.Add(new FeedResult(feed.Name, await feed.RunAsync(ct), sw.Elapsed, null));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new FeedResult(feed.Name, 0, sw.Elapsed, ex.Message));
            }
        }
        afterPull();
        return new PullReport(results);
    }

    public static CatalogPuller CreateDefault(SyncContext ctx, Action afterPull, Func<DateTimeOffset>? now = null) => new(
    [
        new PosProfileFeed(ctx),
        new ItemGroupFeed(ctx),
        new DocFeed<ItemTaxTemplate>(ctx, "Item Tax Template", CatalogMapper.ItemTaxTemplate, ctx.Store.UpsertItemTaxTemplate, ctx.Store.DeleteItemTaxTemplate),
        new DocFeed<SalesTaxTemplate>(ctx, "Sales Taxes and Charges Template", CatalogMapper.SalesTaxTemplate, ctx.Store.UpsertSalesTaxTemplate, ctx.Store.DeleteSalesTaxTemplate),
        new DocFeed<PricingRule>(ctx, "Pricing Rule", CatalogMapper.PricingRule, ctx.Store.UpsertPricingRule, ctx.Store.DeletePricingRule),
        new ItemFeed(ctx),
        new ItemPriceFeed(ctx),
        new DeletionFeed(ctx),
        new ReconcileFeed(ctx, now ?? (() => DateTimeOffset.UtcNow)),
    ], afterPull);
}
