using System.Globalization;
using TillPOS.Core.Catalog;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Sync;
using TillPOS.Sync.Feeds;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Sync;

public sealed class CatalogPullerProgressTests : IDisposable
{
    private const int ItemCount = 1200; // the item feed pages by 500: 500 + 500 + 200
    private readonly TempDb temp = new();
    private readonly FakeErp erp = new();
    private readonly CatalogStore store;
    private readonly SyncContext ctx;

    public CatalogPullerProgressTests()
    {
        store = new CatalogStore(temp.Db);
        ctx = new SyncContext(erp, store, new KeysetPager(erp, new InMemorySyncState()), "Till 1");
        SaveSettings("Retail");
        for (var i = 0; i < ItemCount; i++)
            erp.AddRow("Item", new()
            {
                ["name"] = $"I{i:0000}", ["modified"] = $"2026-10-05 10:{i / 60:00}:{i % 60:00}.000000", ["item_name"] = $"Item {i}",
                ["item_group"] = "Food", ["brand"] = null, ["stock_uom"] = "Nos", ["disabled"] = 0, ["is_sales_item"] = 1,
            });
        Price("P1", "Retail");
        Price("P2", "Retail");
        Price("P3", "Wholesale");
        // Child-table joins (barcodes, UOMs, item taxes) return nothing.
        erp.Override = q => q.Fields.Any(f => f.Contains("`tab", StringComparison.Ordinal)) ? [] : null;
    }

    public void Dispose() => temp.Dispose();

    private void SaveSettings(string priceList) =>
        store.SavePosSettings(new PosSettings("Till 1", "Shop LLC", "Shop LLC", null, null, "AED", "Stores - S", priceList,
            "Walk-in Customer", "UAE VAT 5%", null, false, 0m, 0m, [new PaymentMode("Cash", true)]));

    private void Price(string name, string list) => erp.AddRow("Item Price", new()
    {
        ["name"] = name, ["modified"] = "2026-10-05 11:00:00.000000", ["item_code"] = "I0001", ["uom"] = "Nos", ["price_list_rate"] = "2.5",
        ["valid_from"] = null, ["valid_upto"] = null, ["price_list"] = list, ["customer"] = null, ["batch_no"] = null,
    });

    private sealed class NamedFeed(string name) : ISyncFeed
    {
        public string Name => name;
        public Task<int> RunAsync(CancellationToken ct) => Task.FromResult(0);
    }

    /// <summary>Records synchronously (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class Recorder : IProgress<PullProgress>
    {
        public List<PullProgress> Reports { get; } = [];
        public void Report(PullProgress value) => Reports.Add(value);
    }

    private CatalogPuller Puller() => new([new ItemFeed(ctx), new ItemPriceFeed(ctx), new NamedFeed("Extra")], () => { }, ctx.Pager);

    private static async Task<(PullReport Report, List<PullProgress> Reports)> Run(CatalogPuller puller)
    {
        var recorder = new Recorder();
        var report = await puller.RunAsync(default, recorder);
        return (report, recorder.Reports);
    }

    /// <summary>Item reports: start (0 rows), three pages with growing rows, finish. The pager re-reads each page's last row
    /// (`modified >=`), so the second page brings 499 fresh rows, not 500.</summary>
    private static void AssertPages(List<PullProgress> item)
    {
        Assert.Equal(5, item.Count);
        Assert.Equal(new[] { 0, 500, 999, ItemCount, ItemCount }, item.Select(r => r.Rows));
    }

    [Fact]
    public async Task Reports_each_feed_start_every_page_and_each_finish()
    {
        var (report, reports) = await Run(Puller());

        Assert.True(report.Ok);
        Assert.All(reports, r => Assert.Equal(3, r.Steps));
        string[] names = ["Item", "Item Price", "Extra"];
        Assert.All(reports, r => Assert.Equal(names[r.Step - 1], r.Feed));
        Assert.Equal(reports.Select(r => r.Step).Order(), reports.Select(r => r.Step)); // steps never go back

        var item = reports.Where(r => r.Step == 1).ToList();
        AssertPages(item);
        Assert.Equal(new[] { 0, 0, 0, 0, 1 }, item.Select(r => r.Done.Count));
        Assert.All(item, r => Assert.Equal(ItemCount, r.ExpectedRows));

        var first = reports[0];
        Assert.Equal((1, "Item", 0), (first.Step, first.Feed, first.Rows));
        Assert.Empty(first.Done);

        var last = reports[^1];
        Assert.Equal(names, last.Done.Select(d => d.Feed));
        Assert.Equal(report.Feeds.Select(f => f.Rows), last.Done.Select(d => d.Rows));
        Assert.Equal(new[] { 2, 3 }, reports.Where(r => r.Step == 3).Select(r => r.Done.Count)); // start + finish only
        Assert.Null(reports.First(r => r.Step == 3).ExpectedRows);
    }

    [Fact]
    public async Task Item_price_expects_every_price_row_the_first_pull_pages_through()
    {
        var (_, reports) = await Run(Puller());

        var prices = reports.Where(r => r.Step == 2).ToList();
        Assert.All(prices, r => Assert.Equal(3, r.ExpectedRows));
        Assert.Equal(3, prices[^1].Rows);
    }

    [Fact]
    public async Task Second_pull_has_no_expected_rows_and_asks_no_counts()
    {
        await Run(Puller());
        erp.CountCalls.Clear();

        var (report, reports) = await Run(Puller());

        Assert.True(report.Ok);
        Assert.All(reports, r => Assert.Null(r.ExpectedRows));
        Assert.Empty(erp.CountCalls);
    }

    [Fact]
    public async Task Price_list_switch_expects_a_fresh_price_download()
    {
        await Run(Puller());
        SaveSettings("Wholesale");

        var (_, reports) = await Run(Puller());

        Assert.All(reports.Where(r => r.Step == 1), r => Assert.Null(r.ExpectedRows));
        Assert.All(reports.Where(r => r.Step == 2), r => Assert.Equal(3, r.ExpectedRows));
    }

    [Fact]
    public async Task Failing_count_leaves_expected_rows_null_and_the_pull_still_succeeds()
    {
        erp.FailCount = _ => new ErpException(500, "count failed", null);

        var (report, reports) = await Run(Puller());

        Assert.True(report.Ok);
        Assert.All(reports, r => Assert.Null(r.ExpectedRows));
        Assert.Equal(ItemCount, report.Feeds[0].Rows);
    }

    [Fact]
    public async Task Without_progress_no_counts_are_asked_and_the_pager_is_left_unhooked()
    {
        var report = await Puller().RunAsync();

        Assert.True(report.Ok);
        Assert.Empty(erp.CountCalls);
        Assert.Null(ctx.Pager.PageHandled);
    }

    [Fact]
    public async Task Pager_is_unhooked_after_a_pull_with_progress()
    {
        await Run(Puller());
        Assert.Null(ctx.Pager.PageHandled);
    }

    [Fact]
    public async Task Default_puller_reports_item_pages_through_the_context_pager()
    {
        var now = DateTimeOffset.Parse("2026-10-07T10:00:00Z", CultureInfo.InvariantCulture);
        var (report, reports) = await Run(CatalogPuller.CreateDefault(ctx, () => { }, () => now));

        Assert.All(reports, r => Assert.Equal(report.Feeds.Count, r.Steps));
        AssertPages(reports.Where(r => r.Feed == "Item").ToList());
        Assert.Equal(report.Feeds.Select(f => f.Feed), reports[^1].Done.Select(d => d.Feed));
    }
}
