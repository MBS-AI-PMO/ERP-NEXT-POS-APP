using System.Globalization;
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
/// UsesErpRoundedTotal = cash and split bills (ERPNext's rounded total is the amount due, unless rounding is disabled); card-only
/// bills are exact. RoundingDifference = amount due − grand total (negative when rounding went down).</summary>
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

/// <summary>Card exact; cash rounded to the currency's smallest fraction with ERPNext's rule; split = the whole bill rounded like
/// cash (ERPNext's rounded total, as POS Awesome does), the card part charged exactly as entered and the cash part the rest, so
/// what is paid always equals ERPNext's rounded total. When rounding is disabled (MoneySettings.DisableRoundedTotal), ERPNext
/// keeps the exact grand total, so cash is exact too (only rounded to the currency precision) and no bill uses a rounded
/// total.</summary>
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
            TenderKind.Cash => WithCash(grandTotal, TenderKind.Cash, 0m, grandTotal, CashRound(grandTotal)),
            TenderKind.Card => CardOnly(grandTotal),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), "Refunds are paid in cash or to the card."),
        };
    }

    private static PaymentPlan CardOnly(decimal grandTotal) =>
        new(TenderKind.Card, grandTotal, false, grandTotal, grandTotal, 0m, 0m, 0m, 0m, 0m);

    private PaymentPlan Split(decimal grandTotal, Tender tender)
    {
        if (money.DisableRoundedTotal)
        {
            if (tender.CardAmount <= 0m || tender.CardAmount >= grandTotal)
                throw new ArgumentOutOfRangeException(nameof(tender), "The card part must be more than zero and less than the bill total.");
            if (CashRound(grandTotal - tender.CardAmount) <= 0m)
                throw new ArgumentOutOfRangeException(nameof(tender), "The cash part rounds to zero; take the whole amount by card.");
            return WithCash(grandTotal, TenderKind.Split, tender.CardAmount, grandTotal - tender.CardAmount, tender.CashTendered);
        }

        var due = CashRound(grandTotal);
        if (tender.CardAmount <= 0m || tender.CardAmount >= due)
            throw new ArgumentOutOfRangeException(nameof(tender),
                $"The card part must be more than zero and less than the amount due ({due.ToString("0.00", CultureInfo.InvariantCulture)}); " +
                "for the whole amount by card use Card.");
        var cashDue = Rounder.Round(due - tender.CardAmount, money);
        // The drawer only has coins down to the smallest fraction (AED 0.25): a cash part like 10.12 would owe change it cannot give.
        var fraction = money.SmallestCurrencyFraction;
        if (fraction > 0m && cashDue % fraction != 0m)
        {
            var lowerCash = Math.Floor(cashDue / fraction) * fraction;
            var cards = new[] { due - lowerCash - fraction, due - lowerCash }.Where(c => c > 0m && c < due)
                .Select(c => c.ToString("0.00", CultureInfo.InvariantCulture));
            throw new ArgumentOutOfRangeException(nameof(tender),
                $"The cash part must be in steps of {fraction.ToString("0.00", CultureInfo.InvariantCulture)}: make the card part " +
                string.Join(" or ", cards) + ".");
        }
        var shortfall = Math.Max(0m, cashDue - tender.CashTendered);
        var change = Math.Max(0m, tender.CashTendered - cashDue);
        return new PaymentPlan(TenderKind.Split, grandTotal, true, due, tender.CardAmount, cashDue, tender.CashTendered, change, shortfall,
            Rounder.Round(due - grandTotal, money));
    }

    private decimal CashRound(decimal value) =>
        money.DisableRoundedTotal ? Rounder.Round(value, money) : Rounder.RoundToSmallestFraction(value, money);

    private PaymentPlan WithCash(decimal grandTotal, TenderKind kind, decimal card, decimal exactCash, decimal tendered)
    {
        var cashDue = CashRound(exactCash);
        var shortfall = Math.Max(0m, cashDue - tendered);
        var change = Math.Max(0m, tendered - cashDue);
        return new PaymentPlan(kind, grandTotal, kind == TenderKind.Cash && !money.DisableRoundedTotal, card + cashDue, card, cashDue,
            tendered, change, shortfall, Rounder.Round(cashDue - exactCash, money));
    }
}
