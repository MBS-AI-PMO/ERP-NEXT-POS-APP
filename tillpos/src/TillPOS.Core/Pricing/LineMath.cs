using TillPOS.Core.Money;

namespace TillPOS.Core.Pricing;

public static class LineMath
{
    /// <summary>Unit rate after an offer, rounded to currency precision and never negative.
    /// Amount and Rate rules are defined per stock unit, so they scale with the conversion factor.</summary>
    public static decimal RateAfterRule(decimal priceListRate, decimal conversionFactor, AppliedRule? rule, MoneySettings money)
    {
        if (rule is null) return priceListRate;
        var rate = rule.Kind switch
        {
            RuleKind.DiscountPercentage => priceListRate * (1m - rule.Value / 100m),
            RuleKind.DiscountAmount => priceListRate - rule.Value * conversionFactor,
            RuleKind.Rate => rule.Value * conversionFactor,
            _ => priceListRate,
        };
        return Math.Max(0m, Rounder.Round(rate, money));
    }
}
