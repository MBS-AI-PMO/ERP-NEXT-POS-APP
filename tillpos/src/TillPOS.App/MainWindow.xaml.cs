using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TillPOS.Presentation;

namespace TillPOS.App;

/// <summary>Hosts the screens. Watches all keyboard input for scanner bursts so a scan always reaches the sale screen,
/// whatever has focus, and undoes what the scan typed into a focused text box.</summary>
public partial class MainWindow : Window
{
    private readonly ScanBuffer scanBuffer = new(() => DateTimeOffset.Now);
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(15) };

    private static readonly TimeSpan BurstGap = TimeSpan.FromMilliseconds(50);
    private DateTimeOffset lastInput = DateTimeOffset.MinValue;
    private (TextBox Box, string Text, int Caret)? beforeBurst;

    public MainWindow()
    {
        InitializeComponent();
        PreviewTextInput += OnPreviewTextInput;
        PreviewKeyDown += OnPreviewKeyDown;
        clock.Tick += (_, _) => UpdateClock();
        clock.Start();
        Loaded += (_, _) => UpdateClock();
    }

    private void UpdateClock()
    {
        if (DataContext is ShellViewModel shell) shell.Clock = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>Remembers the focused box's text as it was before each burst of fast input starts (Preview runs before
    /// the box inserts the character), so a burst that turns out to be a scan can be undone wherever the caret was.</summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var now = DateTimeOffset.Now;
        if (now - lastInput > BurstGap)
            beforeBurst = Keyboard.FocusedElement is TextBox box ? (box, box.Text, box.CaretIndex) : null;
        lastInput = now;
        foreach (var c in e.Text) scanBuffer.OnChar(c);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var code = scanBuffer.OnChar('\r');
        if (code is null) return;

        // A scan never acts as typing: put the focused box back as it was before the burst and swallow its Enter.
        // Only the sale screen uses it; elsewhere (e.g. payment) it is ignored, so a scan can never
        // change a cash amount or press "Complete".
        if (beforeBurst is { } saved && ReferenceEquals(Keyboard.FocusedElement, saved.Box))
        {
            saved.Box.Text = saved.Text;
            saved.Box.CaretIndex = Math.Min(saved.Caret, saved.Text.Length);
        }
        beforeBurst = null;
        e.Handled = true;
        if (DataContext is ShellViewModel { Current: SaleViewModel sale }) sale.Scan(code);
        // The login screen took the scan's digits as PIN digits; throw them away.
        else if (DataContext is ShellViewModel { Current: LoginViewModel login }) login.ClearCommand.Execute(null);
    }
}
