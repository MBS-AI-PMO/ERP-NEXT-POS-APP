using TillPOS.Core.Money;
using TillPOS.Core.Sales;

namespace TillPOS.Core.Shifts;

public sealed record ShiftOpening(string ClientId, string Cashier, DateTimeOffset OpenedAt, IReadOnlyList<ReceiptPayment> OpeningAmounts);

public sealed record ShiftModeSummary(string ModeOfPayment, decimal Opening, decimal Expected, decimal Counted, decimal Difference);

public sealed record ShiftClosing(
    string ShiftClientId,
    DateTimeOffset ClosedAt,
    IReadOnlyList<ShiftModeSummary> Modes,
    int Sales,
    int Returns,
    decimal GrandTotal,
    decimal NetTotal,
    decimal TotalTaxes);

/// <summary>Blind count: the cashier enters counted amounts first; expected amounts and differences are worked out here
/// (expected = opening + payments taken − change given; refunds are negative payments).</summary>
public static class ShiftCalculator
{
    public static ShiftClosing Close(ShiftOpening opening, IReadOnlyList<Receipt> receipts, IReadOnlyDictionary<string, decimal> counted,
        TenderModes modes, DateTimeOffset closedAt, MoneySettings money)
    {
        receipts = receipts.Where(r => r.ShiftClientId == opening.ClientId).ToList();
        var names = new List<string> { modes.Cash, modes.Card };
        foreach (var name in opening.OpeningAmounts.Select(p => p.ModeOfPayment)
                     .Concat(receipts.SelectMany(r => r.Payments).Select(p => p.ModeOfPayment))
                     .Concat(counted.Keys))
            if (!names.Contains(name)) names.Add(name);

        var summaries = names.Select(name =>
        {
            var start = opening.OpeningAmounts.Where(p => p.ModeOfPayment == name).Sum(p => p.Amount);
            var taken = receipts.SelectMany(r => r.Payments).Where(p => p.ModeOfPayment == name).Sum(p => p.Amount);
            var change = name == modes.Cash ? receipts.Sum(r => r.Change) : 0m;
            var expected = Rounder.Round(start + taken - change, money);
            var count = counted.TryGetValue(name, out var c) ? Rounder.Round(c, money) : 0m;
            return new ShiftModeSummary(name, start, expected, count, Rounder.Round(count - expected, money));
        }).ToList();

        return new ShiftClosing(opening.ClientId, closedAt, summaries,
            receipts.Count(r => r.Kind == ReceiptKind.Sale), receipts.Count(r => r.Kind == ReceiptKind.Return),
            Rounder.Round(receipts.Sum(r => r.GrandTotal), money), Rounder.Round(receipts.Sum(r => r.NetTotal), money),
            Rounder.Round(receipts.Sum(r => r.TotalTaxes), money));
    }
}
