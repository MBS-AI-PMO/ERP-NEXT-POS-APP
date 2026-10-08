using System.Windows.Controls;
using System.Windows.Input;

namespace TillPOS.App.Views;

/// <summary>The Deliveries screen (see DeliveriesViewModel). Every button is non-focusable; Esc goes back to the sale.</summary>
public partial class DeliveriesView : UserControl
{
    public DeliveriesView()
    {
        InitializeComponent();
        Loaded += (_, _) => Keyboarding.FocusWhenReady(this, () => ScanBox);
        // A scanner types the slip's number into the focused box and presses Enter.
        ScanBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (DataContext is TillPOS.Presentation.DeliveriesViewModel vm) vm.ScanCommand.Execute(ScanBox.Text.Trim());
            ScanBox.Clear();
            e.Handled = true;
        };
    }
}
