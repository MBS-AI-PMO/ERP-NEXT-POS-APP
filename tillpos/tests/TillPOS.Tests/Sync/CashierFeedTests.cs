using TillPOS.Core.Security;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Sync;
using TillPOS.Sync.Feeds;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Sync;

public sealed class CashierFeedTests : IDisposable
{
    private readonly TempDb temp = new();
    private readonly FakeErp erp = new();
    private readonly CashierStore cashiers;
    private readonly CashierFeed feed;

    public CashierFeedTests()
    {
        cashiers = new CashierStore(temp.Db);
        var store = new CatalogStore(temp.Db);
        feed = new CashierFeed(new SyncContext(erp, store, new KeysetPager(erp, new InMemorySyncState()), "Test Counter"), cashiers);
    }

    public void Dispose() => temp.Dispose();

    private void Row(string id, string? pin, bool supervisor = false, bool enabled = true) => erp.AddRow("POS Cashier", new()
    {
        ["name"] = id, ["cashier_name"] = id.ToUpperInvariant(), ["user"] = $"{id}@quickgroc.com", ["pin"] = pin,
        ["is_supervisor"] = supervisor ? 1 : 0, ["enabled"] = enabled ? 1 : 0,
    });

    [Fact]
    public async Task Downloads_cashiers_with_hashed_pins()
    {
        Row("simran", "1234");
        Row("sup", "987654", supervisor: true);

        Assert.Equal(2, await feed.RunAsync(default));

        var all = cashiers.All();
        Assert.All(all, c => Assert.DoesNotContain("1234", c.PinHash));
        var auth = new Authenticator(cashiers.All);
        Assert.Equal("simran", auth.Login("1234")!.Id);
        Assert.Equal("simran@quickgroc.com", auth.Login("1234")!.User);
        Assert.Equal("sup", auth.Supervisor("987654")!.Id);
    }

    [Fact]
    public async Task Cashiers_with_missing_or_invalid_pins_are_skipped()
    {
        Row("ok", "1234");
        Row("nopin", null);
        Row("short", "12");

        await feed.RunAsync(default);

        Assert.Equal("ok", Assert.Single(cashiers.All()).Id);
    }

    [Fact]
    public async Task Unchanged_pin_keeps_its_stored_hash()
    {
        Row("simran", "1234");
        await feed.RunAsync(default);
        var first = cashiers.All()[0].PinHash;

        await feed.RunAsync(default);

        Assert.Equal(first, cashiers.All()[0].PinHash);
    }

    [Fact]
    public async Task Missing_doctype_gives_a_clear_message_and_keeps_the_local_list()
    {
        cashiers.ReplaceAll([new Cashier("kept", "Kept", null, PinHasher.Hash("1234"), false, true)]);
        erp.Fail = q => q.Doctype == "POS Cashier" ? new ErpException(404, "DocType POS Cashier not found", "DoesNotExistError") : null;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => feed.RunAsync(default));

        Assert.Contains("POS Cashier", ex.Message);
        Assert.Equal("kept", Assert.Single(cashiers.All()).Id);
    }
}
