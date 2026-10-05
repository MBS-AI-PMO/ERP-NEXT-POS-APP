using System.Globalization;

namespace TillPOS.Tests;

public static class TestUtil
{
    public static decimal M(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);
}
