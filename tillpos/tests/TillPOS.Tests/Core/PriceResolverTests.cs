using TillPOS.Core.Catalog;
using TillPOS.Core.Pricing;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class PriceResolverTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();
    private readonly Item rice = new("RICE5", "Basmati Rice 5kg", "Rice", null, "Nos", false, true);

    private decimal? Price(string uom = "Nos", decimal cf = 1m, DateOnly? date = null) =>
        new PriceResolver(catalog).PriceListRate(rice, uom, cf, date ?? Today);

    [Fact]
    public void Uses_price_valid_today()
    {
        catalog.Prices.Add(new ItemPrice("P1", "RICE5", "Nos", M("2150"), null, null));
        Assert.Equal(M("2150"), Price());
    }

    [Fact]
    public void Latest_valid_from_wins_once_it_starts()
    {
        catalog.Prices.Add(new ItemPrice("P1", "RICE5", "Nos", M("2150"), null, null));
        catalog.Prices.Add(new ItemPrice("P2", "RICE5", "Nos", M("2000"), new DateOnly(2026, 10, 10), null));
        Assert.Equal(M("2150"), Price());
        Assert.Equal(M("2000"), Price(date: new DateOnly(2026, 10, 12)));
    }

    [Fact]
    public void Expired_price_is_ignored()
    {
        catalog.Prices.Add(new ItemPrice("P1", "RICE5", "Nos", M("2150"), null, new DateOnly(2026, 10, 1)));
        Assert.Null(Price());
    }

    [Fact]
    public void Blank_uom_price_counts_as_stock_uom()
    {
        catalog.Prices.Add(new ItemPrice("P1", "RICE5", null, M("2150"), null, null));
        Assert.Equal(M("2150"), Price());
    }

    [Fact]
    public void Falls_back_to_stock_uom_price_times_conversion_factor()
    {
        catalog.Prices.Add(new ItemPrice("P1", "RICE5", "Nos", M("2.00"), null, null));
        Assert.Equal(M("24.00"), Price("Box", 12m));
    }

    [Fact]
    public void Uom_specific_price_beats_fallback()
    {
        catalog.Prices.Add(new ItemPrice("P1", "RICE5", "Nos", M("2.00"), null, null));
        catalog.Prices.Add(new ItemPrice("P2", "RICE5", "Box", M("20.00"), null, null));
        Assert.Equal(M("20.00"), Price("Box", 12m));
    }

    [Fact]
    public void No_price_returns_null() => Assert.Null(Price());
}
