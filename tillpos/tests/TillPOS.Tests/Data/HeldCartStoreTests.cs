using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Sales;
using TillPOS.Data;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Data;

public sealed class HeldCartStoreTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 6, 16, 0, 0, TimeSpan.FromHours(4));
    private readonly TempDb temp = new();
    private readonly HeldCartStore store;
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();

    public HeldCartStoreTests()
    {
        store = new HeldCartStore(temp.Db);
        catalog.Items.Add(new Item("MILK", "Milk", "Dairy", null, "PCS", false, true));
        catalog.Prices.Add(new ItemPrice("P-MILK", "MILK", "PCS", M("6.79"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("111", "MILK", null));
        catalog.Items.Add(new Item("000089", "CUCUMBER/KIYAR", "Food", null, "Kg", false, true));
        catalog.Prices.Add(new ItemPrice("P-CUC", "000089", "Kg", M("3.50"), null, null));
        catalog.Barcodes.Add(new ItemBarcode("000089", "000089", "Kg"));
    }

    public void Dispose() => temp.Dispose();

    private Cart NewCart() => new(new SaleContext(catalog, new MoneySettings(3, RoundingMethod.Bankers, 0.25m),
        "Standard Selling", "Stores - AAML", null, null, () => new DateOnly(2026, 10, 6)));

    [Fact]
    public void Hold_parks_the_bill_and_recall_restores_quantities_and_weights()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        cart.AddBarcode("111");
        cart.AddBarcode("2000089007400");

        var held = store.Hold(cart, "Customer in blue", At);

        Assert.Empty(cart.Lines);
        Assert.Equal("Customer in blue", Assert.Single(store.List()).Label);

        var taken = store.Take(held.Id)!;
        var fresh = NewCart();
        Assert.Empty(fresh.Restore(taken.Lines));
        Assert.Equal(2m, fresh.Lines[0].Qty);
        Assert.Equal(M("0.740"), fresh.Lines[1].Qty);
        Assert.True(fresh.Lines[1].FromScaleLabel);
        Assert.Empty(store.List());
        Assert.Null(store.Take(held.Id));
    }

    [Fact]
    public void Items_that_can_no_longer_be_sold_are_reported_on_recall()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        var held = store.Hold(cart, "x", At);
        catalog.Prices.Clear();

        var failed = NewCart().Restore(store.Take(held.Id)!.Lines);

        Assert.Equal(AddOutcome.NoPrice, Assert.Single(failed).Outcome);
    }

    [Fact]
    public void Restore_needs_an_empty_cart()
    {
        var cart = NewCart();
        cart.AddBarcode("111");
        Assert.Throws<InvalidOperationException>(() => cart.Restore([new HeldLine("MILK", "PCS", 1m, null, false)]));
    }

    [Fact]
    public void An_empty_bill_cannot_be_held() =>
        Assert.Throws<InvalidOperationException>(() => store.Hold(NewCart(), "x", At));
}
