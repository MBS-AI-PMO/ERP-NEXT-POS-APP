using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

public sealed class DeletionFeed(SyncContext ctx) : ISyncFeed
{
    private static readonly string[] Tracked = ["Item", "Item Price", "Pricing Rule", "Item Tax Template", "Sales Taxes and Charges Template"];

    public string Name => "Deleted Document";

    public Task<int> RunAsync(CancellationToken ct) =>
        ctx.Pager.PullAsync("Deleted Document", "Deleted Document", ["deleted_doctype", "deleted_name"],
            [["deleted_doctype", "in", Tracked]], page =>
            {
                foreach (var r in page) ctx.Store.DeleteDocument(r.Str("deleted_doctype"), r.Str("deleted_name"));
                return Task.CompletedTask;
            }, ct: ct);
}
