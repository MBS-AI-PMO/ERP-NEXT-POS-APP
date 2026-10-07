using System.Text.Json.Serialization;
using TillPOS.Core.Sales;

namespace TillPOS.Core.Shifts;

/// <summary>A counter a cashier can work at: an ERPNext POS Profile and the payment modes used there for cash and card
/// (each counter has its own cash drawer account, e.g. "Cash Counter 1"). Label is the short name shown in the header and
/// printed on receipts and the Z report; blank means the profile name. The counter belongs to the shift, not to the PC.</summary>
public sealed record CounterSettings(string PosProfile, string Label = "", string CashMode = "", string CardMode = "")
{
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? PosProfile : Label.Trim();

    [JsonIgnore]
    public TenderModes Modes => new(CashMode, CardMode);

    /// <summary>The configured counter for a POS Profile (ERPNext names ignore case), or null.</summary>
    public static CounterSettings? Find(IReadOnlyList<CounterSettings> counters, string? profile) =>
        string.IsNullOrWhiteSpace(profile)
            ? null
            : counters.FirstOrDefault(c => string.Equals(c.PosProfile, profile.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The counter a shift was opened at. A shift saved before counters existed (blank) belongs to the default
    /// (first) counter. A counter removed from the settings while its shift is open keeps its profile, label and the cash
    /// mode its float was counted in, so the drawer still balances; the card mode is the default counter's.</summary>
    public static CounterSettings ForShift(IReadOnlyList<CounterSettings> counters, ShiftOpening shift)
    {
        if (counters.Count == 0) throw new ArgumentException("No counter is configured.", nameof(counters));
        if (string.IsNullOrWhiteSpace(shift.Counter)) return counters[0];
        return Find(counters, shift.Counter)
            ?? new CounterSettings(shift.Counter, shift.CounterName ?? shift.Counter,
                shift.OpeningAmounts.FirstOrDefault()?.ModeOfPayment ?? counters[0].CashMode, counters[0].CardMode);
    }
}
