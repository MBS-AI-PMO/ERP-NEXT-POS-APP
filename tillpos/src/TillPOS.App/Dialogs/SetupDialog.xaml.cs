using System.Collections.ObjectModel;
using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Media;
using TillPOS.Core.Shifts;
using TillPOS.Printing;
using TillPOS.Sync.Upload;

namespace TillPOS.App.Dialogs;

/// <summary>Printer, paper width, till number, invoice preview and the counters. Only those fields change; every other setting is kept.
/// "Save" writes settings.json with SetupDone = true; the caller decides whether to start or restart.</summary>
public partial class SetupDialog : Window
{
    private const string NoPrinter = "(No printer — save receipts as files)";
    private readonly TillSettings settings;
    private readonly ObservableCollection<CounterRow> counters;

    /// <param name="firstRun">At the first start an empty printer means "not chosen yet", so an installed receipt printer, else
    /// the Windows default unless it only makes files (PDF, XPS…), is offered (PrinterNames.PickDefault); later it means the
    /// till deliberately saves receipts as files.</param>
    public SetupDialog(TillSettings settings, bool firstRun)
    {
        InitializeComponent();
        this.settings = settings;
        SaveButton.Content = firstRun ? "Save and start" : "Save and restart";

        var current = settings.PrinterName ?? ""; // a JSON null must not break the window (SettingsStore.Load also maps it to "")
        var printers = InstalledPrinters(out var defaultPrinter, out var problem);
        if (current.Length > 0 && !printers.Contains(current, StringComparer.OrdinalIgnoreCase))
            printers.Insert(0, current); // keep a configured printer that is not installed (yet) visible
        PrinterBox.Items.Add(NoPrinter);
        foreach (var name in printers) PrinterBox.Items.Add(name);
        var preselect = current.Length > 0 ? current : firstRun ? PrinterNames.PickDefault(printers, defaultPrinter) : null;
        var index = preselect is null ? -1 : printers.FindIndex(p => string.Equals(p, preselect, StringComparison.OrdinalIgnoreCase));
        PrinterBox.SelectedIndex = index + 1;

        (settings.PaperWidth == PaperWidth.Mm58 ? Paper58 : Paper80).IsChecked = true;
        TillBox.Text = settings.TillNumber.ToString(CultureInfo.InvariantCulture);
        PreviewBox.IsChecked = settings.ShowReceiptPreview;
        counters = new ObservableCollection<CounterRow>(settings.EffectiveCounters()
            .Where(c => c.PosProfile.Length > 0)
            .Select(c => new CounterRow { PosProfile = c.PosProfile, Label = c.Label, CashMode = c.CashMode, CardMode = c.CardMode }));
        CounterRows.ItemsSource = counters;
        (settings.EffectiveUpload switch { UploadMode.Live => UploadLive, UploadMode.DryRun => UploadDryRun, _ => UploadOff }).IsChecked = true;
        // Field-test builds (local test cashiers or the sample QR) offer Off and Dry run only.
        if (settings.IsTestBuild)
        {
            UploadLive.IsEnabled = false;
            UploadLive.Content = "Live (not available in test builds)";
        }
        // At the first start nobody has approved anything yet, so the upload mode is shown but can only be changed later
        // (Settings on the login screen, behind the supervisor PIN).
        UploadPanel.IsEnabled = !firstRun;
        if (problem is not null) ShowStatus($"Printers could not be listed: {problem}", ok: false);
    }

    /// <summary>The saved settings (after "Save"), otherwise null.</summary>
    public TillSettings? Result { get; private set; }

    private string SelectedPrinter => PrinterBox.SelectedIndex > 0 ? (string)PrinterBox.SelectedItem : "";
    private PaperWidth SelectedPaper => Paper58.IsChecked == true ? PaperWidth.Mm58 : PaperWidth.Mm80;
    private UploadMode SelectedUpload =>
        !UploadPanel.IsEnabled ? settings.EffectiveUpload
        : UploadLive.IsChecked == true && !settings.IsTestBuild ? UploadMode.Live
        : UploadDryRun.IsChecked == true ? UploadMode.DryRun
        : UploadMode.Off;

    private static List<string> InstalledPrinters(out string? defaultPrinter, out string? problem)
    {
        defaultPrinter = null;
        problem = null;
        var names = new List<string>();
        try
        {
            using var server = new LocalPrintServer();
            foreach (var queue in server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]))
            {
                using (queue) names.Add(queue.FullName);
            }
            try
            {
                using var queue = LocalPrintServer.GetDefaultPrintQueue();
                defaultPrinter = queue.FullName;
            }
            catch (Exception)
            {
                // No default printer set.
            }
        }
        catch (Exception ex)
        {
            problem = ex.Message; // e.g. the Print Spooler service is stopped
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    private async void TestPrintClick(object sender, RoutedEventArgs e)
    {
        var printer = SelectedPrinter;
        if (printer.Length == 0)
        {
            ShowStatus("Choose a printer first.", ok: false);
            return;
        }
        var bytes = TestPrint.EscPosBytes(printer, SelectedPaper, DateTime.Now);
        TestButton.IsEnabled = false;
        ShowStatus("Printing…", ok: true);
        try
        {
            await Task.Run(() => RawPrinter.Send(printer, bytes));
            ShowStatus("Sent. Did it print and did the drawer open?", ok: true);
        }
        catch (Exception ex)
        {
            ShowStatus($"Test print failed: {ex.Message}", ok: false);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TillBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var till) || till is < 1 or > 99)
        {
            ShowStatus("Enter a till number from 1 to 99.", ok: false);
            TillBox.Focus();
            return;
        }

        var rows = counters.Select(r => new CounterSettings(r.PosProfile, r.Label, r.CashMode, r.CardMode)).ToList();
        if (TillSettings.CounterProblem(rows) is { } problem)
        {
            ShowStatus(problem, ok: false);
            return;
        }

        var updated = (settings with
        {
            PrinterName = SelectedPrinter,
            PaperWidth = SelectedPaper,
            TillNumber = till,
            ShowReceiptPreview = PreviewBox.IsChecked == true,
            SetupDone = true,
            Upload = SelectedUpload,
        }).WithCounters(rows);
        try
        {
            SettingsStore.Save(updated, SettingsStore.ProgramDataPath);
        }
        catch (Exception ex)
        {
            ShowStatus($"Settings could not be saved to {SettingsStore.ProgramDataPath}: {ex.Message}", ok: false);
            return;
        }
        Result = updated;
        DialogResult = true;
    }

    private void AddCounterClick(object sender, RoutedEventArgs e) =>
        counters.Add(new CounterRow { CardMode = counters.FirstOrDefault()?.CardMode ?? settings.CardMode });

    private void RemoveCounterClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is CounterRow row) counters.Remove(row);
    }

    private void ShowStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(ok ? "Accent" : "Danger");
    }
}

/// <summary>One editable counter row on the setup screen (only the screen changes it, so it needs no change notification).</summary>
public sealed class CounterRow
{
    public string PosProfile { get; set; } = "";
    public string Label { get; set; } = "";
    public string CashMode { get; set; } = "";
    public string CardMode { get; set; } = "";
}
