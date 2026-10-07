using System.Globalization;
using System.Windows.Data;

namespace TillPOS.App;

/// <summary>True when the two bound values are equal (e.g. a reason button and the chosen reason, to highlight it).</summary>
public sealed class EqualConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && Equals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
