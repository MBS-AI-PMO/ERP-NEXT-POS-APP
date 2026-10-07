using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TillPOS.App;

/// <summary>Keyboard helpers for the till screens: putting the keyboard in the right box once a screen is laid out, and
/// "tap selects all" for amount and count boxes.</summary>
public static class Keyboarding
{
    /// <summary>When true, the first tap (or click) on a box that does not have the keyboard yet focuses it and selects all
    /// its text, so typing replaces the old amount instead of being added to it.</summary>
    public static readonly DependencyProperty SelectAllOnTapProperty = DependencyProperty.RegisterAttached(
        "SelectAllOnTap", typeof(bool), typeof(Keyboarding), new PropertyMetadata(false, OnSelectAllOnTapChanged));

    public static bool GetSelectAllOnTap(DependencyObject element) => (bool)element.GetValue(SelectAllOnTapProperty);

    public static void SetSelectAllOnTap(DependencyObject element, bool value) => element.SetValue(SelectAllOnTapProperty, value);

    /// <summary>Focuses the element once the screen is laid out (input priority runs after layout), and tries once more when
    /// the dispatcher is idle if it could not take the keyboard yet (e.g. still collapsed, or another screen's focus was
    /// being cleaned up). <paramref name="target"/> is asked each time, so it can pick the element after layout.</summary>
    public static void FocusWhenReady(DispatcherObject owner, Func<UIElement?> target)
    {
        owner.Dispatcher.InvokeAsync(() =>
        {
            if (TryFocus(target())) return;
            owner.Dispatcher.InvokeAsync(() => TryFocus(target()), DispatcherPriority.ContextIdle);
        }, DispatcherPriority.Input);
    }

    /// <summary>The first visible text box inside <paramref name="root"/>, in visual (layout) order.</summary>
    public static TextBox? FirstTextBox(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox { IsVisible: true, IsEnabled: true } box) return box;
            if (FirstTextBox(child) is { } found) return found;
        }
        return null;
    }

    private static bool TryFocus(UIElement? element)
    {
        if (element is null || !element.IsVisible) return false;
        element.Focus();                              // logical focus too, so it comes back when the window is reactivated
        return element.IsKeyboardFocused;
    }

    private static void OnSelectAllOnTapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        if (e.NewValue is true) box.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box || box.IsKeyboardFocusWithin) return;
        e.Handled = true;                             // otherwise the click puts the caret at the tap and drops the selection
        box.Focus();
        box.SelectAll();
    }
}
