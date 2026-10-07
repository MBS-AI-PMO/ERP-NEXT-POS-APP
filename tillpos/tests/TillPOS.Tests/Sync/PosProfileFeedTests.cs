using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Sync;
using TillPOS.Sync.Feeds;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Sync;

public sealed class PosProfileFeedTests : IDisposable
{
    private readonly TempDb temp = new();
    private readonly FakeErp erp = new();
    private readonly CatalogStore store;

    public PosProfileFeedTests()
    {
        store = new CatalogStore(temp.Db);
        erp.Docs[("Company", "Shop LLC")] = new Dictionary<string, object?>
            { ["name"] = "Shop LLC", ["company_name"] = "Shop LLC", ["default_currency"] = "AED", ["tax_id"] = "100000000000003" };
        erp.Docs[("Currency", "AED")] = new Dictionary<string, object?> { ["name"] = "AED", ["smallest_currency_fraction_value"] = 0.25 };
        Profile("Test Counter", "Cash Counter 2", "Stores - S", disableRounded: true);
        Profile("Al Ain Counter 1", "Cash Counter 1", "Stores 1 - S", disableRounded: false);
    }

    public void Dispose() => temp.Dispose();

    private void Profile(string name, string cash, string warehouse, bool disableRounded) =>
        erp.Docs[("POS Profile", name)] = new Dictionary<string, object?>
        {
            ["name"] = name, ["company"] = "Shop LLC", ["warehouse"] = warehouse, ["selling_price_list"] = "Standard Selling",
            ["customer"] = "Walk-in Customer", ["disable_rounded_total"] = disableRounded ? 1 : 0,
            ["payments"] = new[] { new Dictionary<string, object?> { ["mode_of_payment"] = cash, ["default"] = 1 } },
        };

    private SyncContext Ctx(string profile, params string[] profiles) =>
        new(erp, store, new KeysetPager(erp, new InMemorySyncState()), profile) { Profiles = profiles };

    [Fact]
    public async Task One_profile_is_stored_under_the_default_key_and_its_own()
    {
        Assert.Equal(1, await new PosProfileFeed(Ctx("Test Counter")).RunAsync(default));

        Assert.Equal("Test Counter", store.LoadPosSettings()!.PosProfile);
        Assert.Equal("Test Counter", store.LoadPosSettings("Test Counter")!.PosProfile);
    }

    [Fact]
    public async Task Every_counter_gets_its_own_settings()
    {
        Assert.Equal(2, await new PosProfileFeed(Ctx("Test Counter", "Test Counter", "Al Ain Counter 1")).RunAsync(default));

        Assert.Equal("Test Counter", store.LoadPosSettings()!.PosProfile);
        Assert.True(store.LoadPosSettings("Test Counter")!.DisableRoundedTotal);
        var one = store.LoadPosSettings("Al Ain Counter 1")!;
        Assert.False(one.DisableRoundedTotal);
        Assert.Equal("Stores 1 - S", one.Warehouse);
        Assert.Equal("Cash Counter 1", one.PaymentModes[0].ModeOfPayment);
    }

    [Fact]
    public async Task A_counter_that_cannot_be_read_does_not_fail_the_sync()
    {
        Assert.Equal(1, await new PosProfileFeed(Ctx("Test Counter", "Test Counter", "Al Ain Counter 9")).RunAsync(default));

        Assert.NotNull(store.LoadPosSettings("Test Counter"));
        Assert.Null(store.LoadPosSettings("Al Ain Counter 9"));
    }

    [Fact]
    public async Task A_counter_that_cannot_be_read_keeps_its_earlier_settings()
    {
        await new PosProfileFeed(Ctx("Test Counter", "Al Ain Counter 1")).RunAsync(default);
        erp.Docs.Remove(("POS Profile", "Al Ain Counter 1"));

        await new PosProfileFeed(Ctx("Test Counter", "Al Ain Counter 1")).RunAsync(default);

        Assert.Equal("Stores 1 - S", store.LoadPosSettings("Al Ain Counter 1")!.Warehouse);
    }

    [Fact]
    public async Task The_default_counter_that_cannot_be_read_fails_the_feed()
    {
        await Assert.ThrowsAsync<ErpException>(() => new PosProfileFeed(Ctx("Al Ain Counter 9", "Test Counter")).RunAsync(default));
        Assert.Null(store.LoadPosSettings());
    }
}
