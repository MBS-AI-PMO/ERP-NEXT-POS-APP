using System.Text;
using TillPOS.Core.Money;
using TillPOS.Core.Shifts;
using TillPOS.Printing;
using TillPOS.Sync.Upload;

namespace TillPOS.App;

public sealed record LocalTestCashier(string Id, string Name, string Pin, bool IsSupervisor)
{
    /// <summary>The PIN never appears in logs or exception text.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Id = ").Append(Id).Append(", Name = ").Append(Name).Append(", Pin = ").Append(TillSettings.Mask(Pin))
            .Append(", IsSupervisor = ").Append(IsSupervisor);
        return true;
    }
}

/// <summary>Per-till settings (settings.json, read and written by SettingsStore). ApiSecretProtected is DPAPI-protected
/// (see SecretProtector). ApiSecret is the plain secret a packaged settings.json ships with; on the first start it is moved
/// into ApiSecretProtected and removed from the files.
/// LocalTestCashiers are only used while ERPNext has no POS Cashier list yet (testing; removed in Plan 4).
/// Trn / ShopAddress are used on receipts only when ERPNext has none (Company Tax ID / POS Profile company address). ShopPhone is printed under the address when set.
/// ShowReceiptPreview shows the invoice in a popup after each sale (with "Print again").
/// SetupDone is set by the setup screen; until then it is shown at start.
/// SampleQr (testing): with no TRN, receipts carry a QR code with a zero TRN marked "SAMPLE QR - FOR TESTING ONLY".
/// Counters are the counters (POS Profiles) a cashier can open a shift at; see <see cref="EffectiveCounters"/>. Without them,
/// PosProfile / CashMode / CardMode are the only counter (settings from before counters keep working).
/// Upload is the upload mode (Off | DryRun | Live, written as text): only Live may write to ERPNext. It is Off unless a
/// supervisor changes it on the settings screen (logged as an UploadModeChange approval).</summary>
public sealed record TillSettings(
    string BaseUrl,
    string ApiKey,
    string ApiSecretProtected = "",
    string PosProfile = "",
    int TillNumber = 0,
    string CashMode = "",
    string CardMode = "",
    string PrinterName = "",
    PaperWidth PaperWidth = PaperWidth.Mm80,
    string? ReceiptFooter = null,
    int Precision = 3,
    RoundingMethod Rounding = RoundingMethod.Bankers,
    string DbPath = @"C:\ProgramData\TillPOS\till.db",
    int SyncIntervalSeconds = 90,
    IReadOnlyList<LocalTestCashier>? LocalTestCashiers = null,
    string? Trn = null,
    string? ShopAddress = null,
    string? ShopPhone = null,
    bool ShowReceiptPreview = true,
    string? ApiSecret = null,
    bool SetupDone = false,
    bool SampleQr = false,
    IReadOnlyList<CounterSettings>? Counters = null,
    UploadMode Upload = UploadMode.Off)
{
    /// <summary>The counters to offer at Open Shift, the default first: the configured Counters with a POS Profile (each profile
    /// once, trimmed; a blank label, cash or card mode falls back to the profile name, CashMode or CardMode), or, when there are
    /// none, the single PosProfile / CashMode / CardMode counter.</summary>
    public IReadOnlyList<CounterSettings> EffectiveCounters()
    {
        var counters = (Counters ?? [])
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.PosProfile))
            .Select(c => new CounterSettings(c.PosProfile.Trim(), (c.Label ?? "").Trim(), OrDefault(c.CashMode, CashMode),
                OrDefault(c.CardMode, CardMode)))
            .DistinctBy(c => c.PosProfile, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return counters.Count > 0 ? counters : [new CounterSettings((PosProfile ?? "").Trim(), "", CashMode ?? "", CardMode ?? "")];
    }

    /// <summary>Why counter rows from the setup screen cannot be saved, or null. Empty rows are ignored; each other row needs
    /// its POS Profile and cash mode (the card mode may stay blank: it is then the till's CardMode), and a profile can be only
    /// one counter.</summary>
    public static string? CounterProblem(IReadOnlyList<CounterSettings> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Cleaned(rows))
        {
            if (row.PosProfile.Length == 0)
                return $"Counter \"{(row.Label.Length > 0 ? row.Label : row.CashMode)}\" needs its POS Profile (the name in ERPNext).";
            if (row.CashMode.Length == 0) return $"Enter the cash mode of {row.PosProfile} (e.g. \"Cash Counter 1\").";
            if (!seen.Add(row.PosProfile)) return $"{row.PosProfile} is listed twice; each POS Profile is one counter.";
        }
        return null;
    }

    /// <summary>These settings with the counters from the setup screen (see <see cref="CounterProblem"/>). The first counter
    /// also becomes PosProfile / CashMode (and CardMode when set), so the single-profile settings stay meaningful; with no
    /// rows, Counters is removed and the single profile is the only counter again.</summary>
    public TillSettings WithCounters(IReadOnlyList<CounterSettings> rows)
    {
        var counters = Cleaned(rows).ToList();
        if (counters.Count == 0) return this with { Counters = null };
        var first = counters[0];
        return this with
        {
            Counters = counters,
            PosProfile = first.PosProfile,
            CashMode = first.CashMode,
            CardMode = first.CardMode.Length > 0 ? first.CardMode : CardMode,
        };
    }

    private static IEnumerable<CounterSettings> Cleaned(IReadOnlyList<CounterSettings> rows) =>
        rows.Where(r => r is not null)
            .Select(r => new CounterSettings((r.PosProfile ?? "").Trim(), (r.Label ?? "").Trim(), (r.CashMode ?? "").Trim(),
                (r.CardMode ?? "").Trim()))
            .Where(r => r.PosProfile.Length > 0 || r.Label.Length > 0 || r.CashMode.Length > 0 || r.CardMode.Length > 0);

    private static string OrDefault(string? value, string? fallback) =>
        !string.IsNullOrWhiteSpace(value) ? value.Trim() : (fallback ?? "").Trim();

    /// <summary>"***" for a value that is set, so ToString (logs, exception text) never shows the key, the secrets or PINs.</summary>
    internal static string Mask(string? value) => string.IsNullOrEmpty(value) ? "" : "***";

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("BaseUrl = ").Append(BaseUrl)
            .Append(", ApiKey = ").Append(Mask(ApiKey))
            .Append(", ApiSecretProtected = ").Append(Mask(ApiSecretProtected))
            .Append(", ApiSecret = ").Append(Mask(ApiSecret))
            .Append(", PosProfile = ").Append(PosProfile)
            .Append(", TillNumber = ").Append(TillNumber)
            .Append(", CashMode = ").Append(CashMode)
            .Append(", CardMode = ").Append(CardMode)
            .Append(", Counters = [").AppendJoin(", ", Counters ?? []).Append(']')
            .Append(", PrinterName = ").Append(PrinterName)
            .Append(", PaperWidth = ").Append(PaperWidth)
            .Append(", ReceiptFooter = ").Append(ReceiptFooter)
            .Append(", Precision = ").Append(Precision)
            .Append(", Rounding = ").Append(Rounding)
            .Append(", DbPath = ").Append(DbPath)
            .Append(", SyncIntervalSeconds = ").Append(SyncIntervalSeconds)
            .Append(", LocalTestCashiers = [").AppendJoin(", ", LocalTestCashiers ?? []).Append(']')
            .Append(", Trn = ").Append(Trn)
            .Append(", ShopAddress = ").Append(ShopAddress)
            .Append(", ShopPhone = ").Append(ShopPhone)
            .Append(", ShowReceiptPreview = ").Append(ShowReceiptPreview)
            .Append(", SetupDone = ").Append(SetupDone)
            .Append(", SampleQr = ").Append(SampleQr)
            .Append(", Upload = ").Append(Upload);
        return true;
    }
}
