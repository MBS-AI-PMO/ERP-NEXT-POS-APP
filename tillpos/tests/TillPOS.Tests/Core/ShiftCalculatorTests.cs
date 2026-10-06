using TillPOS.Core.Money;
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class ShiftCalculatorTests
{
    private static readonly TenderModes Modes = new("Cash Counter 2", "Credit Card");
    private static readonly DateTimeOffset At = new(2026, 10, 6, 22, 0, 0, TimeSpan.FromHours(4));
    private static readonly MoneySettings Money = new(3, RoundingMethod.Bankers, 0.25m);

    private static Receipt R(ReceiptKind kind, decimal grand, decimal net, decimal change, params ReceiptPayment[] payments) => new(
        Guid.NewGuid().ToString(), kind, null, "S1", "c", At, [], grand, net, grand - net, grand, false, 0m, 0m, payments, change, 0m, null);

    [Fact]
    public void Expected_cash_is_opening_plus_cash_taken_minus_change_and_refunds()
    {
        var opening = new ShiftOpening("S1", "c", At.AddHours(-12), [new ReceiptPayment("Cash Counter 2", 200m)]);
        var receipts = new[]
        {
            R(ReceiptKind.Sale, M("14.37"), M("13.686"), M("5.75"), new ReceiptPayment("Cash Counter 2", 20m)),
            R(ReceiptKind.Sale, M("30.00"), M("28.571"), 0m, new ReceiptPayment("Credit Card", 30m)),
            R(ReceiptKind.Return, M("-10.13"), M("-9.648"), 0m, new ReceiptPayment("Cash Counter 2", M("-10.25"))),
        };

        var closing = ShiftCalculator.Close(opening, receipts,
            new Dictionary<string, decimal> { ["Cash Counter 2"] = M("203.50"), ["Credit Card"] = 30m }, Modes, At, Money);

        var cash = closing.Modes.Single(m => m.ModeOfPayment == "Cash Counter 2");
        Assert.Equal(200m, cash.Opening);
        Assert.Equal(M("204.000"), cash.Expected);                 // 200 + 20 − 5.75 − 10.25
        Assert.Equal(M("-0.500"), cash.Difference);
        var card = closing.Modes.Single(m => m.ModeOfPayment == "Credit Card");
        Assert.Equal(30m, card.Expected);
        Assert.Equal(0m, card.Difference);
        Assert.Equal(2, closing.Sales);
        Assert.Equal(1, closing.Returns);
        Assert.Equal(M("34.240"), closing.GrandTotal);
    }

    [Fact]
    public void Uncounted_modes_count_as_zero()
    {
        var opening = new ShiftOpening("S1", "c", At, []);
        var closing = ShiftCalculator.Close(opening, [R(ReceiptKind.Sale, 10m, M("9.524"), 0m, new ReceiptPayment("Credit Card", 10m))],
            new Dictionary<string, decimal>(), Modes, At, Money);

        Assert.Equal(-10m, closing.Modes.Single(m => m.ModeOfPayment == "Credit Card").Difference);
        Assert.Contains(closing.Modes, m => m.ModeOfPayment == "Cash Counter 2" && m.Expected == 0m);
    }

    [Fact]
    public void Receipts_from_another_shift_are_ignored_and_counted_amounts_are_rounded()
    {
        var opening = new ShiftOpening("S1", "c", At, [new ReceiptPayment("Cash Counter 2", 100m)]);
        var other = R(ReceiptKind.Sale, 50m, M("47.619"), 0m, new ReceiptPayment("Cash Counter 2", 50m)) with { ShiftClientId = "OTHER" };

        var closing = ShiftCalculator.Close(opening, [other], new Dictionary<string, decimal> { ["Cash Counter 2"] = M("100.0004") },
            Modes, At, Money);

        var cash = closing.Modes.Single(m => m.ModeOfPayment == "Cash Counter 2");
        Assert.Equal(100m, cash.Expected);
        Assert.Equal(M("100.000"), cash.Counted);
        Assert.Equal(0m, cash.Difference);
        Assert.Equal(0, closing.Sales);
        Assert.Equal(0m, closing.GrandTotal);
    }
}
