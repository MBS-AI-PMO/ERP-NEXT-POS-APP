using System.Text.Json;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>Re-reads every counter's POS Profile, its Company, Currency and address on every pull (a few small requests each;
/// the Company and Currency are read once per pull). Each counter is stored under its own key; the default counter also under
/// the default key. A failure on the default counter fails the feed; another counter that cannot be read (deleted, or not
/// readable by this till's ERPNext user) is skipped and keeps whatever was downloaded before — with nothing downloaded, the
/// Open Shift screen shows it as unavailable. Skipped counters are named in <see cref="LastNote"/> (the pull stays Ok).</summary>
public sealed class PosProfileFeed(SyncContext ctx) : ISyncFeed, INotingFeed
{
    public string Name => "POS Profile";

    public string? LastNote { get; private set; }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        LastNote = null;
        var docs = new Dictionary<(string, string), JsonElement>();
        async Task<JsonElement> Get(string doctype, string name)
        {
            if (!docs.TryGetValue((doctype, name), out var doc)) docs[(doctype, name)] = doc = await ctx.Erp.GetDocAsync(doctype, name, ct);
            return doc;
        }

        var stored = 0;
        var skipped = new List<string>();
        foreach (var name in ctx.AllProfiles())
        {
            var isDefault = string.Equals(name, ctx.PosProfile.Trim(), StringComparison.OrdinalIgnoreCase);
            try
            {
                var profile = await Get("POS Profile", name);
                var company = await Get("Company", profile.Str("company"));
                var currency = await Get("Currency", profile.StrOrNull("currency") ?? company.Str("default_currency"));
                JsonElement? address = profile.StrOrNull("company_address") is { } a ? await Get("Address", a) : null;
                var settings = CatalogMapper.PosSettings(profile, company, currency, address);
                ctx.Store.SavePosSettings(name, settings);
                if (isDefault) ctx.Store.SavePosSettings(settings);
                stored++;
            }
            catch (Exception ex) when (!isDefault && !ct.IsCancellationRequested)
            {
                skipped.Add($"{name} ({ex.Message})");
            }
        }
        if (skipped.Count > 0) LastNote = "Counter(s) not readable, skipped: " + string.Join("; ", skipped);
        return stored;
    }
}
