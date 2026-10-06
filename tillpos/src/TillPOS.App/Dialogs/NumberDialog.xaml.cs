using System.Globalization;
using System.Windows;
using System.Windows.Input;
using TillPOS.Presentation;

namespace TillPOS.App.Dialogs;

public partial class NumberDialog : Window
{
    // A dialog is its own window, so the main window's scanner watch does not see this input.
    private readonly ScanBuffer scanBuffer = new(() => DateTimeOffset.Now);

    public NumberDialog(string title, string prompt)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        Loaded += (_, _) => ValueBox.Focus();
        PreviewTextInput += (_, e) => { foreach (var c in e.Text) scanBuffer.OnChar(c); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || scanBuffer.OnChar('\r') is null) return;
            // A barcode scanned into the box is not a quantity: empty the box and stay open.
            ValueBox.Clear();
            e.Handled = true;
        };
    }

    public decimal? Value { get; private set; }

    private void Ok(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(ValueBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) return;
        Value = v;
        DialogResult = true;
    }
}
