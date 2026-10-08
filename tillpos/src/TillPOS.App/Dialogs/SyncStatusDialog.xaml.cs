using System.Windows;
using TillPOS.Presentation;

namespace TillPOS.App.Dialogs;

/// <summary>The read-only Sync status window. DialogResult true = "Open Upload problems (supervisor)" was pressed (the PIN is asked
/// after this window has closed).</summary>
public partial class SyncStatusDialog : Window
{
    public SyncStatusDialog(SyncStatusViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        MaxHeight = SystemParameters.WorkArea.Height * 0.95;
    }

    private void OpenProblemsClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
