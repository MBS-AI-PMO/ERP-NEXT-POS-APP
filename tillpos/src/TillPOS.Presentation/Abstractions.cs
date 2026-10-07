using TillPOS.Core.Catalog;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;
using TillPOS.Data;
using TillPOS.Sync.Upload;

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

    /// <summary>Shows the till setup (printer, paper, till number, invoice preview, counters, upload mode). Returns true when the
    /// settings were saved (the app then restarts to use them). When the upload mode was changed, <paramref name="uploadModeChanged"/>
    /// (old mode, new mode) runs before the restart, so it can be logged and the switch to Live prepared.</summary>
    Task<bool> ShowSetupAsync(Func<UploadMode, UploadMode, Task> uploadModeChanged);

    /// <summary>A yes/no question (No is the default). Returns true for Yes.</summary>
    bool Confirm(string title, string message);

    /// <summary>Shows a read-only text (e.g. a document's JSON) until closed.</summary>
    void ShowText(string title, string text);

    /// <summary>Shows the price check (modal). Returns what "Add to bill" should add (<see cref="PriceCheckViewModel.AddToBill"/>),
    /// or null when it was closed without adding.</summary>
    PriceCheckPick? ShowPriceCheck(PriceCheckViewModel vm);

    /// <summary>Shows the held bills (modal). Returns the id of the bill to recall, or null.</summary>
    string? ShowHeldBills(HeldBillsViewModel vm);
}

public interface IClock
{
    DateTimeOffset Now { get; }
}

public interface IReceiptOutput
{
    /// <summary>Prints the receipt; throws if the printer fails (the bill is already saved). <paramref name="copy"/> marks a
    /// reprint ("*** COPY ***").</summary>
    void Print(Receipt receipt, bool openDrawer, bool copy = false);

    /// <summary>Prints the shift (Z) report; throws if the printer fails (the shift is already closed). Never opens the drawer.
    /// <paramref name="approvedBy"/> is the supervisor who approved a cash difference over the limit, if one was needed;
    /// <paramref name="firstCountDifference"/> is the first count's cash difference when a recount changed it.</summary>
    void PrintShiftReport(ShiftOpening opening, ShiftClosing closing, string cashierName, string? approvedBy, decimal? firstCountDifference);
}

/// <summary>Everything the view models need from the rest of the till, assembled once by the app.</summary>
/// <param name="Counters">The counters a shift can be opened at (at least one); the first is the default counter.</param>
/// <param name="NewSaleContextFor">A sale context for a counter's POS Profile (its price list, warehouse, taxes and rounding);
/// throws when that counter's POS settings are not on the till.</param>
public sealed record TillContext(
    int TillNumber,
    IReadOnlyList<CounterSettings> Counters,
    Func<string, SaleContext> NewSaleContextFor,
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
    bool ShowReceiptPreview,
    HeldCartStore Held)
{
    public CounterSettings DefaultCounter => Counters[0];

    /// <summary>The counter the session's shift belongs to (the default counter when no shift is open). It comes from the
    /// shift, so it cannot change until the shift is closed.</summary>
    public CounterSettings CounterOf(SessionState session) =>
        session.Shift is { } shift ? CounterSettings.ForShift(Counters, shift) : DefaultCounter;

    /// <summary>A sale context for the session's counter.</summary>
    public SaleContext SaleContextFor(SessionState session) => NewSaleContextFor(CounterOf(session).PosProfile);
}
