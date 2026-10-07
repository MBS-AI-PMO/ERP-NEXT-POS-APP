using System.Globalization;
using TillPOS.Data;

namespace TillPOS.Sync.Upload;

/// <summary>Live never uploads old history by default. The first time a till goes Live, kv "upload_live_since" is set to that
/// moment and every shift opened before it (test data) is Excluded with its bills and approvals; a supervisor may include
/// them instead, which sets the time to the earliest shift. Going back to Live later (e.g. after a rollback to Off) keeps the
/// time, so the bills queued meanwhile still upload.</summary>
public static class UploadHistory
{
    public const string LiveSinceKey = "upload_live_since";

    /// <summary>When the till first went Live, or null when it never did.</summary>
    public static DateTimeOffset? LiveSince(CatalogStore kv) =>
        kv.GetValue(LiveSinceKey) is { Length: > 0 } text
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;

    /// <summary>True when going Live now would exclude earlier shifts (first time Live), with how many.</summary>
    public static int EarlierShifts(ShiftStore shifts, CatalogStore kv, DateTimeOffset now) =>
        LiveSince(kv) is not null ? 0 : shifts.Unfinished().Count(s => s.Opening.OpenedAt < now);

    /// <summary>The switch to Live. The first time: <paramref name="includeHistory"/> false excludes everything opened before
    /// <paramref name="now"/>; true keeps it all (the time becomes the earliest shift, and anything excluded before is put back).
    /// Later switches change nothing. Returns the shifts excluded.</summary>
    public static int SwitchToLive(ShiftStore shifts, CatalogStore kv, DateTimeOffset now, bool includeHistory)
    {
        if (LiveSince(kv) is not null) return 0;
        if (includeHistory)
        {
            var earliest = shifts.EarliestOpening() is { } first && first < now ? first : now;
            kv.SetValue(LiveSinceKey, earliest.ToString("O", CultureInfo.InvariantCulture));
            shifts.IncludeAll();
            return 0;
        }
        kv.SetValue(LiveSinceKey, now.ToString("O", CultureInfo.InvariantCulture));
        return shifts.ExcludeOpenedBefore(now);
    }
}
