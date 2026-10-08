using System.Windows.Controls;
using System.Windows.Input;
using TillPOS.Presentation;

namespace TillPOS.App.Views;

/// <summary>Close shift: the blind count. Enter moves to the next box and Esc goes back to the sale; every button is
/// non-focusable, so typing always lands in a box. A scan here is ignored by the main window.</summary>
public partial class CloseShiftView : UserControl
{
    public CloseShiftView()
    {
        InitializeComponent();
        // After layout: the AED 500 box.
        Loaded += (_, _) => Keyboarding.FocusWhenReady(this, () => Keyboarding.FirstTextBox(CountStage));
        // Select the box's text on entry, so a count is replaced by typing rather than appended to.
        CountStage.AddHandler(GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (e.NewFocus is TextBox box) box.SelectAll();
        }));
    }

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
