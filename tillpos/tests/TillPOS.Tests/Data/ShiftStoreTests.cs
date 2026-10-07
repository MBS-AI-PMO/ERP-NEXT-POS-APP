using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using TillPOS.Data;

namespace TillPOS.Tests.Data;

public sealed class ShiftStoreTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 6, 8, 0, 0, TimeSpan.FromHours(4));
    private readonly TempDb temp = new();
    private readonly ShiftStore store;

    public ShiftStoreTests() => store = new ShiftStore(temp.Db);

    public void Dispose() => temp.Dispose();

    [Fact]
    public void Open_then_close_a_shift()
    {
        var opening = new ShiftOpening("TILL2-SHIFT-20261006080000", "c", "", At, [new ReceiptPayment("Cash Counter 2", 200m)]);
        store.Open(opening);

        Assert.Equal(opening.ClientId, store.Current()!.ClientId);
        Assert.Equal(200m, store.Current()!.OpeningAmounts[0].Amount);

        var closing = new ShiftClosing(opening.ClientId, At.AddHours(12), [new ShiftModeSummary("Cash Counter 2", 200m, 210m, 210m, 0m)],
            5, 0, 10m, 9.524m, 0.476m);
        store.Close(closing);

        Assert.Null(store.Current());
        var (o, c) = store.Get(opening.ClientId)!.Value;
        Assert.Equal(opening.ClientId, o.ClientId);
        Assert.Equal(210m, c!.Modes[0].Counted);
    }

    [Fact]
    public void Only_one_shift_can_be_open()
    {
        store.Open(new ShiftOpening("A", "c", "", At, []));
        Assert.Throws<InvalidOperationException>(() => store.Open(new ShiftOpening("B", "c", "", At, [])));
    }

    [Fact]
    public void Closing_an_unknown_or_closed_shift_fails()
    {
        Assert.Throws<InvalidOperationException>(() =>
            store.Close(new ShiftClosing("X", At, [], 0, 0, 0m, 0m, 0m)));
    }

    [Fact]
    public void A_shift_keeps_its_counter()
    {
        store.Open(new ShiftOpening("A", "c", "Al Ain Counter 2", At, [new ReceiptPayment("Cash Counter 2", 100m)]) { CounterName = "Counter 2" });
        var current = store.Current()!;
        Assert.Equal("Al Ain Counter 2", current.Counter);
        Assert.Equal("Counter 2", current.CounterName);
    }

    [Fact]
    public void A_shift_saved_before_counters_existed_has_a_blank_counter()
    {
        using (var c = temp.Db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO shift (client_id, opened_at, opening_json) VALUES ('OLD', '2026-10-06T04:00:00.0000000Z', @j)";
            cmd.Parameters.AddWithValue("@j",
                """{"ClientId":"OLD","Cashier":"c","OpenedAt":"2026-10-06T08:00:00+04:00","OpeningAmounts":[{"ModeOfPayment":"Cash Counter 2","Amount":200}]}""");
            cmd.ExecuteNonQuery();
        }
        var current = store.Current()!;
        Assert.Equal("OLD", current.ClientId);
        Assert.Equal("", current.Counter);
    }
}
