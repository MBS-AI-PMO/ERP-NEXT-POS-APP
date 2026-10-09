using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TillPOS.App.Views;

public partial class SaleView : UserControl
{
    public SaleView()
    {
        InitializeComponent();
        Loaded += (_, _) => Keyboarding.FocusWhenReady(this, () => ScanBox);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && DeleteTargetsLines())
            {
                if (DataContext is TillPOS.Presentation.SaleViewModel vm)
                {
                    if (vm.SelectedLine is not null) vm.RemoveSelectedCommand.Execute(null);
                    else vm.RemoveLastCommand.Execute(null);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.F2) { SearchBox.Focus(); e.Handled = true; }
            // F10 reaches WPF as a system key (Windows' menu key), so a KeyBinding never sees it.
            else if (e.Key == Key.System && e.SystemKey == Key.F10 && DataContext is TillPOS.Presentation.SaleViewModel prices)
            {
                prices.UpdatePricesCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape) { ScanBox.Focus(); e.Handled = true; }
        };
    }

    private bool DeleteTargetsLines() =>
        Keyboard.FocusedElement is not TextBox box || (ReferenceEquals(box, ScanBox) && ScanBox.Text.Length == 0);

    private void FocusSearch(object sender, RoutedEventArgs e) => SearchBox.Focus();
}
