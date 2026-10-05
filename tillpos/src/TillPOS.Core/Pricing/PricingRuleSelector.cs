using TillPOS.Core.Catalog;
using TillPOS.Core.Money;

namespace TillPOS.Core.Pricing;

/// <summary>Chooses the single Pricing Rule for an item: highest priority, then most specific
/// (item code &gt; brand &gt; item group), then the biggest saving for the customer.</summary>
public sealed class PricingRuleSelector(ICatalog catalog, MoneySettings money, string priceList, string warehouse)
{
    public AppliedRule? Select(Item item, decimal priceListRate, decimal conversionFactor, DateOnly date)
    {
        var itemGroup = catalog.FindGroup(item.ItemGroup);
        var best = catalog.PricingRules()
            .Where(r => r.UnsupportedReason is null)
            .Where(r => (r.ValidFrom is null || r.ValidFrom <= date) && (r.ValidUpto is null || r.ValidUpto >= date))
            .Where(r => string.IsNullOrEmpty(r.ForPriceList) || r.ForPriceList == priceList)
            .Where(r => string.IsNullOrEmpty(r.Warehouse) || r.Warehouse == warehouse)
            .Select(r => (Rule: r, Specificity: Specificity(r, item, itemGroup)))
            .Where(x => x.Specificity > 0)
            .OrderByDescending(x => x.Rule.Priority)
            .ThenByDescending(x => x.Specificity)
            .ThenByDescending(x => priceListRate - LineMath.RateAfterRule(priceListRate, conversionFactor, ToApplied(x.Rule), money))
            .ThenBy(x => x.Rule.Name, StringComparer.Ordinal)
            .Select(x => x.Rule)
            .FirstOrDefault();
        return best is null ? null : ToApplied(best);
    }

    private static AppliedRule ToApplied(PricingRule r) => new(r.Name, r.Kind, r.Value);

    // 3 = item code, 2 = brand, 1 = item group (incl. parent groups), 0 = no match
    private int Specificity(PricingRule rule, Item item, ItemGroupNode? itemGroup) => rule.ApplyOn switch
    {
        RuleApplyOn.ItemCode => rule.Targets.Contains(item.ItemCode) ? 3 : 0,
        RuleApplyOn.Brand => item.Brand is not null && rule.Targets.Contains(item.Brand) ? 2 : 0,
        RuleApplyOn.ItemGroup => itemGroup is not null && rule.Targets.Any(t => Contains(catalog.FindGroup(t), itemGroup)) ? 1 : 0,
        _ => 0,
    };

    private static bool Contains(ItemGroupNode? ancestor, ItemGroupNode node) =>
        ancestor is not null && ancestor.Lft <= node.Lft && node.Rgt <= ancestor.Rgt;
}
