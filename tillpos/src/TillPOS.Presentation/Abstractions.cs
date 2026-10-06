using TillPOS.Core.Catalog;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Data;

namespace TillPOS.Presentation;

public interface INavigator
{
    void Show(object viewModel);
}

public interface IDialogs
{
    Task<string?> AskPinAsync(string title, string reason);
    Task<decimal?> AskNumberAsync(string title, string prompt);
    void Info(string message);

    /// <summary>Shows the saved bill (modal) with the print error, if any. <paramref name="reprint"/> prints it again without
    /// opening the drawer and returns an error message or null. Returns the barcode when a scan closed the popup, otherwise null.</summary>
    string? ShowReceipt(Receipt receipt, string? printError, Func<string?> reprint);

    /// <summary>Shows the till setup (printer, paper, till number, invoice preview). Returns true when the settings were saved
    /// (the app then restarts to use them).</summary>
    bool ShowSetup();
}

public interface IClock
{
    DateTimeOffset Now { get; }
}

public interface IReceiptOutput
{
    /// <summary>Prints the receipt; throws if the printer fails (the bill is already saved).</summary>
    void Print(Receipt receipt, bool openDrawer);
}

/// <summary>Everything the view models need from the rest of the till, assembled once by the app.</summary>
public sealed record TillContext(
    int TillNumber,
    TenderModes Modes,
    Func<SaleContext> NewSaleContext,
    Func<string, IReadOnlyList<Item>> Search,
    Authenticator Authenticator,
    PinAttemptLimiter LoginLimiter,
    PinAttemptLimiter SupervisorLimiter,
    ShiftStore Shifts,
    ReceiptStore Receipts,
    ApprovalStore Approvals,
    CatalogStore Kv,
    IClock Clock,
    IReceiptOutput Output,
    INavigator Navigator,
    IDialogs Dialogs,
    bool ShowReceiptPreview);
