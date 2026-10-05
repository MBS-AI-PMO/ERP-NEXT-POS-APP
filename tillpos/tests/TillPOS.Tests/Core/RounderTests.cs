using TillPOS.Core.Money;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class RounderTests
{
    [Theory]
    [InlineData("2.345", "2.34")]
    [InlineData("2.355", "2.36")]
    [InlineData("-2.345", "-2.34")]
    [InlineData("0.015", "0.02")]
    public void Bankers_rounds_half_to_even(string input, string expected) =>
        Assert.Equal(M(expected), Rounder.Round(M(input), 2, RoundingMethod.Bankers));

    [Theory]
    [InlineData("2.345", "2.35")]
    [InlineData("-2.345", "-2.35")]
    public void Commercial_rounds_half_away_from_zero(string input, string expected) =>
        Assert.Equal(M(expected), Rounder.Round(M(input), 2, RoundingMethod.Commercial));

    [Theory]
    [InlineData("10.12", "10.00")]
    [InlineData("10.13", "10.25")]
    [InlineData("10.38", "10.50")]
    [InlineData("10.375", "10.26")]
    [InlineData("-10.13", "-10.25")]
    public void Rounds_to_smallest_currency_fraction_like_erpnext(string input, string expected)
    {
        var money = new MoneySettings(SmallestCurrencyFraction: M("0.25"));
        Assert.Equal(M(expected), Rounder.RoundToSmallestFraction(M(input), money));
    }

    [Theory]
    [InlineData("10.5", "Bankers", "10.00")]
    [InlineData("11.5", "Bankers", "12.00")]
    [InlineData("10.5", "Commercial", "11.00")]
    [InlineData("10.49", "Commercial", "10.00")]
    public void Without_fraction_rounds_to_whole_units(string input, string method, string expected)
    {
        var money = new MoneySettings(Rounding: Enum.Parse<RoundingMethod>(method));
        Assert.Equal(M(expected), Rounder.RoundToSmallestFraction(M(input), money));
    }
}
