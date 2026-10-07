using TillPOS.Data;
using TillPOS.Erp;

namespace TillPOS.Sync;

/// <param name="PosProfile">The default counter's POS Profile: its settings are also kept under the default key, which the
/// catalog feeds (price list, company) and the receipt header use.</param>
public sealed record SyncContext(IErpClient Erp, CatalogStore Store, KeysetPager Pager, string PosProfile)
{
    /// <summary>Every counter's POS Profile (may include the default one). Empty = only <see cref="PosProfile"/>.</summary>
    public IReadOnlyList<string> Profiles { get; init; } = [];

    /// <summary>The default profile first, then the other counters' profiles, each once (ERPNext names ignore case).</summary>
    public IReadOnlyList<string> AllProfiles() =>
        new[] { PosProfile }.Concat(Profiles)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
