using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Pricing;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class PricingRuleSelectorTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();
    private readonly Item rice = new("RICE5", "Basmati Rice 5kg", "Rice", "Tilda", "Nos", false, true);

    private static PricingRule Rule(string name, RuleApplyOn on, string target, string pct, int priority = 0,
        DateOnly? from = null, DateOnly? upto = null, string? priceList = null, string? warehouse = null, string? unsupported = null,
        RuleKind kind = RuleKind.DiscountPercentage) =>
        new(name, on, [target], kind, M(pct), priority, from, upto, priceList, warehouse, unsupported);

    private AppliedRule? Select(DateOnly? date = null) =>
        new PricingRuleSelector(catalog, new MoneySettings(), "Retail", "Stores - S").Select(rice, M("2150"), 1m, date ?? Today);

    [Fact]
    public void No_rules_means_no_offer() => Assert.Null(Select());

    [Fact]
    public void Item_code_rule_applies()
    {
        catalog.Rules.Add(Rule("R-ITEM", RuleApplyOn.ItemCode, "RICE5", "10"));
        Assert.Equal("R-ITEM", Select()!.RuleName);
    }

    [Fact]
    public void Brand_rule_applies()
    {
        catalog.Rules.Add(Rule("R-BRAND", RuleApplyOn.Brand, "Tilda", "7"));
        Assert.Equal("R-BRAND", Select()!.RuleName);
    }

    [Fact]
    public void Parent_group_rule_applies_to_child_group_items()
    {
        catalog.Rules.Add(Rule("R-FOOD", RuleApplyOn.ItemGroup, "Food", "5"));
        Assert.Equal("R-FOOD", Select()!.RuleName);
    }

    [Fact]
    public void Sibling_group_rule_does_not_apply()
    {
        catalog.Rules.Add(Rule("R-DAIRY", RuleApplyOn.ItemGroup, "Dairy", "5"));
        Assert.Null(Select());
    }

    [Fact]
    public void Item_code_beats_brand_beats_group_at_equal_priority()
    {
        catalog.Rules.Add(Rule("R-FOOD", RuleApplyOn.ItemGroup, "Food", "30"));
        catalog.Rules.Add(Rule("R-BRAND", RuleApplyOn.Brand, "Tilda", "20"));
        Assert.Equal("R-BRAND", Select()!.RuleName);
        catalog.Rules.Add(Rule("R-ITEM", RuleApplyOn.ItemCode, "RICE5", "10"));
        Assert.Equal("R-ITEM", Select()!.RuleName);
    }

    [Fact]
    public void Higher_priority_beats_more_specific()
    {
        catalog.Rules.Add(Rule("R-ITEM", RuleApplyOn.ItemCode, "RICE5", "10", priority: 1));
        catalog.Rules.Add(Rule("R-FOOD", RuleApplyOn.ItemGroup, "Food", "5", priority: 5));
        Assert.Equal("R-FOOD", Select()!.RuleName);
    }

    [Fact]
    public void Tie_goes_to_the_bigger_saving_for_the_customer()
    {
        catalog.Rules.Add(Rule("R-PCT", RuleApplyOn.ItemCode, "RICE5", "10"));                         // saves 215
        catalog.Rules.Add(Rule("R-AMT", RuleApplyOn.ItemCode, "RICE5", "300", kind: RuleKind.DiscountAmount)); // saves 300
        Assert.Equal("R-AMT", Select()!.RuleName);
    }

    [Fact]
    public void Rules_outside_their_dates_are_ignored()
    {
        catalog.Rules.Add(Rule("R-OLD", RuleApplyOn.ItemCode, "RICE5", "10", upto: new DateOnly(2026, 10, 4)));
        catalog.Rules.Add(Rule("R-NEW", RuleApplyOn.ItemCode, "RICE5", "10", from: new DateOnly(2026, 10, 6)));
        Assert.Null(Select());
        Assert.Equal("R-NEW", Select(new DateOnly(2026, 10, 6))!.RuleName);
    }

    [Fact]
    public void Rules_for_other_price_lists_or_warehouses_are_ignored()
    {
        catalog.Rules.Add(Rule("R-WHOLESALE", RuleApplyOn.ItemCode, "RICE5", "10", priceList: "Wholesale"));
        catalog.Rules.Add(Rule("R-OTHER-WH", RuleApplyOn.ItemCode, "RICE5", "10", warehouse: "Branch - S"));
        Assert.Null(Select());
    }

    [Fact]
    public void Unsupported_rules_are_never_applied()
    {
        catalog.Rules.Add(Rule("R-QTY", RuleApplyOn.ItemCode, "RICE5", "50", unsupported: "quantity conditions are not supported"));
        Assert.Null(Select());
    }
}
