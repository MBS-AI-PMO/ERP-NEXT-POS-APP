using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TillPOS.App;

/// <summary>Visible when the count is above zero; with <see cref="Invert"/>, visible only when it is zero (empty states).</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is int count && count > 0) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
