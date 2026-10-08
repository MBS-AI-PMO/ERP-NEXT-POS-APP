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

    [Theory]
    [InlineData("11.429", "11.43")]   // a card machine charges fils, never the third decimal
    [InlineData("22.858", "22.86")]
    [InlineData("10.125", "10.13")]   // half away from zero
    [InlineData("14.370", "14.37")]
    public void Card_is_charged_to_two_decimals_and_never_rounded_to_the_cash_fraction(string grand, string card)
    {
        var p = calc.Plan(M(grand), Tender.Card());
        Assert.Equal(M(card), p.CardAmount);
        Assert.Equal(M(card), p.AmountDue);
        Assert.False(p.UsesErpRoundedTotal);
        Assert.True(p.IsComplete);
    }

    [Fact]
    public void With_rounding_disabled_the_card_is_the_exact_grand_total() =>
        Assert.Equal(M("11.429"), new PaymentCalculator(new MoneySettings(3, RoundingMethod.Bankers, 0.25m, DisableRoundedTotal: true))
            .Plan(M("11.429"), Tender.Card()).CardAmount);

    [Fact]
    public void Split_rounds_the_whole_bill_and_charges_the_card_exactly()
    {
        var p = calc.Plan(M("14.37"), Tender.Split(10m, 5m));
        Assert.True(p.UsesErpRoundedTotal);
        Assert.Equal(10m, p.CardAmount);
        Assert.Equal(M("4.25"), p.CashDue);
        Assert.Equal(M("14.25"), p.AmountDue);
        Assert.Equal(M("0.75"), p.Change);
        Assert.Equal(M("-0.12"), p.RoundingDifference);
        Assert.True(p.IsComplete);
    }

    [Fact]
    public void Split_pays_erpnexts_rounded_total()
    {
        var p = calc.Plan(M("6.145"), Tender.Split(M("6.00"), M("1.00")));
        Assert.Equal(M("6.25"), p.AmountDue);
        Assert.Equal(M("0.25"), p.CashDue);
        Assert.Equal(M("0.75"), p.Change);
        Assert.Equal(M("0.105"), p.RoundingDifference);
        Assert.Equal(p.AmountDue, p.CardAmount + p.CashTendered - p.Change);
    }

    // Production POS Awesome bill ACC-PSINV-2026-06508 took card 6.15 + cash 0.10: change in fils the drawer cannot give.
    [Theory]
    [InlineData("6.145", "6.15", "6.00 or 6.25")]   // due 6.25: cash 0.10 → card 6.00 (cash 0.25); 6.25 is the whole due, so not offered
    [InlineData("20.13", "10.13", "10.00 or 10.25")]
    public void Split_cash_part_must_be_in_quarter_steps(string grand, string card, string suggestion)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => calc.Plan(M(grand), Tender.Split(M(card), 20m)));
        Assert.Contains("steps of 0.25", ex.Message);
        Assert.Contains(suggestion.Split(" or ")[0], ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("14.25")]   // the amount due (14.37 rounds to 14.25): use Card instead
    [InlineData("14.30")]   // under the bill total but over the amount due
    [InlineData("20")]
    public void Split_card_part_must_be_between_zero_and_the_amount_due(string card) =>
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

    // Rounding disabled (MoneySettings.DisableRoundedTotal; bills taken before the till ignored the profile's flag): ERPNext keeps
    // the exact grand total, so cash is not rounded to the quarter either.
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
