using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TillPOS.Presentation;
using TillPOS.Printing;

namespace TillPOS.App.Dialogs;

/// <summary>The saved bill on screen, laid out exactly as printed. "Print again" reprints without the drawer.
/// A barcode scanned while it is open closes it and is handed back (it goes on the next bill).</summary>
public partial class ReceiptDialog : Window
{
    private const double PaperFontSize = 12.5;
    private static readonly FontFamily Mono = new("Consolas");

    // A dialog is its own window, so the main window's scanner watch does not see this input.
    private readonly ScanBuffer scanBuffer = new(() => DateTimeOffset.Now);
    private readonly Func<string?> reprint;

    public ReceiptDialog(IReadOnlyList<PrintLine> lines, bool hasQr, string? printError, Func<string?> reprint)
    {
        InitializeComponent();
        this.reprint = reprint;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;
        foreach (var line in lines) Paper.Children.Add(LineBlock(line));
        if (hasQr) QrBox.Visibility = Visibility.Visible;
        if (printError is not null) ShowBanner(PrinterProblem(printError), ok: false);

        Loaded += (_, _) => Scroller.Focus();
        PreviewTextInput += (_, e) => { foreach (var c in e.Text) scanBuffer.OnChar(c); };
        // Enter goes through the scan buffer first, so a scan's Enter closes the popup with the code, never as a plain Close.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || scanBuffer.OnChar('\r') is not { } code) return;
            e.Handled = true;
            ScannedCode = code;
            Close();
        };
    }

    /// <summary>The barcode that closed the popup, or null when it was closed by hand.</summary>
    public string? ScannedCode { get; private set; }

    /// <summary>Lines are shown exactly as the renderer gives them (spaces kept). Title text is bare, so it is centred here;
    /// it prints double width and height, Big prints double height.</summary>
    private static TextBlock LineBlock(PrintLine line)
    {
        var block = new TextBlock
        {
            Text = line.Text,
            FontFamily = Mono,
            FontSize = PaperFontSize,
            Foreground = Brushes.Black,
            TextWrapping = TextWrapping.NoWrap,
        };
        switch (line.Style)
        {
            case LineStyle.Title:
                block.FontSize = PaperFontSize * 2;
                block.FontWeight = FontWeights.Bold;
                block.TextAlignment = TextAlignment.Center;
                break;
            case LineStyle.Big:
                block.FontWeight = FontWeights.Bold;
                block.LayoutTransform = new ScaleTransform(1, 2);
                break;
            case LineStyle.Bold:
                block.FontWeight = FontWeights.Bold;
                break;
        }
        return block;
    }

    private void PrintAgain(object sender, RoutedEventArgs e)
    {
        PrintAgainButton.IsEnabled = false;
        var error = reprint();
        ShowBanner(error is null ? "Sent to printer" : PrinterProblem(error), ok: error is null);
        // Re-enable only after taps queued during the (blocking) print were handled, so a double tap prints once.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => PrintAgainButton.IsEnabled = true);
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void ShowBanner(string text, bool ok)
    {
        BannerText.Text = text;
        Banner.Background = (Brush)FindResource(ok ? "Accent" : "Danger");
        Banner.Visibility = Visibility.Visible;
    }

    private static string PrinterProblem(string error) => $"Printer problem: {error}. The bill is saved.";
}
