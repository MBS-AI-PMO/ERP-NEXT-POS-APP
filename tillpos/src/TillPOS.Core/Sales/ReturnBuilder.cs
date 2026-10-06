using TillPOS.Core.Payments;
using TillPOS.Core.Security;
using TillPOS.Core.Tax;

namespace TillPOS.Core.Sales;

public sealed record ReturnLineRequest(int LineNo, decimal Qty);

/// <summary>Builds and stores return receipts. Lines keep the original sale's line number and rate; quantities are negative.
/// A return without a receipt, or a refund above the limit, needs a supervisor.</summary>
public sealed class ReturnBuilder(IReceiptStore store, SaleContext ctx, int tillNumber, TenderModes modes, Func<DateTimeOffset> now,
    decimal approvalLimit = 50m)
{
    public decimal Returnable(Receipt original, int lineNo)
    {
        var sold = original.Lines.Single(l => l.LineNo == lineNo);
        var returned = store.ReturnsAgainst(original.ClientId).SelectMany(r => r.Lines).Where(l => l.LineNo == lineNo).Sum(l => -l.Qty);
        return sold.Qty - returned;
    }

    public Receipt Build(Receipt original, IReadOnlyList<ReturnLineRequest> requests, TenderKind refundKind, string cashier,
        string shiftClientId, string? approvedBy)
    {
        if (original.Kind != ReceiptKind.Sale) throw new InvalidOperationException("Only a sale can be returned.");
        if (requests.Count == 0 || requests.Any(r => r.Qty <= 0m))
            throw new ArgumentException("Choose at least one line and a quantity above zero.", nameof(requests));
        if (requests.Select(r => r.LineNo).Distinct().Count() != requests.Count)
            throw new ArgumentException("Each line can appear only once.", nameof(requests));

        var lines = new List<ReceiptLine>();
        foreach (var request in requests)
        {
            var sold = original.Lines.SingleOrDefault(l => l.LineNo == request.LineNo)
                ?? throw new ArgumentException($"Line {request.LineNo} is not on receipt {original.ClientId}.", nameof(requests));
            var left = Returnable(original, request.LineNo);
            if (request.Qty > left) throw new InvalidOperationException($"Only {left} of {sold.ItemName} can still be returned.");
            lines.Add(sold with { Qty = -request.Qty, Amount = 0m });
        }
        return Finish(lines, original.ClientId, refundKind, cashier, shiftClientId, approvedBy, alwaysNeedsApproval: false);
    }

    public Receipt BuildWithoutReceipt(Cart cart, TenderKind refundKind, string cashier, string shiftClientId, string? approvedBy)
    {
        if (cart.Lines.Count == 0) throw new InvalidOperationException("Scan the items being returned first.");
        var lines = SaleRecorder.ToLines(cart, cart.Totals()).Select(l => l with { Qty = -l.Qty, Amount = 0m }).ToList();
        var receipt = Finish(lines, null, refundKind, cashier, shiftClientId, approvedBy, alwaysNeedsApproval: true);
        cart.Clear();
        return receipt;
    }

    private Receipt Finish(List<ReceiptLine> lines, string? returnAgainst, TenderKind refundKind, string cashier, string shiftClientId,
        string? approvedBy, bool alwaysNeedsApproval)
    {
        var totals = new TaxCalculator(ctx.Money, ctx.Catalog.FindItemTaxTemplate)
            .Calculate(lines.Select(l => new TaxLineInput(l.Qty, l.Rate, l.ItemTaxTemplate)).ToList(), ctx.TaxTemplate);
        lines = lines.Select((l, i) => l with { Amount = totals.Lines[i].Amount }).ToList();

        if (approvedBy is null)
        {
            if (alwaysNeedsApproval) throw new ApprovalRequiredException("A return without a receipt needs a supervisor.");
            if (-totals.GrandTotal > approvalLimit) throw new ApprovalRequiredException($"A refund above {approvalLimit} needs a supervisor.");
        }

        var plan = new PaymentCalculator(ctx.Money).PlanRefund(totals.GrandTotal, refundKind);
        var at = now();
        var receipt = new Receipt(
            ClientIds.Receipt(tillNumber, at, store.NextSequence()), ReceiptKind.Return, returnAgainst, shiftClientId, cashier, at,
            lines, totals.Total, totals.NetTotal, totals.TotalTaxes, totals.GrandTotal,
            plan.UsesErpRoundedTotal,
            plan.UsesErpRoundedTotal ? plan.AmountDue : 0m,
            plan.UsesErpRoundedTotal ? plan.RoundingDifference : 0m,
            SaleRecorder.Payments(plan, modes), 0m, plan.RoundingDifference, approvedBy);
        store.Save(receipt);
        return receipt;
    }
}
