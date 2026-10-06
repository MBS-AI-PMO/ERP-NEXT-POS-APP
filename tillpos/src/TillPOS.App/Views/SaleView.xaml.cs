using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TillPOS.App.Views;

public partial class SaleView : UserControl
{
    public SaleView()
    {
        InitializeComponent();
        Loaded += (_, _) => ScanBox.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F2) { SearchBox.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { ScanBox.Focus(); e.Handled = true; }
        };
    }

    private void FocusSearch(object sender, RoutedEventArgs e) => SearchBox.Focus();
}
