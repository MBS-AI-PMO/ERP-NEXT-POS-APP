using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TillPOS.Presentation;

namespace TillPOS.App.Dialogs;

/// <summary>Price check (F4): a scan (into either box) shows the item's price and never closes the window or adds the item;
/// only "Add to bill" hands the item back to the sale.</summary>
public partial class PriceCheckDialog : Window
{
    private static readonly TimeSpan BurstGap = TimeSpan.FromMilliseconds(50);

    // A dialog is its own window, so the main window's scanner watch does not see this input.
    private readonly ScanBuffer scanBuffer = new(() => DateTimeOffset.Now);
    private readonly PriceCheckViewModel vm;
    private DateTimeOffset lastInput = DateTimeOffset.MinValue;
    private (TextBox Box, string Text, int Caret)? beforeBurst;

    public PriceCheckDialog(PriceCheckViewModel vm)
    {
        InitializeComponent();
        this.vm = vm;
        DataContext = vm;
        MaxHeight = SystemParameters.WorkArea.Height * 0.95;
        Loaded += (_, _) => ScanBox.Focus();
        PreviewTextInput += OnPreviewTextInput;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>What "Add to bill" adds, or null when the window was closed without adding.</summary>
    public PriceCheckPick? Pick { get; private set; }

    /// <summary>Remembers the focused box as it was before each burst of fast input (see MainWindow), so a scan typed into the
    /// search box can be taken back out of it.</summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var now = DateTimeOffset.Now;
        if (now - lastInput > BurstGap)
            beforeBurst = Keyboard.FocusedElement is TextBox box ? (box, box.Text, box.CaretIndex) : null;
        lastInput = now;
        foreach (var c in e.Text) scanBuffer.OnChar(c);
    }

    /// <summary>A scan's Enter looks the code up wherever the keyboard is (the scan box, or the search box) and puts that box
    /// back as it was before the burst. A typed code in the scan box is looked up by the box's own Enter binding.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var code = scanBuffer.OnChar('\r');
        if (code is null) return;
        if (beforeBurst is { } saved && ReferenceEquals(Keyboard.FocusedElement, saved.Box))
        {
            saved.Box.Text = saved.Text;
            saved.Box.CaretIndex = Math.Min(saved.Caret, saved.Text.Length);
        }
        beforeBurst = null;
        e.Handled = true;
        vm.Lookup(code);
    }

    private void AddToBillClick(object sender, RoutedEventArgs e)
    {
        if (vm.AddToBill is not { } pick) return;
        Pick = pick;
        DialogResult = true;
    }
}
