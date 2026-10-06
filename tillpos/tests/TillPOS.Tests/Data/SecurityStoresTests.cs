using TillPOS.Core.Security;
using TillPOS.Data;

namespace TillPOS.Tests.Data;

public sealed class SecurityStoresTests : IDisposable
{
    private readonly TempDb temp = new();

    public void Dispose() => temp.Dispose();

    [Fact]
    public void Cashier_list_is_replaced_as_a_whole()
    {
        var store = new CashierStore(temp.Db);
        store.ReplaceAll([new Cashier("a", "Ana", "a@x", "h1", false, true), new Cashier("b", "Ben", null, "h2", true, true)]);
        store.ReplaceAll([new Cashier("b", "Ben", null, "h2", true, false)]);

        var b = Assert.Single(store.All());
        Assert.Equal("b", b.Id);
        Assert.False(b.Enabled);
    }

    [Fact]
    public void Approvals_are_logged_until_uploaded()
    {
        var store = new ApprovalStore(temp.Db);
        var at = new DateTimeOffset(2026, 10, 6, 16, 0, 0, TimeSpan.FromHours(4));
        store.Add(new ApprovalRecord("1", ApprovalAction.LineVoid, "sup", "TILL2-1", "MILK", 6.79m, null, at));
        store.Add(new ApprovalRecord("2", ApprovalAction.NoSaleDrawerOpen, "sup", null, null, 0m, "change for customer", at.AddMinutes(1)));

        Assert.Equal(new[] { "1", "2" }, store.Unsynced().Select(a => a.Id));
        Assert.Equal(ApprovalAction.LineVoid, store.Unsynced()[0].Action);

        store.MarkSynced(["1"]);
        Assert.Equal("2", Assert.Single(store.Unsynced()).Id);
    }
}
