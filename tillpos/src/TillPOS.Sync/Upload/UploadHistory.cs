using System.Globalization;
using TillPOS.Core.Security;
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
        // The exclusion (or inclusion) comes first and the time is written last: if the first step fails, the till has not gone
        // Live and the next switch starts over.
        if (includeHistory)
        {
            var earliest = shifts.EarliestOpening() is { } first && first < now ? first : now;
            shifts.IncludeAll();
            kv.SetValue(LiveSinceKey, earliest.ToString("O", CultureInfo.InvariantCulture));
            return true;
        }
        shifts.ExcludeClosedBefore(now);
        kv.SetValue(LiveSinceKey, now.ToString("O", CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>kv flag: the refusal of Live from settings.json is logged (once, until the mode changes).</summary>
    public const string RefusalLoggedKey = "upload_live_refused_logged";

    /// <summary>The upload mode a till starts in, for the mode in its settings file. Live while the first switch must wait for an
    /// open shift starts Off, with <see cref="CloseShiftFirst"/> as the notice, and is logged once (until the mode changes). Live
    /// otherwise records the switch the first time (history before it stays out) and logs it "from settings file".</summary>
    public static (UploadMode Mode, string? Notice) ModeAtStart(UploadMode requested, ShiftStore shifts, CatalogStore kv, ApprovalStore approvals,
        DateTimeOffset now)
    {
        if (requested != UploadMode.Live || !MustCloseShiftFirst(shifts, kv))
        {
            if (kv.GetValue(RefusalLoggedKey) is { Length: > 0 }) kv.SetValue(RefusalLoggedKey, "");
            if (requested == UploadMode.Live && SwitchToLive(shifts, kv, now, includeHistory: false))
                approvals.Add(Log("from settings file", now));
            return (requested, null);
        }
        if (kv.GetValue(RefusalLoggedKey) is not { Length: > 0 })
        {
            approvals.Add(Log("Live requested in settings.json while a shift was open — stayed Off", now));
            kv.SetValue(RefusalLoggedKey, "1");
        }
        return (UploadMode.Off, CloseShiftFirst);
    }

    private static ApprovalRecord Log(string reason, DateTimeOffset now) =>
        new(Guid.NewGuid().ToString("N"), ApprovalAction.UploadModeChange, "", "", "", null, null, 0m, reason, now);
}
