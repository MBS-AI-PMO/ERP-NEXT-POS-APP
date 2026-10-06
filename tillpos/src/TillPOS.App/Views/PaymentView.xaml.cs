using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TillPOS.Presentation;

namespace TillPOS.App.Views;

/// <summary>Keeps the keyboard in a box: cash on arrival, the card part when split is chosen, so typed digits and Enter
/// always land where the cashier expects (the keypad and kind buttons never take focus).</summary>
public partial class PaymentView : UserControl
{
    public PaymentView()
    {
        InitializeComponent();
        Loaded += (_, _) => CashBox.Focus();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is PaymentViewModel old) old.PropertyChanged -= OnViewModelChanged;
            if (e.NewValue is PaymentViewModel vm) vm.PropertyChanged += OnViewModelChanged;
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaymentViewModel.Kind) || sender is not PaymentViewModel vm) return;
        // After layout: the card box is collapsed until IsSplit's binding shows it, and a collapsed box cannot take focus.
        // Leaving split collapses the card box, so move the keyboard back to cash rather than lose it.
        Dispatcher.InvokeAsync(() => (vm.IsSplit ? CardBox : CashBox).Focus(), DispatcherPriority.Input);
    }

    private void CardFocused(object sender, RoutedEventArgs e) { if (DataContext is PaymentViewModel vm) vm.EditCard = true; }

    private void CashFocused(object sender, RoutedEventArgs e) { if (DataContext is PaymentViewModel vm) vm.EditCard = false; }
}
