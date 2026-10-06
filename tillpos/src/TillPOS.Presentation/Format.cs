using System.Globalization;

namespace TillPOS.Presentation;

public static class Format
{
    public static string Money(decimal value) => value.ToString("0.00#", CultureInfo.InvariantCulture);
}
