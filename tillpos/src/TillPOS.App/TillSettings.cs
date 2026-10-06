using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TillPOS.Core.Money;
using TillPOS.Printing;

namespace TillPOS.App;

public sealed record LocalTestCashier(string Id, string Name, string Pin, bool IsSupervisor);

/// <summary>Per-till settings (settings.json). ApiSecretProtected is DPAPI-protected (see SecretProtector).
/// LocalTestCashiers are only used while ERPNext has no POS Cashier list yet (testing; removed in Plan 4).
/// Trn / ShopAddress are used on receipts only when ERPNext has none (Company Tax ID / POS Profile company address). ShopPhone is printed under the address when set.
/// ShowReceiptPreview shows the invoice in a popup after each sale (with "Print again").</summary>
public sealed record TillSettings(
    string BaseUrl,
    string ApiKey,
    string ApiSecretProtected,
    string PosProfile,
    int TillNumber,
    string CashMode,
    string CardMode,
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
    bool ShowReceiptPreview = true)
{
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("TILLPOS_SETTINGS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TillPOS", "settings.json");

    public static TillSettings Load(string path) =>
        JsonSerializer.Deserialize<TillSettings>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } })
        ?? throw new InvalidDataException($"{path} is empty.");
}
