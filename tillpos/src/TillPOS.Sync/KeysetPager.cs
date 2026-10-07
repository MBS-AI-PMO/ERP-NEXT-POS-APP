using System.Globalization;
using System.Text.Json;
using TillPOS.Erp;

namespace TillPOS.Sync;

/// <summary>Pulls rows changed since the saved mark, ordered by (modified, name), page by page.
/// The mark is saved after every page, so an interrupted pull resumes where it stopped.
/// Uses `modified >= mark` and skips names already processed at the mark, so rows sharing a
/// timestamp across a page boundary are never skipped or repeated.
/// Each pull also re-reads rows modified within the overlap window before the saved mark, so rows
/// committed late (stamped earlier than the mark) are not skipped; handlers must therefore be idempotent.</summary>
public sealed class KeysetPager(IErpClient erp, ISyncStateStore state, TimeSpan? overlap = null)
{
    private readonly TimeSpan effectiveOverlap = overlap ?? TimeSpan.FromMinutes(5);

    /// <summary>Optional observer, called after each handled page with its number of fresh rows (download progress).</summary>
    public Action<int>? PageHandled { get; set; }

    /// <summary>True while nothing has been pulled for this key yet (the next pull is a full first download).</summary>
    public bool IsAtStart(string key) => state.Get(key).Modified == SyncMark.Start.Modified;

    public async Task<int> PullAsync(
        string key, string doctype, IReadOnlyList<string> fields, IReadOnlyList<object[]> extraFilters,
        Func<IReadOnlyList<JsonElement>, Task> handlePage, int pageSize = 500, CancellationToken ct = default)
    {
        var saved = state.Get(key);
        var mark = saved.Modified == SyncMark.Start.Modified || effectiveOverlap == TimeSpan.Zero
            ? saved
            : new SyncMark(Shift(saved.Modified, -effectiveOverlap), []);
        var processedAtMark = new HashSet<string>(mark.NamesAtMark);
        var allFields = fields.Union(["name", "modified"]).ToList();
        var size = pageSize;
        var total = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var filters = extraFilters.Append(["modified", ">=", mark.Modified]).ToList();
            var rows = await erp.GetListAsync(new ListQuery(doctype, allFields, filters, "modified asc, name asc", 0, size), ct);
            if (rows.Count == 0) break;

            var fresh = rows.Where(r => !(Modified(r) == mark.Modified && processedAtMark.Contains(Name(r)))).ToList();
            if (fresh.Count == 0)
            {
                if (rows.Count < size) break;
                size *= 2; // a full page of rows we already processed: widen the window
                continue;
            }

            await handlePage(fresh);
            total += fresh.Count;

            var last = Modified(rows[^1]);
            var namesAtLast = rows.Where(r => Modified(r) == last).Select(Name);
            processedAtMark = last == mark.Modified
                ? [.. processedAtMark, .. namesAtLast]
                : [.. namesAtLast];
            mark = new SyncMark(last, processedAtMark.ToList());
            state.Set(key, mark);
            PageHandled?.Invoke(fresh.Count);

            if (rows.Count < size) break;
            size = pageSize;
        }
        return total;
    }

    public static string NormalizeTimestamp(string raw) =>
        DateTime.ParseExact(raw, ["yyyy-MM-dd HH:mm:ss.FFFFFF", "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None)
            .ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

    private static string Shift(string normalized, TimeSpan delta)
    {
        var shifted = DateTime.ParseExact(normalized, "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None) + delta;
        var text = shifted.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        return string.CompareOrdinal(text, SyncMark.Start.Modified) < 0 ? SyncMark.Start.Modified : text;
    }

    private static string Modified(JsonElement r) => NormalizeTimestamp(r.GetProperty("modified").GetString()!);
    private static string Name(JsonElement r) => r.GetProperty("name").GetString()!;
}
