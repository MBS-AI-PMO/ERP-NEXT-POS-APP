using System.Text.Json;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>Re-reads this till's POS Profile, its Company, Currency and address on every pull (4 small requests).</summary>
public sealed class PosProfileFeed(SyncContext ctx) : ISyncFeed
{
    public string Name => "POS Profile";

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var profile = await ctx.Erp.GetDocAsync("POS Profile", ctx.PosProfile, ct);
        var company = await ctx.Erp.GetDocAsync("Company", profile.Str("company"), ct);
        var currency = await ctx.Erp.GetDocAsync("Currency", profile.StrOrNull("currency") ?? company.Str("default_currency"), ct);
        JsonElement? address = profile.StrOrNull("company_address") is { } a ? await ctx.Erp.GetDocAsync("Address", a, ct) : null;
        ctx.Store.SavePosSettings(CatalogMapper.PosSettings(profile, company, currency, address));
        return 1;
    }
}
