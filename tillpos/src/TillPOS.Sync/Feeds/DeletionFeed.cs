using TillPOS.Erp;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>Applies Deleted Document rows. A name that exists on the server again (deleted then recreated,
/// or re-delivered within the pager's overlap window) is left alone: only names still absent are deleted.</summary>
public sealed class DeletionFeed(SyncContext ctx) : ISyncFeed
{
    private static readonly string[] Tracked = ["Item", "Item Price", "Pricing Rule", "Item Tax Template", "Sales Taxes and Charges Template"];

    public string Name => "Deleted Document";

    public Task<int> RunAsync(CancellationToken ct) =>
        ctx.Pager.PullAsync("Deleted Document", "Deleted Document", ["deleted_doctype", "deleted_name"],
            [["deleted_doctype", "in", Tracked]], async page =>
            {
                foreach (var group in page.GroupBy(r => r.Str("deleted_doctype")))
                {
                    var names = group.Select(r => r.Str("deleted_name")).Distinct().ToList();
                    var present = (await ctx.Erp.GetListAsync(new ListQuery(group.Key, ["name"], [["name", "in", names]], "name asc", 0, 0), ct))
                        .Select(r => r.Str("name")).ToHashSet();
                    foreach (var name in names.Where(n => !present.Contains(n)))
                        ctx.Store.DeleteDocument(group.Key, name);
                }
            }, ct: ct);
}
