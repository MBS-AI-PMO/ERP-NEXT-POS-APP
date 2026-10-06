using TillPOS.Core.Payments;
using TillPOS.Core.Tax;

namespace TillPOS.Core.Sales;

/// <summary>Turns a paid cart into a stored receipt (the outbox entry Plan 2b uploads) and clears the cart.</summary>
public sealed class SaleRecorder(IReceiptStore store, int tillNumber, TenderModes modes, Func<DateTimeOffset> now)
{
    public Receipt CompleteSale(Cart cart, PaymentPlan plan, string cashier, string shiftClientId, string? cashierUser = null)
    {
        if (cart.Lines.Count == 0) throw new InvalidOperationException("The bill is empty.");
        var totals = cart.Totals();
        if (plan.GrandTotal != totals.GrandTotal)
            throw new InvalidOperationException("The payment was calculated for a different total; calculate it again.");
        if (!plan.IsComplete) throw new InvalidOperationException($"Still to pay: {plan.Shortfall}.");

        var at = now();
        var receipt = new Receipt(
            ClientIds.Receipt(tillNumber, at, store.NextSequence()), ReceiptKind.Sale, null, shiftClientId, cashier, at,
            ToLines(cart, totals), totals.Total, totals.NetTotal, totals.TotalTaxes, totals.GrandTotal,
            plan.UsesErpRoundedTotal,
            plan.UsesErpRoundedTotal ? plan.AmountDue : 0m,
            plan.UsesErpRoundedTotal ? plan.RoundingDifference : 0m,
            Payments(plan, modes), plan.Change, plan.RoundingDifference, null)
        {
            CashierUser = cashierUser,
        };
        store.Save(receipt);
        cart.Clear();
        return receipt;
    }

    internal static IReadOnlyList<ReceiptLine> ToLines(Cart cart, BillTotals totals) =>
        cart.Lines.Select((l, i) => new ReceiptLine(i + 1, l.Item.ItemCode, l.Item.ItemName, l.Barcode, l.Uom, l.ConversionFactor,
            l.Qty, l.PriceListRate, l.Rate, totals.Lines[i].Amount, l.Rule?.RuleName, l.ItemTaxTemplate, l.FromScaleLabel,
            l.UomFallbackFrom)).ToList();

    internal static IReadOnlyList<ReceiptPayment> Payments(PaymentPlan plan, TenderModes modes)
    {
        var rows = new List<ReceiptPayment>();
        if (plan.CardAmount != 0m) rows.Add(new ReceiptPayment(modes.Card, plan.CardAmount));
        if (plan.CashTendered != 0m) rows.Add(new ReceiptPayment(modes.Cash, plan.CashTendered));
        return rows;
    }
}
