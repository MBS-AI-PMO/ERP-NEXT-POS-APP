using TillPOS.Core.Payments;

namespace TillPOS.Core.Sales;

public enum DeliveryStatus { Open, Paid, Cancelled }

/// <summary>A delivery bill kept on the till until the driver brings the money (never uploaded before). <see cref="Bill"/> is
/// the unpaid bill as made — its lines carry the fixed prices, it has no payments, and its ClientId is the delivery's number.
/// The amounts to collect: cash rounded like any cash bill, card to 2 decimals.</summary>
public sealed record Delivery(string ClientId, DeliveryStatus Status, DateTimeOffset CreatedAt, string ShiftClientId, string Cashier,
    string? CashierName, string? CounterName, Receipt Bill, decimal CashToCollect, decimal CardToCollect)
{
    /// <summary>When it was paid or cancelled.</summary>
    public DateTimeOffset? ClosedAt { get; init; }
    /// <summary>Who paid it (cashier id) or the supervisor who cancelled it.</summary>
    public string? ClosedBy { get; init; }
    /// <summary>A cancel's reason ("Refused", "Not delivered", "Other").</summary>
    public string? Reason { get; init; }
}

/// <summary>For the shift (Z) report: deliveries paid in the shift (already in its sales) and those still out.</summary>
public sealed record DeliverySummary(int PaidCount, decimal PaidTotal, IReadOnlyList<Delivery> StillOut);

public static class Deliveries
{
    /// <summary>The cart as an open delivery (the cart is not cleared).</summary>
    public static Delivery Make(Cart cart, string clientId, DateTimeOffset at, string shiftClientId, string cashier, string? cashierName,
        string? counterName)
    {
        if (cart.Lines.Count == 0) throw new InvalidOperationException("The bill is empty.");
        var bill = Bill(cart, clientId, at, shiftClientId, cashier, cashierName, counterName);
        var (cash, card) = ToCollect(cart);
        return new Delivery(clientId, DeliveryStatus.Open, at, shiftClientId, cashier, cashierName, counterName, bill, cash, card);
    }

    /// <summary>The delivery with the cart's (changed) lines: bill and amounts worked out again; number, time and maker kept.</summary>
    public static Delivery Rebill(Delivery d, Cart cart)
    {
        if (cart.Lines.Count == 0) throw new InvalidOperationException("A delivery needs at least one line; cancel it instead.");
        var (cash, card) = ToCollect(cart);
        return d with
        {
            Bill = Bill(cart, d.ClientId, d.CreatedAt, d.ShiftClientId, d.Cashier, d.CashierName, d.CounterName),
            CashToCollect = cash,
            CardToCollect = card,
        };
    }

    private static Receipt Bill(Cart cart, string clientId, DateTimeOffset at, string shiftClientId, string cashier, string? cashierName,
        string? counterName)
    {
        var totals = cart.Totals();
        return new Receipt(clientId, ReceiptKind.Sale, null, shiftClientId, cashier, at, SaleRecorder.ToLines(cart, totals),
            totals.Total, totals.NetTotal, totals.TotalTaxes, totals.GrandTotal, false, 0m, 0m, [], 0m, 0m, null)
        {
            CashierName = cashierName,
            CounterName = counterName,
            PosProfile = cart.Context.PosProfile,
            Warehouse = cart.Context.Warehouse,
            IsDelivery = true,
        };
    }

    private static (decimal Cash, decimal Card) ToCollect(Cart cart)
    {
        var calc = new PaymentCalculator(cart.Context.Money);
        var grand = cart.Totals().GrandTotal;
        return (calc.Plan(grand, Tender.Cash(0m)).AmountDue, calc.Plan(grand, Tender.Card()).AmountDue);
    }
}
