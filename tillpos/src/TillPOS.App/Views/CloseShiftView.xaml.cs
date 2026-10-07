using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TillPOS.Presentation;

namespace TillPOS.App.Views;

/// <summary>Close shift: the blind count, then the result. In the count, Enter moves to the next box and Esc goes back to
/// the sale; every button is non-focusable, so typing always lands in a box. A scan here is ignored by the main window.</summary>
public partial class CloseShiftView : UserControl
{
    public CloseShiftView()
    {
        InitializeComponent();
        Loaded += (_, _) => FocusStage();
        // Select the box's text on entry, so a count is replaced by typing rather than appended to.
        CountStage.AddHandler(GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (e.NewFocus is TextBox box) box.SelectAll();
        }));
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is CloseShiftViewModel old) old.PropertyChanged -= OnViewModelChanged;
            if (e.NewValue is CloseShiftViewModel vm) vm.PropertyChanged += OnViewModelChanged;
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CloseShiftViewModel.IsCounting)) FocusStage();
    }

    /// <summary>After layout (a collapsed panel cannot take focus): the first count box while counting, otherwise the view
    /// itself, so the keyboard is never left on a hidden box.</summary>
    private void FocusStage() =>
        Dispatcher.InvokeAsync(() =>
        {
            if (DataContext is CloseShiftViewModel { IsCounting: true }) CountStage.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            else Focus();
        }, DispatcherPriority.Input);

    private void CountStageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is TextBox box)
        {
            box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && DataContext is CloseShiftViewModel vm)
        {
            vm.BackCommand.Execute(null);
            e.Handled = true;
        }
    }
}
