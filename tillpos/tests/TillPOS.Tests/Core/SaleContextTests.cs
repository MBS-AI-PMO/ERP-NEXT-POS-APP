using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Sales;
using TillPOS.Core.Tax;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Core;

public class SaleContextTests
{
    private static readonly SalesTaxTemplate Vat = new("UAE VAT 5%", [new TaxRow(1, "VAT 5% - S", "VAT 5%", 5m, true)], null);

    private static PosSettings Settings(string? taxes) => new("Till 1", "Shop LLC", "Shop LLC", null, null, "AED", "Stores - S", "Retail",
        "Walk-in Customer", taxes, "Retail Cat", true, 0.25m, 0m, [new PaymentMode("Cash", true)]);

    private static SaleContext Create(PosSettings s, Func<string, SalesTaxTemplate?> find) =>
        SaleContext.Create(new InMemoryCatalog(), s, find, 2, RoundingMethod.Bankers, () => new DateOnly(2026, 10, 5));

    [Fact]
    public void Missing_template_throws_naming_it()
    {
        var ex = Assert.Throws<UnsupportedTaxSetupException>(() => Create(Settings("UAE VAT 5%"), _ => null));
        Assert.Contains("UAE VAT 5%", ex.Message);
    }

    [Fact]
    public void Present_template_is_set_and_money_carries_profile_values()
    {
        var ctx = Create(Settings("UAE VAT 5%"), n => n == "UAE VAT 5%" ? Vat : null);
        Assert.Same(Vat, ctx.TaxTemplate);
        Assert.Equal(0.25m, ctx.Money.SmallestCurrencyFraction);
        // POS Awesome rounds every bill although the profiles disable the rounded total; so does the till.
        Assert.False(ctx.Money.DisableRoundedTotal);
        Assert.Equal("Retail", ctx.PriceList);
        Assert.Equal("Stores - S", ctx.Warehouse);
        Assert.Equal("Retail Cat", ctx.TaxCategory);
    }

    [Fact]
    public void Null_taxes_and_charges_means_no_template()
    {
        var ctx = Create(Settings(null), _ => throw new InvalidOperationException("should not look up"));
        Assert.Null(ctx.TaxTemplate);
    }
}
