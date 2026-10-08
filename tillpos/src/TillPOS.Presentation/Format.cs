using System.Globalization;

namespace TillPOS.Presentation;

public static class Format
{
    /// <summary>An amount as shown to people: 2 decimals (11.429 → 11.43, half away from zero). ERPNext's figures keep their
    /// 3 decimals inside the till; only the display is rounded.</summary>
    public static string Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);
}
