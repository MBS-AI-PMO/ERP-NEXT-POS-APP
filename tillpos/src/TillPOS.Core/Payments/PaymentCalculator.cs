using TillPOS.Core.Money;

namespace TillPOS.Core.Payments;

public enum TenderKind { Cash, Card, Split }

public sealed record Tender(TenderKind Kind, decimal CardAmount, decimal CashTendered)
{
    public static Tender Cash(decimal tendered) => new(TenderKind.Cash, 0m, tendered);
    public static Tender Card() => new(TenderKind.Card, 0m, 0m);
    public static Tender Split(decimal cardAmount, decimal cashTendered) => new(TenderKind.Split, cardAmount, cashTendered);
}

/// <summary>How a bill is paid. GrandTotal is ERPNext's exact total; AmountDue is what the customer pays.
/// UsesErpRoundedTotal = cash-only bill (ERPNext rounded total applies); card and split bills are not rounded as a whole.
/// RoundingDifference = cash due − the exact cash portion (negative when rounding went down).</summary>
public sealed record PaymentPlan(
    TenderKind Kind,
    decimal GrandTotal,
    bool UsesErpRoundedTotal,
    decimal AmountDue,
    decimal CardAmount,
    decimal CashDue,
    decimal CashTendered,
    decimal Change,
    decimal Shortfall,
    decimal RoundingDifference)
{
    public bool IsComplete => Shortfall == 0m;
}

/// <summary>Spec §0 decision 4: card exact; cash rounded to the currency's smallest fraction with ERPNext's rule;
/// split = card exact + cash remainder rounded.</summary>
public sealed class PaymentCalculator(MoneySettings money)
{
    public PaymentPlan Plan(decimal grandTotal, Tender tender) => tender.Kind switch
    {
        TenderKind.Cash => WithCash(grandTotal, TenderKind.Cash, 0m, grandTotal, tender.CashTendered),
        TenderKind.Card => CardOnly(grandTotal),
        TenderKind.Split => Split(grandTotal, tender),
        _ => throw new ArgumentOutOfRangeException(nameof(tender)),
    };

    /// <summary>Refunds: cash rounded like a sale, card exact; the refund is paid in full (nothing tendered).</summary>
    public PaymentPlan PlanRefund(decimal grandTotal, TenderKind kind)
    {
        if (grandTotal >= 0m) throw new ArgumentOutOfRangeException(nameof(grandTotal), "A refund total is negative.");
        return kind switch
        {
            TenderKind.Cash => WithCash(grandTotal, TenderKind.Cash, 0m, grandTotal, Rounder.RoundToSmallestFraction(grandTotal, money)),
            TenderKind.Card => CardOnly(grandTotal),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), "Refunds are paid in cash or to the card."),
        };
    }

    private static PaymentPlan CardOnly(decimal grandTotal) =>
        new(TenderKind.Card, grandTotal, false, grandTotal, grandTotal, 0m, 0m, 0m, 0m, 0m);

    private PaymentPlan Split(decimal grandTotal, Tender tender)
    {
        if (tender.CardAmount <= 0m || tender.CardAmount >= grandTotal)
            throw new ArgumentOutOfRangeException(nameof(tender), "The card part must be more than zero and less than the bill total.");
        var remainder = grandTotal - tender.CardAmount;
        if (Rounder.RoundToSmallestFraction(remainder, money) <= 0m)
            throw new ArgumentOutOfRangeException(nameof(tender), "The cash part rounds to zero; take the whole amount by card.");
        return WithCash(grandTotal, TenderKind.Split, tender.CardAmount, remainder, tender.CashTendered);
    }

    private PaymentPlan WithCash(decimal grandTotal, TenderKind kind, decimal card, decimal exactCash, decimal tendered)
    {
        var cashDue = Rounder.RoundToSmallestFraction(exactCash, money);
        var shortfall = Math.Max(0m, cashDue - tendered);
        var change = Math.Max(0m, tendered - cashDue);
        return new PaymentPlan(kind, grandTotal, kind == TenderKind.Cash, card + cashDue, card, cashDue, tendered, change,
            shortfall, Rounder.Round(cashDue - exactCash, money));
    }
}
