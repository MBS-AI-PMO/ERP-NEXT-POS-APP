using System.Globalization;

namespace TillPOS.Core.Pricing;

public enum RuleApplyOn { ItemCode, ItemGroup, Brand }

public enum RuleKind { DiscountPercentage, DiscountAmount, Rate }

/// <summary>An ERPNext selling Pricing Rule reduced to what the till supports.
/// UnsupportedReason != null means the rule is stored but never applied.</summary>
public sealed record PricingRule(
    string Name,
    RuleApplyOn ApplyOn,
    IReadOnlyList<string> Targets,
    RuleKind Kind,
    decimal Value,
    int Priority,
    DateOnly? ValidFrom,
    DateOnly? ValidUpto,
    string? ForPriceList,
    string? Warehouse,
    string? UnsupportedReason);

public sealed record AppliedRule(string RuleName, RuleKind Kind, decimal Value)
{
    public string Label => Kind switch
    {
        RuleKind.DiscountPercentage => Value.ToString("0.##", CultureInfo.InvariantCulture) + "% OFF",
        RuleKind.DiscountAmount => Value.ToString("0.00", CultureInfo.InvariantCulture) + " OFF",
        _ => "OFFER",
    };
}
