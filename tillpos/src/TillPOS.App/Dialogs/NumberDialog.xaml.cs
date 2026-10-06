using System.Globalization;
using System.Windows;

namespace TillPOS.App.Dialogs;

public partial class NumberDialog : Window
{
    public NumberDialog(string title, string prompt)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        Loaded += (_, _) => ValueBox.Focus();
    }

    public decimal? Value { get; private set; }

    private void Ok(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(ValueBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) return;
        Value = v;
        DialogResult = true;
    }
}
