using TillPOS.Core.Catalog;
using TillPOS.Presentation;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Presentation;

/// <summary>New prices from ERPNext on the sale screen: the open bill is re-priced, and "Update prices (F10)" fetches them now.</summary>
public sealed class PriceUpdateTests : IDisposable
{
    private readonly PresentationFixture f = new();

    public PriceUpdateTests() => f.LogInWithOpenShift();

    public void Dispose() => f.Dispose();

    private SaleViewModel Sale(Func<Task<PriceRefresh>>? refresh = null) =>
        new(f.Ctx with { RefreshPrices = refresh }, f.Session, new SupervisorGate(f.Ctx, f.Session), (s, k) => "payment", () => "login");

    private void SetPrice(string name, string itemCode, string uom, string rate)
    {
        f.Catalog.Prices.RemoveAll(p => p.Name == name);
        f.Catalog.Prices.Add(new ItemPrice(name, itemCode, uom, M(rate), null, null));
    }

    [Fact]
    public void New_prices_reprice_the_open_bill_and_say_what_changed()
    {
        var sale = Sale();
        sale.Scan("2000089007400");                                    // cucumber, 0.740 Kg at 3.50
        SetPrice("P-CUC", "000089", "Kg", "2.75");

        sale.PricesChanged();

        Assert.Equal("Price updated: CUCUMBER/KIYAR 3.50 → 2.75 /Kg", sale.Message);
        Assert.False(sale.MessageIsError);
        Assert.Equal("2.75", Assert.Single(sale.Lines).Price);
        Assert.Equal("2.04", sale.Total);                              // 0.740 × 2.75 = 2.035
        Assert.Contains("000089", f.Ctx.Kv.GetValue(SaleViewModel.AutosaveKey));
    }

    [Fact]
    public void Unchanged_prices_leave_the_bill_and_the_message_alone()
    {
        var sale = Sale();
        sale.Scan("111");
        var before = sale.Message;

        sale.PricesChanged();

        Assert.Equal(before, sale.Message);
        Assert.Equal("6.79", Assert.Single(sale.Lines).Price);
    }

    [Fact]
    public async Task Update_prices_fetches_now_and_reprices_the_bill()
    {
        var sale = Sale(() =>
        {
            SetPrice("P-MILK", "MILK", "PCS", "7.25");                  // the refresh brings a new price
            return Task.FromResult(new PriceRefresh(true, 1, DateTimeOffset.Now));
        });
        sale.Scan("111");

        await sale.UpdatePricesAsync();

        Assert.Equal("Price updated: Full Cream Milk 1L 6.79 → 7.25 /PCS", sale.Message);
        Assert.Equal("7.25", Assert.Single(sale.Lines).Price);
    }

    [Theory]
    [InlineData(3, "Prices updated: 3 changed")]
    [InlineData(0, "Prices are up to date")]
    public async Task Update_prices_says_how_many_changed_when_the_bill_is_unaffected(int changed, string message)
    {
        var sale = Sale(() => Task.FromResult(new PriceRefresh(true, changed, DateTimeOffset.Now)));

        await sale.UpdatePricesAsync();

        Assert.Equal(message, sale.Message);
        Assert.False(sale.MessageIsError);
    }

    [Fact]
    public async Task Offline_update_says_which_prices_are_in_use()
    {
        var sale = Sale(() => Task.FromResult(new PriceRefresh(false, 0, new DateTimeOffset(2026, 10, 10, 10, 42, 0, TimeSpan.FromHours(4)))));

        await sale.UpdatePricesAsync();

        Assert.Equal("Offline – using prices from 10:42", sale.Message);
        Assert.True(sale.MessageIsError);
    }

    [Fact]
    public async Task A_failed_update_is_reported_and_the_bill_is_kept()
    {
        var sale = Sale(() => throw new InvalidOperationException("timeout"));
        sale.Scan("111");

        await sale.UpdatePricesAsync();

        Assert.Equal("Could not update prices (timeout) – using the last downloaded prices", sale.Message);
        Assert.Single(sale.Lines);
    }
}
