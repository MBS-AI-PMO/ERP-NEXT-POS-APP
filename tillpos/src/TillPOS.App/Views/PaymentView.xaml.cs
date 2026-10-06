using System.Windows;
using System.Windows.Controls;
using TillPOS.Presentation;

namespace TillPOS.App.Views;

public partial class PaymentView : UserControl
{
    public PaymentView() => InitializeComponent();

    private void CardFocused(object sender, RoutedEventArgs e) { if (DataContext is PaymentViewModel vm) vm.EditCard = true; }

    private void CashFocused(object sender, RoutedEventArgs e) { if (DataContext is PaymentViewModel vm) vm.EditCard = false; }
}
