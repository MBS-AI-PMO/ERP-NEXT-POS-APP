using System.Text.Json;
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;

namespace TillPOS.Tests.Core;

public class CounterSettingsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 7, 8, 0, 0, TimeSpan.FromHours(4));
    private static readonly CounterSettings One = new("Al Ain Counter 1", "Counter 1", "Cash Counter 1", "Credit Card");
    private static readonly CounterSettings Two = new("Al Ain Counter 2", "Counter 2", "Cash Counter 2", "Credit Card");
    private static readonly IReadOnlyList<CounterSettings> Both = [One, Two];

    [Fact]
    public void Label_falls_back_to_the_profile_name()
    {
        Assert.Equal("Counter 1", One.DisplayName);
        Assert.Equal("Test Counter", new CounterSettings("Test Counter", " ", "Cash", "Card").DisplayName);
    }

    [Fact]
    public void Modes_are_the_counters_cash_and_card_modes() =>
        Assert.Equal(new TenderModes("Cash Counter 2", "Credit Card"), Two.Modes);

    [Fact]
    public void Find_matches_the_profile_ignoring_case()
    {
        Assert.Same(Two, CounterSettings.Find(Both, "al ain counter 2"));
        Assert.Null(CounterSettings.Find(Both, "Al Ain Counter 9"));
    }

    [Fact]
    public void A_shift_without_a_counter_belongs_to_the_default_counter() =>
        Assert.Equal(One, CounterSettings.ForShift(Both, new ShiftOpening("S", "c", "", At, [])));

    [Fact]
    public void A_shift_belongs_to_its_counter() =>
        Assert.Equal(Two, CounterSettings.ForShift(Both, new ShiftOpening("S", "c", "Al Ain Counter 2", At, [])));

    [Fact]
    public void The_shifts_saved_label_and_modes_win_over_the_settings()
    {
        var shift = new ShiftOpening("S", "c", "Al Ain Counter 2", At, [new ReceiptPayment("Cash Counter 2", 100m)])
            { CounterName = "Old name", CashMode = "Old cash", CardMode = "Old card" };
        Assert.Equal(new CounterSettings("Al Ain Counter 2", "Old name", "Old cash", "Old card"), CounterSettings.ForShift(Both, shift));
    }

    [Fact]
    public void Without_saved_modes_the_float_names_the_cash_mode()
    {
        var shift = new ShiftOpening("S", "c", "Al Ain Counter 2", At, [new ReceiptPayment("Cash Counter 2 Old", 100m)]);
        Assert.Equal(new CounterSettings("Al Ain Counter 2", "Counter 2", "Cash Counter 2 Old", "Credit Card"), CounterSettings.ForShift(Both, shift));
    }

    [Fact]
    public void Shift_json_without_saved_modes_reads_them_as_blank()
    {
        var json = """{"ClientId":"S1","Cashier":"c","Counter":"X","CounterName":null,"CashMode":null,"OpenedAt":"2026-10-07T08:00:00+04:00","OpeningAmounts":[]}""";
        var shift = JsonSerializer.Deserialize<ShiftOpening>(json)!;
        Assert.Equal(("", ""), (shift.CashMode, shift.CardMode));
        var back = JsonSerializer.Deserialize<ShiftOpening>(JsonSerializer.Serialize(shift with { CashMode = "C", CardMode = "D" }))!;
        Assert.Equal(("C", "D"), (back.CashMode, back.CardMode));
    }

    [Fact]
    public void A_shift_at_a_counter_no_longer_configured_keeps_its_profile_and_opening_cash_mode()
    {
        var shift = new ShiftOpening("S", "c", "Al Ain Counter 3", At, [new ReceiptPayment("Cash Counter 3", 100m)]) { CounterName = "Counter 3" };
        var counter = CounterSettings.ForShift(Both, shift);
        Assert.Equal(new CounterSettings("Al Ain Counter 3", "Counter 3", "Cash Counter 3", "Credit Card"), counter);
    }

    [Fact]
    public void Shift_json_without_a_counter_reads_as_blank()
    {
        var json = """{"ClientId":"S1","Cashier":"c","OpenedAt":"2026-10-07T08:00:00+04:00","OpeningAmounts":[{"ModeOfPayment":"Cash Counter 2","Amount":200}]}""";
        var shift = JsonSerializer.Deserialize<ShiftOpening>(json)!;
        Assert.Equal("", shift.Counter);
        Assert.Null(shift.CounterName);
        Assert.Equal(200m, shift.OpeningAmounts[0].Amount);
    }

    [Fact]
    public void Shift_json_keeps_the_counter()
    {
        var shift = new ShiftOpening("S1", "c", "Al Ain Counter 2", At, []) { CounterName = "Counter 2" };
        var back = JsonSerializer.Deserialize<ShiftOpening>(JsonSerializer.Serialize(shift))!;
        Assert.Equal("Al Ain Counter 2", back.Counter);
        Assert.Equal("Counter 2", back.CounterName);
    }

    [Fact]
    public void Counter_json_has_only_the_four_settings()
    {
        var json = JsonSerializer.Serialize(One);
        Assert.Equal("""{"PosProfile":"Al Ain Counter 1","Label":"Counter 1","CashMode":"Cash Counter 1","CardMode":"Credit Card"}""", json);
        Assert.Equal(One, JsonSerializer.Deserialize<CounterSettings>(json));
    }
}
