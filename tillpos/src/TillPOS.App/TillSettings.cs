using TillPOS.Core.Money;
using TillPOS.Printing;

namespace TillPOS.App;

public sealed record LocalTestCashier(string Id, string Name, string Pin, bool IsSupervisor);

/// <summary>Per-till settings (settings.json, read and written by SettingsStore). ApiSecretProtected is DPAPI-protected
/// (see SecretProtector). ApiSecret is the plain secret a packaged settings.json ships with; on the first start it is moved
/// into ApiSecretProtected and removed from the files.
/// LocalTestCashiers are only used while ERPNext has no POS Cashier list yet (testing; removed in Plan 4).
/// Trn / ShopAddress are used on receipts only when ERPNext has none (Company Tax ID / POS Profile company address). ShopPhone is printed under the address when set.
/// ShowReceiptPreview shows the invoice in a popup after each sale (with "Print again").
/// SetupDone is set by the setup screen; until then it is shown at start.</summary>
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
    bool SetupDone = false);
