using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Tax;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class TaxCalculatorTests
{
    private const string VatAccount = "VAT 5% - S";
    private static readonly SalesTaxTemplate Inclusive = new("UAE VAT 5%", [new TaxRow(1, VatAccount, "VAT 5%", 5m, true)], null);
    private static readonly SalesTaxTemplate Exclusive = new("UAE VAT 5% Excl", [new TaxRow(1, VatAccount, "VAT 5%", 5m, false)], null);
    private static readonly ItemTaxTemplate ZeroRated = new("Zero Rated", new Dictionary<string, decimal> { [VatAccount] = 0m });

    private static BillTotals Calc(SalesTaxTemplate? template, MoneySettings? money, params (string qty, string rate, string? itt)[] lines) =>
        new TaxCalculator(money ?? new MoneySettings(), name => name == ZeroRated.Name ? ZeroRated : null)
            .Calculate(lines.Select(l => new TaxLineInput(M(l.qty), M(l.rate), l.itt)).ToList(), template);

    [Fact]
    public void Inclusive_vat_is_back_calculated_and_total_is_unchanged()
    {
        var t = Calc(Inclusive, null, ("1", "105", null));
        Assert.Equal(M("105.00"), t.GrandTotal);
        Assert.Equal(M("100.00"), t.NetTotal);
        Assert.Equal(M("5.00"), t.TotalTaxes);
        Assert.Equal(M("100.00"), t.Lines[0].NetAmount);
        Assert.Equal(M("5.00"), t.Taxes[0].TaxAmount);
    }

    [Fact]
    public void Mockup_basket_matches_hand_calculation()
    {
        var t = Calc(Inclusive, null,
            ("2", "290", null), ("1", "1935", null), ("1", "180", null), ("1", "420", null),
            ("2", "579.50", null), ("1", "395", null), ("1", "289", null), ("3", "160", null));
        Assert.Equal(M("5438.00"), t.Total);
        Assert.Equal(M("5179.05"), t.NetTotal);
        Assert.Equal(M("258.95"), t.TotalTaxes);
        Assert.Equal(M("5438.00"), t.GrandTotal);
        Assert.Equal(M("5438.00"), t.AmountDue);
    }

    [Fact]
    public void Inclusive_rounding_difference_is_absorbed_so_grand_total_equals_shelf_total()
    {
        // Each 0.10 line nets to 0.10 after rounding, VAT 0.015 rounds to 0.02, row total 0.32;
        // ERPNext's grand_total_diff (-0.02) brings the grand total back to 0.30.
        var t = Calc(Inclusive, null, ("1", "0.10", null), ("1", "0.10", null), ("1", "0.10", null));
        Assert.Equal(M("0.30"), t.NetTotal);
        Assert.Equal(M("0.02"), t.TotalTaxes);
        Assert.Equal(M("0.30"), t.GrandTotal);
    }

    [Fact]
    public void Exclusive_vat_is_added_on_top()
    {
        var t = Calc(Exclusive, null, ("1", "100", null));
        Assert.Equal(M("100.00"), t.NetTotal);
        Assert.Equal(M("5.00"), t.TotalTaxes);
        Assert.Equal(M("105.00"), t.GrandTotal);
    }

    [Fact]
    public void Item_tax_template_overrides_rate_for_its_line_only()
    {
        var t = Calc(Inclusive, null, ("1", "105", null), ("1", "50", ZeroRated.Name));
        Assert.Equal(M("150.00"), t.NetTotal);
        Assert.Equal(M("5.00"), t.TotalTaxes);
        Assert.Equal(M("155.00"), t.GrandTotal);
        Assert.Equal(M("50.00"), t.Lines[1].NetAmount);
    }

    [Fact]
    public void Rounded_total_to_whole_units_when_no_fraction_is_set()
    {
        var t = Calc(Exclusive, null, ("1", "99.40", null));
        Assert.Equal(M("104.37"), t.GrandTotal);
        Assert.Equal(M("104.00"), t.RoundedTotal);
        Assert.Equal(M("-0.37"), t.RoundingAdjustment);
        Assert.Equal(M("104.00"), t.AmountDue);
    }

    [Fact]
    public void Rounded_total_to_quarter_fraction()
    {
        var t = Calc(Exclusive, new MoneySettings(SmallestCurrencyFraction: M("0.25")), ("1", "99.40", null));
        Assert.Equal(M("104.25"), t.RoundedTotal);
        Assert.Equal(M("-0.12"), t.RoundingAdjustment);
    }

    [Fact]
    public void Disabled_rounded_total_means_amount_due_is_grand_total()
    {
        var t = Calc(Exclusive, new MoneySettings(DisableRoundedTotal: true), ("1", "99.40", null));
        Assert.Equal(0m, t.RoundedTotal);
        Assert.Equal(M("104.37"), t.AmountDue);
    }

    [Fact]
    public void No_template_means_no_tax()
    {
        var t = Calc(null, null, ("2", "10.50", null));
        Assert.Equal(M("21.00"), t.GrandTotal);
        Assert.Equal(0m, t.TotalTaxes);
        Assert.Empty(t.Taxes);
    }

    [Fact]
    public void Empty_bill_is_zero()
    {
        var t = Calc(Inclusive, null);
        Assert.Equal(0m, t.GrandTotal);
        Assert.Equal(0m, t.AmountDue);
    }

    [Fact]
    public void Unsupported_template_throws()
    {
        var bad = new SalesTaxTemplate("Bad", [], "tax charge type 'Actual' is not supported");
        var ex = Assert.Throws<UnsupportedTaxSetupException>(() => Calc(bad, null, ("1", "1", null)));
        Assert.Contains("Actual", ex.Message);
    }

    [Fact]
    public void Unknown_item_tax_template_throws_instead_of_guessing()
    {
        var ex = Assert.Throws<UnsupportedTaxSetupException>(() => Calc(Inclusive, null, ("1", "10", "Missing T")));
        Assert.Contains("Missing T", ex.Message);
    }
}
