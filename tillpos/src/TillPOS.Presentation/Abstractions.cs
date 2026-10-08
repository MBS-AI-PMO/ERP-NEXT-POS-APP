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
    string? ShowReceipt(Receipt receipt, string? printError, Func<string?> reprint, bool copy = false, bool reprinted = false);

    /// <summary>Shows the till setup (printer, paper, till number, invoice preview, counters, upload mode). Returns true when the
    /// settings were saved (the app then restarts to use them). When the upload mode was changed, <paramref name="uploadModeChanged"/>
    /// (old mode, new mode) runs before the restart, so it can be logged and the switch to Live prepared; when it returns false
    /// (or fails) the old upload mode is saved back.</summary>
    Task<bool> ShowSetupAsync(Func<UploadMode, UploadMode, Task<bool>> uploadModeChanged);

    /// <summary>A yes/no question (No is the default). Returns true for Yes.</summary>
    bool Confirm(string title, string message);

    /// <summary>Shows a read-only text (e.g. a document's JSON) until closed.</summary>
    void ShowText(string title, string text);

    /// <summary>Asks for a line of text; null when cancelled.</summary>
    Task<string?> AskTextAsync(string title, string prompt);

    /// <summary>Shows the price check (modal). Returns what "Add to bill" should add (<see cref="PriceCheckViewModel.AddToBill"/>),
    /// or null when it was closed without adding.</summary>
    PriceCheckPick? ShowPriceCheck(PriceCheckViewModel vm);

    /// <summary>Shows the Sync status window (modal, read-only). Returns true when "Open Upload problems (supervisor)" was pressed.</summary>
    bool ShowSyncStatus(SyncStatusViewModel vm);

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
    /// <paramref name="approvedBy"/> is the supervisor who approved the close; <paramref name="firstCountDifference"/> is a
    /// first count's cash difference to print (null: none; the blind close has no recount).</summary>
    void PrintShiftReport(ShiftOpening opening, ShiftClosing closing, string cashierName, string? approvedBy, decimal? firstCountDifference,
        DeliverySummary? deliveries = null);

    /// <summary>Prints a delivery's NOT PAID slip (<paramref name="copy"/>: a reprint); never opens the drawer; throws if the printer fails.</summary>
    void PrintDelivery(Delivery delivery, bool copy);
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

    /// <summary>The recent bills of the other tills, downloaded from ERPNext (cross-till returns); null = none.</summary>
    public RemoteReceiptStore? RemoteReceipts { get; init; }

    /// <summary>Re-reads from ERPNext what was returned against another till's bill, just before it is refunded here; null = the
    /// check is not possible (treated as offline).</summary>
    public TillPOS.Sync.IRemoteReturnsCheck? RemoteReturnsCheck { get; init; }

    /// <summary>The synced Sales Taxes and Charges Template by name (for previews built like the uploader's); none by default.</summary>
    public Func<string, SalesTaxTemplate?> TaxTemplates { get; init; } = _ => null;

    /// <summary>Delivery bills kept on this till until paid.</summary>
    public required DeliveryStore Deliveries { get; init; }

    /// <summary>The counter the session's shift belongs to (the default counter when no shift is open). It comes from the
    /// shift, so it cannot change until the shift is closed.</summary>
    public CounterSettings CounterOf(SessionState session) =>
        session.Shift is { } shift ? CounterSettings.ForShift(Counters, shift) : DefaultCounter;

    /// <summary>A sale context for the session's counter.</summary>
    public SaleContext SaleContextFor(SessionState session) => NewSaleContextFor(CounterOf(session).PosProfile);
}
