using System.Text;
using TillPOS.Core.Money;
using TillPOS.Printing;

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
/// SampleQr (testing): with no TRN, receipts carry a QR code with a zero TRN marked "SAMPLE QR - FOR TESTING ONLY".</summary>
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
    bool SampleQr = false)
{
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
            .Append(", SampleQr = ").Append(SampleQr);
        return true;
    }
}
