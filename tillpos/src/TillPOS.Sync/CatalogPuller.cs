using System.Diagnostics;
using TillPOS.Core.Catalog;
using TillPOS.Core.Pricing;
using TillPOS.Erp.Mapping;
using TillPOS.Sync.Feeds;

namespace TillPOS.Sync;

/// <summary>One feed's outcome. Error makes the pull not Ok; Note (e.g. a skipped counter) is information only.</summary>
public sealed record FeedResult(string Feed, int Rows, TimeSpan Duration, string? Error)
{
    public string? Note { get; init; }
}

public sealed record PullReport(IReadOnlyList<FeedResult> Feeds)
{
    public bool Ok => Feeds.All(f => f.Error is null);

    /// <summary>The feeds' notes (information that does not fail the pull), e.g. a counter that could not be read.</summary>
    public IReadOnlyList<string> Notes => Feeds.Select(f => f.Note).OfType<string>().ToList();
}

/// <summary>Download progress for the first-start screen. Step is 1-based (of Steps); Rows counts the current feed's rows so far;
/// ExpectedRows is known only for a feed's full first download; Done lists the finished feeds (with their errors).</summary>
public sealed record PullProgress(int Step, int Steps, string Feed, int Rows, int? ExpectedRows, IReadOnlyList<FeedResult> Done);

/// <summary>Runs every feed in order. A failing feed is reported and the rest still run;
/// afterPull (e.g. SqliteCatalog.Reload) always runs at the end.</summary>
/// <param name="pager">The pager the feeds share; with a progress observer, its pages are reported while RunAsync runs.</param>
public sealed class CatalogPuller(IReadOnlyList<ISyncFeed> feeds, Action afterPull, KeysetPager? pager = null)
{
    public IReadOnlyList<string> FeedNames => feeds.Select(f => f.Name).ToList();

    /// <param name="progress">Optional: told when each feed starts, after every page and when each feed finishes. Only then
    /// are expected row counts asked from ERPNext (a failing count is ignored).</param>
    public async Task<PullReport> RunAsync(CancellationToken ct = default, IProgress<PullProgress>? progress = null)
    {
        var results = new List<FeedResult>();
        var step = 0;
        var feedName = "";
        var rows = 0;
        int? expected = null;
        void Report() => progress?.Report(new PullProgress(step, feeds.Count, feedName, rows, expected, results.ToList()));

        Action<int>? onPage = progress is null || pager is null ? null : n => { rows += n; Report(); };
        if (onPage is not null) pager!.PageHandled = onPage;
        try
        {
            foreach (var feed in feeds)
            {
                step++;
                feedName = feed.Name;
                rows = 0;
                expected = progress is not null && feed is ICountedFeed counted ? await ExpectedRows(counted, ct) : null;
                Report();

                var sw = Stopwatch.StartNew();
                FeedResult result;
                try
                {
                    result = new FeedResult(feed.Name, await feed.RunAsync(ct), sw.Elapsed, null)
                    {
                        Note = (feed as INotingFeed)?.LastNote,
                    };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result = new FeedResult(feed.Name, 0, sw.Elapsed, ex.Message);
                }
                results.Add(result);
                rows = result.Rows;
                Report();
            }
        }
        finally
        {
            if (onPage is not null && pager!.PageHandled == onPage) pager.PageHandled = null;
        }
        afterPull();
        return new PullReport(results);
    }

    /// <summary>A count is only a nicety for the progress screen: it never fails the pull.</summary>
    private static async Task<int?> ExpectedRows(ICountedFeed feed, CancellationToken ct)
    {
        try
        {
            return await feed.ExpectedRowsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Only the Item Price feed (rows changed since the last pull): the frequent price check between full syncs.
    /// Prices are read from the database on every scan, so nothing needs reloading afterwards.</summary>
    public static CatalogPuller CreatePricesOnly(SyncContext ctx) => new([new ItemPriceFeed(ctx)], () => { }, ctx.Pager);

    public static CatalogPuller CreateDefault(SyncContext ctx, Action afterPull, Func<DateTimeOffset>? now = null, params ISyncFeed[] extraFeeds) => new(
    [
        new PosProfileFeed(ctx),
        new ItemGroupFeed(ctx),
        new DocFeed<ItemTaxTemplate>(ctx, "Item Tax Template", CatalogMapper.ItemTaxTemplate, ctx.Store.UpsertItemTaxTemplate, ctx.Store.DeleteItemTaxTemplate),
        new DocFeed<SalesTaxTemplate>(ctx, "Sales Taxes and Charges Template", CatalogMapper.SalesTaxTemplate, ctx.Store.UpsertSalesTaxTemplate, ctx.Store.DeleteSalesTaxTemplate),
        new DocFeed<PricingRule>(ctx, "Pricing Rule", d => CatalogMapper.PricingRule(d, ctx.Store.LoadPosSettings()?.Company), ctx.Store.UpsertPricingRule, ctx.Store.DeletePricingRule),
        new ItemFeed(ctx),
        new ItemPriceFeed(ctx),
        new DeletionFeed(ctx),
        new ReconcileFeed(ctx, now ?? (() => DateTimeOffset.UtcNow)),
        .. extraFeeds,
    ], afterPull, ctx.Pager);
}
