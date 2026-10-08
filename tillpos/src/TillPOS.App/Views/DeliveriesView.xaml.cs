using System.Windows.Controls;
using System.Windows.Input;

namespace TillPOS.App.Views;

/// <summary>The Deliveries screen (see DeliveriesViewModel). Every button is non-focusable and the scan box holds the
/// keyboard (it takes focus back after a click in the list), so a scanner read is never lost; Esc goes back to the sale.</summary>
public partial class DeliveriesView : UserControl
{
    public DeliveriesView()
    {
        InitializeComponent();
        Loaded += (_, _) => Keyboarding.FocusWhenReady(this, () => ScanBox);
        // Clicking a row takes keyboard focus; give it back so the next scanner read lands in the scan box.
        DeliveryList.PreviewMouseUp += (_, _) => Keyboarding.FocusWhenReady(this, () => ScanBox);
        DeliveryList.SelectionChanged += (_, _) => Keyboarding.FocusWhenReady(this, () => ScanBox);
        // A scanner types the slip's number into the focused box and presses Enter.
        ScanBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var code = ScanBox.Text.Trim();
            if (code.Length == 0) return;                      // Enter on an empty box does nothing
            if (DataContext is TillPOS.Presentation.DeliveriesViewModel vm) vm.ScanCommand.Execute(code);
            ScanBox.Clear();
        };
    }
}
