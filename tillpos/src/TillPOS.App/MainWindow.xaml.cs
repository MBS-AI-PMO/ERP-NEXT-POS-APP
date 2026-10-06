using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TillPOS.Presentation;

namespace TillPOS.App;

/// <summary>Hosts the screens. Watches all keyboard input for scanner bursts so a scan always reaches the sale screen,
/// whatever has focus, and removes the scanned digits from a focused text box.</summary>
public partial class MainWindow : Window
{
    private readonly ScanBuffer scanBuffer = new(() => DateTimeOffset.Now);
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(15) };

    public MainWindow()
    {
        InitializeComponent();
        PreviewTextInput += (_, e) => { foreach (var c in e.Text) scanBuffer.OnChar(c); };
        PreviewKeyDown += OnPreviewKeyDown;
        clock.Tick += (_, _) => UpdateClock();
        clock.Start();
        Loaded += (_, _) => UpdateClock();
    }

    private void UpdateClock()
    {
        if (DataContext is ShellViewModel shell) shell.Clock = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var code = scanBuffer.OnChar('\r');
        if (code is null) return;

        // A scan never acts as typing: strip its digits from the focused box and swallow its Enter.
        // Only the sale screen uses it; elsewhere (e.g. payment) it is ignored, so a scan can never
        // enter a cash amount and press "Complete".
        if (Keyboard.FocusedElement is TextBox box && box.Text.EndsWith(code, StringComparison.Ordinal))
            box.Text = box.Text[..^code.Length];
        e.Handled = true;
        if (DataContext is ShellViewModel { Current: SaleViewModel sale }) sale.Scan(code);
    }
}
