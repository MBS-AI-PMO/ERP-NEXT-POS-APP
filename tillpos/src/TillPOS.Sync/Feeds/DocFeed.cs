using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>For small doctypes with child tables (Pricing Rule, tax templates): list changed names,
/// fetch each full document, then upsert it — or delete it locally when the mapper returns null.</summary>
public sealed class DocFeed<T>(
    SyncContext ctx, string doctype, Func<System.Text.Json.JsonElement, T?> map, Action<T> upsert, Action<string> delete) : ISyncFeed
    where T : class
{
    public string Name => doctype;

    public Task<int> RunAsync(CancellationToken ct) =>
        ctx.Pager.PullAsync(doctype, doctype, [], [], async page =>
        {
            foreach (var row in page)
            {
                var name = row.Str("name");
                var mapped = map(await ctx.Erp.GetDocAsync(doctype, name, ct));
                if (mapped is null) delete(name); else upsert(mapped);
            }
        }, ct: ct);
}
