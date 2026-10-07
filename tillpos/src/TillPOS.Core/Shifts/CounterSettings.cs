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

    /// <summary>The counter a shift was opened at, as it was then: what the shift saved wins over the settings, so editing or
    /// removing a counter while its shift is open changes nothing for that shift (the drawer still balances).
    /// Profile: the shift's (a blank one, saved before counters existed, is the default counter's). Label: the shift's
    /// CounterName, else the configured one. Cash mode: the shift's, else the mode its float was counted in, else the
    /// configured one. Card mode: the shift's, else the configured one (the default counter's for an unknown counter).</summary>
    public static CounterSettings ForShift(IReadOnlyList<CounterSettings> counters, ShiftOpening shift)
    {
        if (counters.Count == 0) throw new ArgumentException("No counter is configured.", nameof(counters));
        var configured = string.IsNullOrWhiteSpace(shift.Counter) ? counters[0] : Find(counters, shift.Counter);
        return new CounterSettings(
            configured?.PosProfile ?? shift.Counter.Trim(),
            NonBlank(shift.CounterName) ?? configured?.Label ?? shift.Counter.Trim(),
            NonBlank(shift.CashMode) ?? NonBlank(shift.OpeningAmounts.FirstOrDefault()?.ModeOfPayment)
                ?? configured?.CashMode ?? counters[0].CashMode,
            NonBlank(shift.CardMode) ?? configured?.CardMode ?? counters[0].CardMode);
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
