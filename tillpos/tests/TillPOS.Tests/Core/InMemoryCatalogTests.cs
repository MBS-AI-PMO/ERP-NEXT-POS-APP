using TillPOS.Core.Catalog;
using TillPOS.Core.Pricing;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Core;

public class InMemoryCatalogTests
{
    [Fact]
    public void Stock_uom_conversion_factor_is_one_and_other_uoms_come_from_item_uoms()
    {
        var c = InMemoryCatalog.WithStandardGroups();
        c.Items.Add(new Item("WATER", "Water 500ml", "Food", null, "Nos", false, true));
        c.Uoms.Add(new ItemUom("WATER", "Box", 12m));

        Assert.Equal(1m, c.ConversionFactor("WATER", "Nos"));
        Assert.Equal(12m, c.ConversionFactor("WATER", "Box"));
        Assert.Null(c.ConversionFactor("WATER", "Pallet"));
    }

    [Theory]
    [InlineData(RuleKind.DiscountPercentage, "10", "10% OFF")]
    [InlineData(RuleKind.DiscountPercentage, "12.5", "12.5% OFF")]
    [InlineData(RuleKind.DiscountAmount, "2", "2.00 OFF")]
    [InlineData(RuleKind.Rate, "9.99", "OFFER")]
    public void Applied_rule_label(RuleKind kind, string value, string expected) =>
        Assert.Equal(expected, new AppliedRule("R1", kind, TestUtil.M(value)).Label);
}
