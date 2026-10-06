using System.Windows;
using System.Windows.Controls;

namespace TillPOS.App.Dialogs;

public partial class PinDialog : Window
{
    public PinDialog(string title, string reason)
    {
        InitializeComponent();
        Title = title;
        ReasonText.Text = reason;
        Loaded += (_, _) => PinBox.Focus();
    }

    public string Pin => PinBox.Password;

    private void Digit(object sender, RoutedEventArgs e)
    {
        if (PinBox.Password.Length < 6) PinBox.Password += ((Button)sender).Content;
    }

    private void Ok(object sender, RoutedEventArgs e) => DialogResult = true;
}
