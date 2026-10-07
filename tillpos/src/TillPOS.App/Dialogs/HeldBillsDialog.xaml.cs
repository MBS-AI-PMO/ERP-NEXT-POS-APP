using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TillPOS.Presentation;

namespace TillPOS.App.Dialogs;

/// <summary>The bills on hold (F7). Enter, "Recall" or a double-click hands the selected bill back to the sale screen; a
/// scanned barcode is ignored (it never recalls). Deleting a held bill asks for a supervisor.</summary>
public partial class HeldBillsDialog : Window
{
    // A dialog is its own window, so the main window's scanner watch does not see this input.
    private readonly ScanBuffer scanBuffer = new(() => DateTimeOffset.Now);
    private readonly HeldBillsViewModel vm;

    public HeldBillsDialog(HeldBillsViewModel vm)
    {
        InitializeComponent();
        this.vm = vm;
        DataContext = vm;
        MaxHeight = SystemParameters.WorkArea.Height * 0.95;
        // Activated: on opening, and again after the supervisor PIN window of a delete closes.
        Activated += (_, _) => FocusSelected();
        PreviewTextInput += (_, e) => { foreach (var c in e.Text) scanBuffer.OnChar(c); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (scanBuffer.OnChar('\r') is not null) return;   // a scan: ignore it
            Recall();
        };
    }

    /// <summary>The id of the bill to recall, or null when the window was closed without recalling.</summary>
    public string? ChosenId { get; private set; }

    private void Recall()
    {
        if (vm.Selected is not { } bill) return;
        ChosenId = bill.Id;
        DialogResult = true;
    }

    private void RecallClick(object sender, RoutedEventArgs e) => Recall();

    private void BillDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: HeldBillRow row }) vm.Selected = row;
        Recall();
    }

    /// <summary>Keyboard focus on the selected row, so the arrow keys move through the bills.</summary>
    private void FocusSelected() =>
        Dispatcher.InvokeAsync(() =>
        {
            if (vm.Selected is { } selected && BillList.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item) item.Focus();
            else BillList.Focus();
        }, DispatcherPriority.Input);
}
