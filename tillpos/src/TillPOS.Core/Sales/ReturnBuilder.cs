using TillPOS.Core.Payments;
using TillPOS.Core.Security;
using TillPOS.Core.Tax;

namespace TillPOS.Core.Sales;

public sealed record ReturnLineRequest(int LineNo, decimal Qty);

/// <summary>Returns taken on other tills against a sale (downloaded read-only from ERPNext), as read models whose lines carry
/// the sale's line numbers and negative quantities. The sale may be this till's (then by its ERPNext name once uploaded) or
/// another till's (whose <see cref="Receipt.ClientId"/> is its ERPNext name).</summary>
public interface IOtherTillReturns
{
    IReadOnlyList<Receipt> ReturnsAgainst(Receipt original);
}

/// <summary>What a return would refund, before it is saved. RefundDue is the rounded cash refund (negative);
/// Needs lists every supervisor approval the return requires (empty when none).</summary>
public sealed record ReturnPreview(decimal GrandTotal, decimal TotalTaxes, decimal RefundDue, decimal RoundingDifference,
    IReadOnlyList<ApprovalAction> Needs);

/// <summary>Builds and stores return receipts. Lines keep the original sale's line number and rate; quantities are negative.
/// A return without a receipt, refunds on one receipt that add up to more than the limit, or a receipt older than
/// maxAgeDays calendar days (till local time) need a supervisor. Refunds are paid in the modes of the shift's counter
/// (<paramref name="modes"/>), whose label (<paramref name="counterName"/>) is printed on the credit note.
/// <para>A sale of another till (downloaded from ERPNext) is returned the same way: its <see cref="Receipt.ClientId"/> is its
/// ERPNext name, which becomes the return's <see cref="Receipt.ReturnAgainst"/>. Returns taken on other tills
/// (<paramref name="otherTills"/>) count toward what is left and toward the refund limit.</para></summary>
public sealed class ReturnBuilder(IReceiptStore store, SaleContext ctx, int tillNumber, TenderModes modes, Func<DateTimeOffset> now,
    decimal approvalLimit = 50m, int maxAgeDays = 7, string? counterName = null, IOtherTillReturns? otherTills = null)
{
    public decimal Returnable(Receipt original, int lineNo) => original.Lines.Single(l => l.LineNo == lineNo).Qty - Returned(original, lineNo);

    /// <summary>How much of the line was returned already, on this till and on the others.</summary>
    public decimal Returned(Receipt original, int lineNo) => Returned(ReturnsOf(original), lineNo);

    /// <summary>Every return against the sale: this till's, then the other tills' (when known).</summary>
    public IReadOnlyList<Receipt> ReturnsOf(Receipt original) =>
        [.. store.ReturnsAgainst(original.ClientId), .. otherTills?.ReturnsAgainst(original) ?? []];

    private static decimal Returned(IReadOnlyList<Receipt> returns, int lineNo) =>
        returns.SelectMany(r => r.Lines).Where(l => l.LineNo == lineNo).Sum(l => -l.Qty);

    /// <summary>The return <see cref="Build"/> would make, checked the same way, without saving anything.</summary>
    public ReturnPreview Preview(Receipt original, IReadOnlyList<ReturnLineRequest> requests) => ToPreview(Prepare(original, requests));

    /// <summary>The return <see cref="BuildWithoutReceipt"/> would make, without saving anything or clearing the cart.</summary>
    public ReturnPreview PreviewWithoutReceipt(Cart cart) => ToPreview(PrepareWithoutReceipt(cart));

    /// <param name="approvedNeeds">The approvals the supervisor gave (from the preview). When given with
    /// <paramref name="approvedBy"/>, a return that now needs anything else (say the receipt just turned too old) is refused with
    /// <see cref="ApprovalRequiredException"/> and nothing is saved.</param>
    public Receipt Build(Receipt original, IReadOnlyList<ReturnLineRequest> requests, TenderKind refundKind, string cashier,
        string shiftClientId, string? approvedBy, string? reason = null, string? cashierUser = null, string? cashierName = null,
        IReadOnlyCollection<ApprovalAction>? approvedNeeds = null) =>
        Finish(Prepare(original, requests), refundKind, cashier, shiftClientId, approvedBy, reason, cashierUser, cashierName, approvedNeeds);

    /// <param name="approvedNeeds">As for <see cref="Build"/>.</param>
    public Receipt BuildWithoutReceipt(Cart cart, TenderKind refundKind, string cashier, string shiftClientId, string? approvedBy,
        string? reason = null, string? cashierUser = null, string? cashierName = null, IReadOnlyCollection<ApprovalAction>? approvedNeeds = null)
    {
        var receipt = Finish(PrepareWithoutReceipt(cart), refundKind, cashier, shiftClientId, approvedBy, reason, cashierUser, cashierName,
            approvedNeeds);
        cart.Clear();
        return receipt;
    }

    /// <summary>A checked, priced return that has not been saved.</summary>
    private sealed record Draft(IReadOnlyList<ReceiptLine> Lines, BillTotals Totals, string? ReturnAgainst, IReadOnlyList<ApprovalAction> Needs);

    private Draft Prepare(Receipt original, IReadOnlyList<ReturnLineRequest> requests)
    {
        if (original.Kind != ReceiptKind.Sale) throw new InvalidOperationException("Only a sale can be returned.");
        if (requests.Count == 0 || requests.Any(r => r.Qty <= 0m))
            throw new ArgumentException("Choose at least one line and a quantity above zero.", nameof(requests));
        if (requests.Select(r => r.LineNo).Distinct().Count() != requests.Count)
            throw new ArgumentException("Each line can appear only once.", nameof(requests));

        var returns = ReturnsOf(original);
        var lines = new List<ReceiptLine>();
        foreach (var request in requests)
        {
            var sold = original.Lines.SingleOrDefault(l => l.LineNo == request.LineNo)
                ?? throw new ArgumentException($"Line {request.LineNo} is not on receipt {original.ClientId}.", nameof(requests));
            if (!sold.FromScaleLabel && sold.Qty == decimal.Truncate(sold.Qty) && request.Qty != decimal.Truncate(request.Qty))
                throw new ArgumentException($"{sold.ItemName} is returned in whole units.", nameof(requests));
            var left = sold.Qty - Returned(returns, request.LineNo);
            if (request.Qty > left) throw new InvalidOperationException($"Only {left} of {sold.ItemName} can still be returned.");
            lines.Add(sold with { Qty = -request.Qty, Amount = 0m });
        }

        var totals = Price(lines);
        var needs = new List<ApprovalAction>();
        var alreadyRefunded = -returns.Sum(r => r.GrandTotal);
        if (alreadyRefunded - totals.GrandTotal > approvalLimit) needs.Add(ApprovalAction.ReturnOverLimit);
        if (DateOnly.FromDateTime(original.CreatedAt.LocalDateTime) < ctx.Today().AddDays(-maxAgeDays)) needs.Add(ApprovalAction.ReturnOldReceipt);
        return new Draft(WithAmounts(lines, totals), totals, original.ClientId, needs);
    }

    private Draft PrepareWithoutReceipt(Cart cart)
    {
        if (cart.Lines.Count == 0) throw new InvalidOperationException("Scan the items being returned first.");
        var lines = SaleRecorder.ToLines(cart, cart.Totals()).Select(l => l with { Qty = -l.Qty, Amount = 0m }).ToList();
        var totals = Price(lines);
        return new Draft(WithAmounts(lines, totals), totals, null, [ApprovalAction.ReturnWithoutReceipt]);
    }

    private BillTotals Price(IReadOnlyList<ReceiptLine> lines) =>
        new TaxCalculator(ctx.Money, ctx.Catalog.FindItemTaxTemplate)
            .Calculate(lines.Select(l => new TaxLineInput(l.Qty, l.Rate, l.ItemTaxTemplate)).ToList(), ctx.TaxTemplate);

    private static List<ReceiptLine> WithAmounts(IReadOnlyList<ReceiptLine> lines, BillTotals totals) =>
        lines.Select((l, i) => l with { Amount = totals.Lines[i].Amount }).ToList();

    private ReturnPreview ToPreview(Draft draft)
    {
        var cash = new PaymentCalculator(ctx.Money).PlanRefund(draft.Totals.GrandTotal, TenderKind.Cash);
        return new ReturnPreview(draft.Totals.GrandTotal, draft.Totals.TotalTaxes, cash.AmountDue, cash.RoundingDifference, draft.Needs);
    }

    private string NeedMessage(ApprovalAction need) => need switch
    {
        ApprovalAction.ReturnWithoutReceipt => "A return without a receipt needs a supervisor.",
        ApprovalAction.ReturnOverLimit => $"Refunds above {approvalLimit} on one receipt need a supervisor.",
        ApprovalAction.ReturnOldReceipt => $"Receipts older than {maxAgeDays} days need a supervisor.",
        _ => "This return needs a supervisor.",
    };

    private Receipt Finish(Draft draft, TenderKind refundKind, string cashier, string shiftClientId, string? approvedBy, string? reason,
        string? cashierUser, string? cashierName, IReadOnlyCollection<ApprovalAction>? approvedNeeds)
    {
        var approved = !string.IsNullOrWhiteSpace(approvedBy);
        if (!approved && draft.Needs.Count > 0) throw new ApprovalRequiredException(NeedMessage(draft.Needs[0]));
        if (approved && approvedNeeds is not null)
            foreach (var need in draft.Needs)
                if (!approvedNeeds.Contains(need)) throw new ApprovalRequiredException(NeedMessage(need));

        var totals = draft.Totals;
        var plan = new PaymentCalculator(ctx.Money).PlanRefund(totals.GrandTotal, refundKind);
        var at = now();
        var receipt = new Receipt(
            ClientIds.Receipt(tillNumber, at, store.NextSequence()), ReceiptKind.Return, draft.ReturnAgainst, shiftClientId, cashier, at,
            draft.Lines, totals.Total, totals.NetTotal, totals.TotalTaxes, totals.GrandTotal,
            plan.UsesErpRoundedTotal,
            plan.UsesErpRoundedTotal ? plan.AmountDue : 0m,
            plan.UsesErpRoundedTotal ? plan.RoundingDifference : 0m,
            SaleRecorder.Payments(plan, modes), 0m, plan.RoundingDifference, approved ? approvedBy : null)
        {
            CashierUser = cashierUser,
            CashierName = cashierName,
            Reason = reason,
            CounterName = counterName,
            PosProfile = ctx.PosProfile,
            Warehouse = ctx.Warehouse,
            DisableRoundedTotal = ctx.Money.DisableRoundedTotal,
        };
        store.Save(receipt);
        return receipt;
    }
}
