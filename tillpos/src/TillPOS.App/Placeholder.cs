using System.Windows;

namespace TillPOS.App;

/// <summary>Hint text (and an optional leading icon glyph) for a text box. The theme's text box template draws them: the
/// icon always, the text only while the box is empty. Plain properties with no handlers, so it costs nothing per keystroke.</summary>
public static class Placeholder
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Placeholder), new FrameworkPropertyMetadata(null));

    /// <summary>A glyph from the icon font (Segoe Fluent Icons / Segoe MDL2 Assets), e.g. "&#xE721;" for search.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(Placeholder), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.RegisterAttached(
        "FontSize", typeof(double), typeof(Placeholder), new FrameworkPropertyMetadata(16d));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.RegisterAttached(
        "IconSize", typeof(double), typeof(Placeholder), new FrameworkPropertyMetadata(18d));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    public static string? GetIcon(DependencyObject element) => (string?)element.GetValue(IconProperty);

    public static void SetIcon(DependencyObject element, string? value) => element.SetValue(IconProperty, value);

    public static double GetFontSize(DependencyObject element) => (double)element.GetValue(FontSizeProperty);

    public static void SetFontSize(DependencyObject element, double value) => element.SetValue(FontSizeProperty, value);

    public static double GetIconSize(DependencyObject element) => (double)element.GetValue(IconSizeProperty);

    public static void SetIconSize(DependencyObject element, double value) => element.SetValue(IconSizeProperty, value);
}
