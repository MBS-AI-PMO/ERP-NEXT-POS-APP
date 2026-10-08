using TillPOS.Core.Payments;
using TillPOS.Core.Tax;

namespace TillPOS.Core.Sales;

/// <summary>Turns a paid cart into a stored receipt (the outbox entry Plan 2b uploads) and clears the cart.
/// <paramref name="modes"/> and <paramref name="counterName"/> are those of the shift's counter.</summary>
public sealed class SaleRecorder(IReceiptStore store, int tillNumber, TenderModes modes, Func<DateTimeOffset> now, string? counterName = null)
{
    public Receipt CompleteSale(Cart cart, PaymentPlan plan, string cashier, string shiftClientId, string? cashierUser = null,
        string? cashierName = null)
    {
        var receipt = Build(cart, plan, cashier, shiftClientId, cashierUser, cashierName, null);
        store.Save(receipt);
        cart.Clear();
        return receipt;
    }

    /// <summary>The paid bill as a receipt, not saved and the cart not cleared. <paramref name="deliveryClientId"/>: a delivery
    /// being paid keeps its number (and the receipt is marked <see cref="Receipt.IsDelivery"/>); null takes the next number.</summary>
    public Receipt Build(Cart cart, PaymentPlan plan, string cashier, string shiftClientId, string? cashierUser, string? cashierName,
        string? deliveryClientId)
    {
        if (cart.Lines.Count == 0) throw new InvalidOperationException("The bill is empty.");
        var totals = cart.Totals();
        if (plan.GrandTotal != totals.GrandTotal)
            throw new InvalidOperationException("The payment was calculated for a different total; calculate it again.");
        if (!plan.IsComplete) throw new InvalidOperationException($"Still to pay: {plan.Shortfall}.");

        var at = now();
        // Card only with rounding on: the card pays the exact total, ERPNext still gets its rounded total (Receipt.ExactCardOnRoundedTotal).
        var exactCard = plan.Kind == TenderKind.Card && !totals.RoundedTotalDisabled;
        return new Receipt(
            deliveryClientId ?? ClientIds.Receipt(tillNumber, at, store.NextSequence()), ReceiptKind.Sale, null, shiftClientId, cashier, at,
            ToLines(cart, totals), totals.Total, totals.NetTotal, totals.TotalTaxes, totals.GrandTotal,
            plan.UsesErpRoundedTotal,
            plan.UsesErpRoundedTotal ? plan.AmountDue : exactCard ? totals.RoundedTotal : 0m,
            plan.UsesErpRoundedTotal ? plan.RoundingDifference : exactCard ? totals.RoundingAdjustment : 0m,
            Payments(plan, modes), plan.Change, plan.RoundingDifference, null)
        {
            ExactCardOnRoundedTotal = exactCard,
            CashierUser = cashierUser,
            CashierName = cashierName,
            CounterName = counterName,
            PosProfile = cart.Context.PosProfile,
            Warehouse = cart.Context.Warehouse,
            DisableRoundedTotal = cart.Context.Money.DisableRoundedTotal,
            IsDelivery = deliveryClientId is not null,
        };
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
