using System.Windows;
using TillPOS.App.Dialogs;
using TillPOS.Core.Sales;
using TillPOS.Presentation;
using TillPOS.Printing;

namespace TillPOS.App;

/// <param name="owner">The main window the dialogs are centred on (PIN, number and message prompts go on the active window).</param>
/// <param name="receiptLayout">The header and paper width the receipts are printed with (for the invoice popup).</param>
/// <param name="hasPrinter">False when receipts are saved as files (no printer configured), for the popup's wording.</param>
/// <param name="restart">Restarts the app, after setup saved new settings (the till is built from them at start).</param>
/// <param name="logError">Writes to errors.log.</param>
public sealed class WpfDialogs(
    Window owner, Func<(ReceiptHeader Header, PaperWidth Paper)> receiptLayout, bool hasPrinter, Action restart, Action<Exception> logError)
    : IDialogs
{
    public Task<string?> AskPinAsync(string title, string reason)
    {
        var dialog = new PinDialog(title, reason) { Owner = Top() };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Pin : null);
    }

    public Task<decimal?> AskNumberAsync(string title, string prompt)
    {
        var dialog = new NumberDialog(title, prompt) { Owner = Top() };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Value : null);
    }

    public void Info(string message) => MessageBox.Show(Top(), message, "TillPOS");

    /// <summary>The bill is already saved when this runs, so a problem showing it (e.g. POS settings missing) is logged and
    /// the popup skipped; it never fails the sale.</summary>
    public string? ShowReceipt(Receipt receipt, string? printError, Func<string?> reprint)
    {
        try
        {
            var (header, paper) = receiptLayout();
            var dialog = new ReceiptDialog(ReceiptRenderer.Layout(receipt, header, paper), printError, reprint, hasPrinter,
                isCreditNote: receipt.Kind == ReceiptKind.Return)
            {
                Owner = owner,
            };
            dialog.ShowDialog();
            return dialog.ScannedCode;
        }
        catch (Exception ex)
        {
            logError(ex);
            return null;
        }
    }

    public PriceCheckPick? ShowPriceCheck(PriceCheckViewModel vm)
    {
        var dialog = new PriceCheckDialog(vm) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Pick : null;
    }

    public string? ShowHeldBills(HeldBillsViewModel vm)
    {
        var dialog = new HeldBillsDialog(vm) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.ChosenId : null;
    }

    /// <summary>Starts from the file on disk (not the settings the till started with), so hand edits made since are kept.</summary>
    public bool ShowSetup(Action<string> uploadModeChanged)
    {
        TillSettings current;
        try
        {
            current = SettingsStore.Load(SettingsStore.ProgramDataPath);
        }
        catch (Exception ex)
        {
            Info($"Settings could not be read from {SettingsStore.ProgramDataPath}:\n{ex.Message}");
            return false;
        }

        var dialog = new SetupDialog(current, firstRun: false) { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } saved) return false;
        if (saved.Upload != current.Upload)
        {
            try
            {
                uploadModeChanged($"Upload mode {current.Upload} → {saved.Upload}");
            }
            catch (Exception ex)
            {
                logError(ex); // the settings are saved; a failed log line must not stop the restart
            }
        }
        restart();
        return true;
    }

    /// <summary>The window a prompt belongs on: the active one (e.g. the held-bills or price-check dialog, whose supervisor
    /// PIN or message must sit above it), else the main window.</summary>
    private Window Top() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? owner;
}
