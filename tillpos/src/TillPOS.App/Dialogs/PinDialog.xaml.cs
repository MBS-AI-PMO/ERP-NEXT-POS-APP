using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TillPOS.Presentation;

namespace TillPOS.App.Dialogs;

public partial class PinDialog : Window
{
    // A dialog is its own window, so the main window's scanner watch does not see this input.
    private readonly ScanBuffer scanBuffer = new(() => DateTimeOffset.Now);

    public PinDialog(string title, string reason)
    {
        InitializeComponent();
        Title = title;
        ReasonText.Text = reason;
        Loaded += (_, _) => PinBox.Focus();
        PreviewTextInput += (_, e) => { foreach (var c in e.Text) scanBuffer.OnChar(c); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || scanBuffer.OnChar('\r') is null) return;
            // A barcode scanned while the PIN is asked must not be tried as a PIN: empty the box and stay open.
            PinBox.Clear();
            e.Handled = true;
        };
    }

    public string Pin => PinBox.Password;

    private void Digit(object sender, RoutedEventArgs e)
    {
        if (PinBox.Password.Length < 6) PinBox.Password += ((Button)sender).Content;
    }

    private void Ok(object sender, RoutedEventArgs e) => DialogResult = true;
}
