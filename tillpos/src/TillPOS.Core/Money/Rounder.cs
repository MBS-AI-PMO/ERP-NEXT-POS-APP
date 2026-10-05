namespace TillPOS.Core.Money;

public enum RoundingMethod { Bankers, Commercial }

public sealed record MoneySettings(
    int Precision = 2,
    RoundingMethod Rounding = RoundingMethod.Bankers,
    decimal SmallestCurrencyFraction = 0m,
    bool DisableRoundedTotal = false);

/// <summary>Rounding that reproduces Frappe's flt()/rounded() and ERPNext's
/// round_based_on_smallest_currency_fraction, so till totals equal ERPNext totals.</summary>
public static class Rounder
{
    public static decimal Round(decimal value, int precision, RoundingMethod method) =>
        Math.Round(value, precision,
            method == RoundingMethod.Bankers ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero);

    public static decimal Round(decimal value, MoneySettings money) =>
        Round(value, money.Precision, money.Rounding);

    public static decimal RoundToSmallestFraction(decimal value, MoneySettings money)
    {
        var fraction = money.SmallestCurrencyFraction;
        if (fraction > 0)
        {
            var remainder = PythonRemainder(value, fraction, money.Precision, money.Rounding);
            value = remainder > fraction / 2 ? value + (fraction - remainder) : value - remainder;
        }
        else
        {
            value = Round(value, 0, money.Rounding);
        }
        return Round(value, money);
    }

    // Python's % takes the sign of the divisor, so for a positive fraction the
    // remainder is never negative (-10.13 % 0.25 == 0.12). ERPNext relies on that.
    private static decimal PythonRemainder(decimal numerator, decimal denominator, int precision, RoundingMethod method)
    {
        var r = numerator % denominator;
        if (r != 0 && (r < 0) != (denominator < 0)) r += denominator;
        return Round(r, precision, method);
    }
}
