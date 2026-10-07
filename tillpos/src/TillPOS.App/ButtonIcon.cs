using System.Windows;

namespace TillPOS.App;

/// <summary>A leading icon for a button, drawn by the theme's button template from the icon font (Segoe Fluent Icons /
/// Segoe MDL2 Assets): a single text glyph, so no images to load.</summary>
public static class ButtonIcon
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(ButtonIcon), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached(
        "Size", typeof(double), typeof(ButtonIcon), new FrameworkPropertyMetadata(18d));

    public static string? GetGlyph(DependencyObject element) => (string?)element.GetValue(GlyphProperty);

    public static void SetGlyph(DependencyObject element, string? value) => element.SetValue(GlyphProperty, value);

    public static double GetSize(DependencyObject element) => (double)element.GetValue(SizeProperty);

    public static void SetSize(DependencyObject element, double value) => element.SetValue(SizeProperty, value);
}
