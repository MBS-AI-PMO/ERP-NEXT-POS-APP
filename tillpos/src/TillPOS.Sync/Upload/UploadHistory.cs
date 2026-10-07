using System.Globalization;
using TillPOS.Data;

namespace TillPOS.Sync.Upload;

/// <summary>Going Live never hides real sales, and never uploads test history by default. The first time a till goes Live,
/// kv "upload_live_since" is set to that moment; every shift <b>closed</b> before it (test data) is Excluded with its bills and
/// its approvals of before then, unless a supervisor includes them (the time then becomes the earliest shift). Nothing created
/// at or after that moment is excluded, and the first switch is refused while a shift is open (its bills would be split
/// across the line). Going back to Live later (e.g. after a rollback to Off) keeps the time, so bills queued meanwhile upload.</summary>
public static class UploadHistory
{
    public const string LiveSinceKey = "upload_live_since";
    public const string CloseShiftFirst = "Close the shift first, then switch to Live.";

    /// <summary>When the till first went Live, or null when it never did.</summary>
    public static DateTimeOffset? LiveSince(CatalogStore kv) =>
        kv.GetValue(LiveSinceKey) is { Length: > 0 } text
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;

    /// <summary>True when switching to Live must wait: it would be the first switch, and a shift is open.</summary>
    public static bool MustCloseShiftFirst(ShiftStore shifts, CatalogStore kv) => LiveSince(kv) is null && shifts.Current() is not null;

    /// <summary>How many closed shifts with something still to upload the first switch to Live at <paramref name="now"/> would
    /// exclude (0 once the till has been Live).</summary>
    public static int EarlierShifts(ShiftStore shifts, CatalogStore kv, DateTimeOffset now) =>
        LiveSince(kv) is not null ? 0 : shifts.Unfinished().Count(s => s.Closing is { } closing && closing.ClosedAt < now);

    /// <summary>The switch to Live. The first time: <paramref name="includeHistory"/> false excludes the shifts closed before
    /// <paramref name="now"/>; true keeps it all (the time becomes the earliest shift, and anything excluded before is put back).
    /// Later switches change nothing. Returns true when this call recorded the time (the first switch).</summary>
    /// <exception cref="InvalidOperationException">The first switch while a shift is open (<see cref="CloseShiftFirst"/>).</exception>
    public static bool SwitchToLive(ShiftStore shifts, CatalogStore kv, DateTimeOffset now, bool includeHistory)
    {
        if (LiveSince(kv) is not null) return false;
        if (shifts.Current() is not null) throw new InvalidOperationException(CloseShiftFirst);
        if (includeHistory)
        {
            var earliest = shifts.EarliestOpening() is { } first && first < now ? first : now;
            kv.SetValue(LiveSinceKey, earliest.ToString("O", CultureInfo.InvariantCulture));
            shifts.IncludeAll();
            return true;
        }
        kv.SetValue(LiveSinceKey, now.ToString("O", CultureInfo.InvariantCulture));
        shifts.ExcludeClosedBefore(now);
        return true;
    }
}
