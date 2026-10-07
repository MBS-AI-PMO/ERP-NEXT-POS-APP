using TillPOS.Core.Money;
using TillPOS.Core.Payments;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class PaymentCalculatorTests
{
    private readonly PaymentCalculator calc = new(new MoneySettings(3, RoundingMethod.Bankers, 0.25m));

    [Fact]
    public void Cash_is_rounded_to_a_quarter_with_change()
    {
        var p = calc.Plan(M("14.37"), Tender.Cash(20m));
        Assert.True(p.UsesErpRoundedTotal);
        Assert.Equal(M("14.25"), p.AmountDue);
        Assert.Equal(M("14.25"), p.CashDue);
        Assert.Equal(M("5.75"), p.Change);
        Assert.Equal(M("-0.12"), p.RoundingDifference);
        Assert.True(p.IsComplete);
    }

    [Theory]
    [InlineData("9.994", "10.000")]   // real live bill
    [InlineData("9.875", "9.750")]    // exact half of the fraction rounds down, as in ERPNext
    [InlineData("9.876", "10.000")]
    public void Cash_uses_erpnext_rounding_rule(string grand, string due) =>
        Assert.Equal(M(due), calc.Plan(M(grand), Tender.Cash(100m)).AmountDue);

    [Fact]
    public void Too_little_cash_leaves_a_shortfall()
    {
        var p = calc.Plan(M("14.37"), Tender.Cash(10m));
        Assert.False(p.IsComplete);
        Assert.Equal(M("4.25"), p.Shortfall);
        Assert.Equal(0m, p.Change);
    }

    [Fact]
    public void Card_is_exact_and_not_rounded()
    {
        var p = calc.Plan(M("14.37"), Tender.Card());
        Assert.False(p.UsesErpRoundedTotal);
        Assert.Equal(M("14.37"), p.AmountDue);
        Assert.Equal(M("14.37"), p.CardAmount);
        Assert.Equal(0m, p.CashDue);
        Assert.Equal(0m, p.RoundingDifference);
    }

    [Fact]
    public void Split_charges_card_exactly_and_rounds_the_cash_remainder()
    {
        var p = calc.Plan(M("14.37"), Tender.Split(10m, 5m));
        Assert.False(p.UsesErpRoundedTotal);
        Assert.Equal(10m, p.CardAmount);
        Assert.Equal(M("4.25"), p.CashDue);
        Assert.Equal(M("14.25"), p.AmountDue);
        Assert.Equal(M("0.75"), p.Change);
        Assert.Equal(M("-0.12"), p.RoundingDifference);
    }

    [Fact]
    public void Split_with_a_fractional_card_amount()
    {
        var p = calc.Plan(M("14.37"), Tender.Split(M("10.10"), 5m));
        Assert.Equal(M("4.25"), p.CashDue);
        Assert.Equal(M("14.35"), p.AmountDue);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("14.37")]
    [InlineData("20")]
    public void Split_card_part_must_be_between_zero_and_the_total(string card) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => calc.Plan(M("14.37"), Tender.Split(M(card), 5m)));

    [Fact]
    public void Cash_refund_is_rounded_like_erpnext()
    {
        var p = calc.PlanRefund(M("-10.13"), TenderKind.Cash);
        Assert.Equal(M("-10.25"), p.AmountDue);
        Assert.Equal(M("-10.25"), p.CashTendered);
        Assert.True(p.IsComplete);
    }

    [Fact]
    public void Card_refund_is_exact()
    {
        var p = calc.PlanRefund(M("-10.13"), TenderKind.Card);
        Assert.Equal(M("-10.13"), p.AmountDue);
        Assert.Equal(M("-10.13"), p.CardAmount);
    }

    [Fact]
    public void Refund_needs_a_negative_total() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => calc.PlanRefund(M("5"), TenderKind.Cash));

    [Fact]
    public void Split_whose_cash_part_rounds_to_zero_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => calc.Plan(M("10.10"), Tender.Split(M("10.00"), 0m)));

    // POS Profile "Disable Rounded Total" (the live Test Counter has it on; the shop counters have it off): ERPNext keeps the
    // exact grand total, so cash is not rounded to the quarter either.
    private readonly PaymentCalculator exact = new(new MoneySettings(3, RoundingMethod.Bankers, 0.25m, DisableRoundedTotal: true));

    [Fact]
    public void Cash_is_exact_when_the_profile_disables_the_rounded_total()
    {
        var p = exact.Plan(M("14.37"), Tender.Cash(20m));
        Assert.False(p.UsesErpRoundedTotal);
        Assert.Equal(M("14.37"), p.AmountDue);
        Assert.Equal(M("14.37"), p.CashDue);
        Assert.Equal(M("5.63"), p.Change);
        Assert.Equal(0m, p.RoundingDifference);
        Assert.True(p.IsComplete);
    }

    [Fact]
    public void Exact_cash_still_needs_the_full_amount()
    {
        var p = exact.Plan(M("14.37"), Tender.Cash(M("14.25")));
        Assert.False(p.IsComplete);
        Assert.Equal(M("0.12"), p.Shortfall);
    }

    [Fact]
    public void Split_cash_part_is_exact_when_rounding_is_disabled()
    {
        var p = exact.Plan(M("10.10"), Tender.Split(M("10.00"), 1m));
        Assert.Equal(M("0.10"), p.CashDue);
        Assert.Equal(M("10.10"), p.AmountDue);
        Assert.Equal(M("0.90"), p.Change);
        Assert.Equal(0m, p.RoundingDifference);
    }

    [Fact]
    public void Cash_refund_is_exact_when_rounding_is_disabled()
    {
        var p = exact.PlanRefund(M("-10.13"), TenderKind.Cash);
        Assert.False(p.UsesErpRoundedTotal);
        Assert.Equal(M("-10.13"), p.AmountDue);
        Assert.Equal(M("-10.13"), p.CashTendered);
        Assert.Equal(0m, p.RoundingDifference);
    }
}
