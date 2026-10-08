using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using TillPOS.Presentation;
using TillPOS.Printing;

namespace TillPOS.App.Dialogs;

/// <summary>The saved bill on screen, laid out exactly as printed. "Print again" reprints without the drawer.
/// A barcode scanned while it is open closes it and is handed back (it goes on the next bill).</summary>
public partial class ReceiptDialog : Window
{
    private const double PaperFontSize = 12.5;
    private const double QrSize = 160;
    private static readonly FontFamily Mono = new("Consolas");

    // A dialog is its own window, so the main window's scanner watch does not see this input.
    private readonly ScanBuffer scanBuffer = new(() => DateTimeOffset.Now);
    private readonly Func<string?> reprint;
    private readonly bool hasPrinter;
    private readonly bool isCreditNote;

    /// <param name="hasPrinter">False when receipts are saved as files; "Print again" then reports that instead of "Sent to printer".</param>
    /// <param name="isCreditNote">A return: the window and the printer message say "credit note" instead of invoice / bill.</param>
    /// <param name="copy">The lines are a copy (they carry "*** COPY ***"): the title says so.</param>
    /// <param name="reprinted">Opened by "Reprint last": a green banner says where the reprint went (unless it failed).</param>
    public ReceiptDialog(IReadOnlyList<PrintLine> lines, string? printError, Func<string?> reprint, bool hasPrinter, bool isCreditNote = false,
        bool copy = false, bool reprinted = false)
    {
        InitializeComponent();
        this.reprint = reprint;
        this.hasPrinter = hasPrinter;
        this.isCreditNote = isCreditNote;
        Title = (isCreditNote ? "Credit note" : "Invoice") + (copy ? " (copy)" : "");
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;
        foreach (var line in lines) Paper.Children.Add(line.Style == LineStyle.Qr ? QrImage(line.Text) : LineBlock(line));
        if (printError is not null) ShowBanner(PrinterProblem(printError), ok: false);
        else if (reprinted) ShowBanner(hasPrinter ? "Reprinted - sent to the printer" : "Reprinted - saved to the receipts folder (no printer set up)", ok: true);

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

    /// <summary>Lines are shown exactly as the renderer gives them (spaces kept). Title and Barcode text is bare, so it is centred
    /// here; Title prints double width and height, Big prints double height, Barcode shows the number large.</summary>
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
            case LineStyle.Barcode:
                // The printer draws CODE128 bars here; on screen the invoice number is shown large instead.
                block.FontSize = PaperFontSize * 1.6;
                block.FontWeight = FontWeights.Bold;
                block.TextAlignment = TextAlignment.Center;
                block.Margin = new Thickness(0, 6, 0, 6);
                break;
        }
        return block;
    }

    /// <summary>The receipt's QR code (the same payload the printer gets), centred, square and with crisp modules.</summary>
    private static Image QrImage(string payload)
    {
        byte[] png;
        using (var generator = new QRCodeGenerator())
        using (var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M))
        using (var code = new PngByteQRCode(data))
        {
            png = code.GetGraphic(8);
        }

        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(png))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;   // read now, so the stream can be closed
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }
        bitmap.Freeze();

        var image = new Image
        {
            Source = bitmap,
            Width = QrSize,
            Height = QrSize,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 4),
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        return image;
    }

    private void PrintAgain(object sender, RoutedEventArgs e)
    {
        PrintAgainButton.IsEnabled = false;
        var error = reprint();
        ShowBanner(error is null ? (hasPrinter ? "Sent to printer" : "Saved to receipts folder") : PrinterProblem(error), ok: error is null);
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

    private string PrinterProblem(string error) => $"Printer problem: {error}. The {(isCreditNote ? "credit note" : "bill")} is saved.";
}
