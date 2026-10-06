using TillPOS.Data;
using TillPOS.Sync;
using TillPOS.Sync.Feeds;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Sync;

public sealed class CatalogPullerExtraFeedTests : IDisposable
{
    private readonly TempDb temp = new();

    public void Dispose() => temp.Dispose();

    private sealed class CountingFeed : ISyncFeed
    {
        public int Runs { get; private set; }
        public string Name => "Extra";
        public Task<int> RunAsync(CancellationToken ct) { Runs++; return Task.FromResult(0); }
    }

    [Fact]
    public async Task Extra_feeds_run_after_the_default_feeds()
    {
        var erp = new FakeErp();
        var ctx = new SyncContext(erp, new CatalogStore(temp.Db), new KeysetPager(erp, new InMemorySyncState()), "Test Counter");
        var extra = new CountingFeed();

        var report = await CatalogPuller.CreateDefault(ctx, () => { }, null, extra).RunAsync();

        Assert.Equal(1, extra.Runs);
        Assert.Equal("Extra", report.Feeds[^1].Feed);
        Assert.Null(report.Feeds[^1].Error);
    }
}
