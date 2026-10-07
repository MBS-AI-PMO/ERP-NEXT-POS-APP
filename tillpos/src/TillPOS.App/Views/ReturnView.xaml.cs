using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TillPOS.Presentation;

namespace TillPOS.App.Views;

/// <summary>Returns (F6): find the receipt, choose what comes back (or scan a return list without a receipt), then confirm.
/// The keyboard always sits in the stage's main box: the receipt number, the first return quantity, or the scan box. Every
/// button is non-focusable. A scanned code is handed to the view model by the main window (a receipt number on the find
/// stage, an item on the others), never typed into a box.</summary>
public partial class ReturnView : UserControl
{
    public ReturnView()
    {
        InitializeComponent();
        Loaded += (_, _) => FocusStage();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ReturnViewModel old) old.PropertyChanged -= OnViewModelChanged;
            if (e.NewValue is ReturnViewModel vm) vm.PropertyChanged += OnViewModelChanged;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F2 && DataContext is ReturnViewModel { IsNoReceipt: true })
            {
                SearchBox.Focus();
                e.Handled = true;
            }
        };
    }

    private ReturnViewModel? Vm => DataContext as ReturnViewModel;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReturnViewModel.Stage)) FocusStage();
    }

    /// <summary>After layout (a collapsed stage cannot take focus): the receipt-number box, the first return quantity, or the
    /// scan box; the view itself when the stage has none, so Enter and Esc still work.</summary>
    private void FocusStage() =>
        Keyboarding.FocusWhenReady(this, () => Vm switch
        {
            { IsFind: true } => FindBox,
            { IsChoose: true } => (UIElement?)Keyboarding.FirstTextBox(ChooseStage) ?? this,
            { IsNoReceipt: true } => ScanBox,
            _ => this,
        });

    /// <summary>Down arrow from the receipt-number box moves into the recent bills.</summary>
    private void FindBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || RecentList.Items.Count == 0) return;
        RecentList.SelectedIndex = 0;
        RecentList.ScrollIntoView(RecentList.Items[0]);
        (RecentList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
        e.Handled = true;
    }

    /// <summary>Enter on a recent bill opens it (and never confirms a refund from the find stage).</summary>
    private void RecentListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (RecentList.SelectedItem is RecentBill bill) Vm?.OpenBillCommand.Execute(bill.ClientId);
        e.Handled = true;
    }

    /// <summary>A tap on a recent bill opens it (a tap on the scroll bar does not).</summary>
    private void RecentListTapped(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (RecentList.ContainerFromElement(source) is ListBoxItem { DataContext: RecentBill bill }) Vm?.OpenBillCommand.Execute(bill.ClientId);
    }

    /// <summary>Enter in an empty scan box confirms the refund; with a typed code it adds that item (the box's own binding).</summary>
    private void ScanBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is not { } vm || vm.ScanText.Trim().Length > 0) return;
        vm.ConfirmCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Enter in the search box adds the first match to the return list and goes back to the scan box; in an empty
    /// search box it confirms the refund; with text but no match it does nothing.</summary>
    private void SearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is not { } vm) return;
        e.Handled = true;
        if (vm.SearchResults.Count > 0)
        {
            vm.AddFromSearchCommand.Execute(vm.SearchResults[0].ItemCode);
            ScanBox.Focus();
        }
        else if (vm.SearchText.Trim().Length == 0)
        {
            vm.ConfirmCommand.Execute(null);
        }
    }
}
