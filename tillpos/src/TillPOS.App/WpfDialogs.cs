using System.Windows;
using TillPOS.App.Dialogs;
using TillPOS.Core.Sales;
using TillPOS.Presentation;
using TillPOS.Printing;
using TillPOS.Sync.Upload;

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
    public async Task<bool> ShowSetupAsync(Func<UploadMode, UploadMode, Task<bool>> uploadModeChanged)
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
            bool accepted;
            try
            {
                accepted = await uploadModeChanged(current.Upload, saved.Upload);
            }
            catch (Exception ex)
            {
                logError(ex);
                accepted = false;
            }
            // Refused (e.g. Live while a shift is open) or failed: the other settings stay saved, the upload mode does not change.
            if (!accepted)
            {
                try
                {
                    SettingsStore.Save(saved with { Upload = current.Upload }, SettingsStore.ProgramDataPath);
                }
                catch (Exception ex)
                {
                    logError(ex);
                    Info($"The upload mode could not be set back in {SettingsStore.ProgramDataPath}: {ex.Message}");
                }
            }
        }
        restart();
        return true;
    }

    public Task<string?> AskTextAsync(string title, string prompt)
    {
        var box = new System.Windows.Controls.TextBox { FontSize = 18, Height = 40, Margin = new Thickness(0, 8, 0, 12), VerticalContentAlignment = VerticalAlignment.Center };
        var ok = new System.Windows.Controls.Button { Content = "OK", Width = 120, Height = 44, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", Width = 120, Height = 44, IsCancel = true };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, FontSize = 16 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Title = title, Content = panel, Width = 520, SizeToContent = SizeToContent.Height, Owner = Top(),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => box.Focus();
        return Task.FromResult(window.ShowDialog() == true ? box.Text : null);
    }

    public void ShowText(string title, string text)
    {
        var box = new System.Windows.Controls.TextBox
        {
            Text = text, IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
        };
        new Window { Title = title, Content = box, Width = 900, Height = 620, Owner = Top(), WindowStartupLocation = WindowStartupLocation.CenterOwner }
            .ShowDialog();
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Top(), message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>The window a prompt belongs on: the active one (e.g. the held-bills or price-check dialog, whose supervisor
    /// PIN or message must sit above it), else the main window.</summary>
    private Window Top() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? owner;
}
