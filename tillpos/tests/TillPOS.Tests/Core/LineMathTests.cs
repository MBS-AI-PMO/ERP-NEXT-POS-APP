using TillPOS.Core.Money;
using TillPOS.Core.Pricing;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class LineMathTests
{
    private static readonly MoneySettings Money = new();

    [Theory]
    [InlineData("2150", "1", "DiscountPercentage", "10", "1935.00")]
    [InlineData("610", "1", "DiscountPercentage", "5", "579.50")]
    [InlineData("340", "1", "DiscountPercentage", "15", "289.00")]
    [InlineData("10.00", "1", "DiscountAmount", "2.50", "7.50")]
    [InlineData("24.00", "12", "DiscountAmount", "0.50", "18.00")]
    [InlineData("10.00", "1", "Rate", "8.99", "8.99")]
    [InlineData("24.00", "12", "Rate", "1.50", "18.00")]
    [InlineData("5.00", "1", "DiscountAmount", "7.00", "0.00")]
    public void Applies_rule(string plr, string cf, string kind, string value, string expected)
    {
        var rule = new AppliedRule("R", Enum.Parse<RuleKind>(kind), M(value));
        Assert.Equal(M(expected), LineMath.RateAfterRule(M(plr), M(cf), rule, Money));
    }

    [Fact]
    public void No_rule_keeps_price_list_rate() =>
        Assert.Equal(M("12.34"), LineMath.RateAfterRule(M("12.34"), 1m, null, Money));
}
