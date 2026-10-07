using System.Globalization;
using System.Text.Json;
using TillPOS.Core.Sales;

namespace TillPOS.Data;

/// <summary>One item row of a downloaded POS Invoice. <see cref="RowId"/> is its posa_row_id (TillPOS sends the till's line
/// number; POS Awesome a random id), or null.</summary>
public sealed record RemoteLine(string? RowId, string ItemCode, string ItemName, decimal Qty, string Uom, decimal ConversionFactor,
    decimal Rate, decimal PriceListRate, decimal Amount, string? Barcode, string? ItemTaxTemplate);

public sealed record RemotePayment(string ModeOfPayment, decimal Amount);

/// <summary>A submitted POS Invoice of another till (or of a POS Awesome counter), downloaded read-only so that it can be
/// returned here. <see cref="Posting"/> is ERPNext's posting date and time (the shop's local time). A return invoice has
/// <see cref="IsReturn"/> and negative quantities.</summary>
public sealed record RemoteReceipt(
    string ErpName,
    string? ClientRequestId,
    string? Till,
    string? PosProfile,
    DateTime Posting,
    string? Customer,
    decimal GrandTotal,
    decimal RoundedTotal,
    decimal NetTotal,
    decimal TotalTaxes,
    bool IsReturn,
    string? ReturnAgainst,
    IReadOnlyList<RemoteLine> Lines,
    IReadOnlyList<RemotePayment> Payments)
{
    /// <summary>ERPNext's whole-bill discount (discount_amount) and its percentage (additional_discount_percentage): the till
    /// cannot re-price such a bill, so it is not returned here.</summary>
    public decimal DiscountAmount { get; init; }

    public decimal AdditionalDiscountPercentage { get; init; }

    /// <summary>The line numbers given to <see cref="Lines"/>: their posa_row_id when every one is a distinct positive number
    /// (a TillPOS bill keeps its own line numbers), otherwise their position (1, 2, …).</summary>
    public IReadOnlyList<int> LineNumbers()
    {
        var ids = Lines.Select(l => int.TryParse(l.RowId, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0).ToList();
        return ids.All(n => n > 0) && ids.Distinct().Count() == ids.Count ? ids : Enumerable.Range(1, Lines.Count).ToList();
    }

    /// <summary>The bill as a <see cref="Receipt"/> read model: its ERPNext name stands for its number (so a return of it is
    /// made against that name), its posting time is its time, and it belongs to no shift of this till.</summary>
    public Receipt ToReceipt()
    {
        var numbers = LineNumbers();
        var lines = Lines.Select((l, i) => new ReceiptLine(numbers[i], l.ItemCode, l.ItemName, l.Barcode, l.Uom, l.ConversionFactor, l.Qty,
            l.PriceListRate, l.Rate, l.Amount, null, l.ItemTaxTemplate, false, null)).ToList();
        var rounded = RoundedTotal != 0m;
        return new Receipt(ErpName, IsReturn ? ReceiptKind.Return : ReceiptKind.Sale, ReturnAgainst, "", "",
            new DateTimeOffset(Posting, TimeZoneInfo.Local.GetUtcOffset(Posting)), lines, lines.Sum(l => l.Amount), NetTotal, TotalTaxes,
            GrandTotal, rounded, RoundedTotal, rounded ? RoundedTotal - GrandTotal : 0m,
            Payments.Select(p => new ReceiptPayment(p.ModeOfPayment, p.Amount)).ToList(), 0m, 0m, null)
        {
            PosProfile = PosProfile,
            Customer = Customer,
        };
    }
}

/// <summary>The recent POS Invoices of the other tills (the "remote receipts", refreshed by the RecentInvoicesFeed), so a bill
/// from another till can be returned here and every till's returns count toward what is left. Read-only copies: nothing here is
/// ever uploaded. ERPNext names and client ids are matched ignoring case.</summary>
public sealed class RemoteReceiptStore(TillDb db) : IOtherTillReturns
{
    private const string PostingFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Adds the invoice, or replaces the copy downloaded before.</summary>
    public void Upsert(RemoteReceipt receipt, DateTimeOffset fetchedAt)
    {
        using var c = db.Open();
        c.Exec(null, """
            INSERT INTO remote_receipt (erp_name, client_request_id, till, pos_profile, posting, customer, grand_total, rounded_total,
                is_return, return_against, json, fetched_at)
            VALUES (@n, @cid, @till, @p, @at, @cu, @g, @r, @ret, @ra, @j, @f)
            ON CONFLICT(erp_name) DO UPDATE SET client_request_id = excluded.client_request_id, till = excluded.till,
                pos_profile = excluded.pos_profile, posting = excluded.posting, customer = excluded.customer,
                grand_total = excluded.grand_total, rounded_total = excluded.rounded_total, is_return = excluded.is_return,
                return_against = excluded.return_against, json = excluded.json, fetched_at = excluded.fetched_at
            """,
            ("@n", receipt.ErpName), ("@cid", receipt.ClientRequestId), ("@till", receipt.Till), ("@p", receipt.PosProfile),
            ("@at", receipt.Posting.ToString(PostingFormat, CultureInfo.InvariantCulture)), ("@cu", receipt.Customer),
            ("@g", SqlExt.Dec(receipt.GrandTotal)), ("@r", SqlExt.Dec(receipt.RoundedTotal)), ("@ret", receipt.IsReturn ? 1 : 0),
            ("@ra", receipt.ReturnAgainst), ("@j", JsonSerializer.Serialize(receipt, ReceiptStore.Json)), ("@f", SqlExt.Instant(fetchedAt)));
    }

    /// <summary>Removes an invoice (cancelled in ERPNext).</summary>
    public void Delete(string erpName)
    {
        using var c = db.Open();
        c.Exec(null, "DELETE FROM remote_receipt WHERE erp_name = @n", ("@n", erpName));
    }

    public RemoteReceipt? FindByErpName(string erpName) =>
        Query("WHERE erp_name = @p COLLATE NOCASE", erpName).FirstOrDefault();

    /// <summary>The invoice with this client id (posa_client_request_id), or null.</summary>
    public RemoteReceipt? FindByClientId(string clientId) =>
        Query("WHERE client_request_id = @p COLLATE NOCASE ORDER BY is_return, posting DESC", clientId).FirstOrDefault();

    public int Count()
    {
        using var c = db.Open();
        return Convert.ToInt32(c.Scalar(null, "SELECT COUNT(*) FROM remote_receipt"), CultureInfo.InvariantCulture);
    }

    /// <summary>Removes the invoices posted more than <paramref name="days"/> days before <paramref name="today"/>; returns how many.</summary>
    public int DeleteOlderThan(int days, DateOnly today)
    {
        using var c = db.Open();
        var cutoff = today.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return c.Exec(null, "DELETE FROM remote_receipt WHERE posting < @c", ("@c", cutoff));
    }

    /// <summary>How much of each line of the remote sale <paramref name="erpName"/> was returned already: on this till and on
    /// the others. Empty when the sale is not downloaded.</summary>
    public IReadOnlyDictionary<int, decimal> ReturnedQtyByLine(string erpName)
    {
        if (FindByErpName(erpName) is not { IsReturn: false } remote) return new Dictionary<int, decimal>();
        var original = remote.ToReceipt();
        return new ReceiptStore(db).ReturnsAgainst(original.ClientId).Concat(ReturnsAgainst(original))
            .SelectMany(r => r.Lines)
            .GroupBy(l => l.LineNo)
            .ToDictionary(g => g.Key, g => -g.Sum(l => l.Qty));
    }

    /// <summary>The return invoices of other tills against <paramref name="original"/>: a downloaded sale (by its ERPNext name), or
    /// a sale of this till once it is uploaded (by the ERPNext name it got). Each return line is given the sale's line number:
    /// the sale line with the same posa_row_id and item, else the first line of that item with something left (lines of items not
    /// on the sale are left out).</summary>
    public IReadOnlyList<Receipt> ReturnsAgainst(Receipt original)
    {
        string? erpName;
        Dictionary<string, int> rowIds;
        if (FindByErpName(original.ClientId) is { IsReturn: false } remote)
        {
            erpName = remote.ErpName;
            var numbers = remote.LineNumbers();
            rowIds = [];
            for (var i = 0; i < remote.Lines.Count; i++)
                if (remote.Lines[i].RowId is { Length: > 0 } id) rowIds.TryAdd(id, numbers[i]);
        }
        else
        {
            using var c = db.Open();
            erpName = c.Scalar(null, "SELECT erp_name FROM receipt WHERE client_id = @id AND sync_status = 'Synced'", ("@id", original.ClientId))
                as string;
            rowIds = original.Lines.ToDictionary(l => l.LineNo.ToString(CultureInfo.InvariantCulture), l => l.LineNo);
        }
        if (string.IsNullOrEmpty(erpName)) return [];

        var left = original.Lines.ToDictionary(l => l.LineNo, l => l.Qty);
        var result = new List<Receipt>();
        foreach (var ret in Query("WHERE is_return = 1 AND return_against = @p COLLATE NOCASE ORDER BY posting, erp_name", erpName))
        {
            var receipt = ret.ToReceipt();
            var lines = new List<ReceiptLine>();
            for (var i = 0; i < ret.Lines.Count; i++)
            {
                var line = ret.Lines[i];
                int? lineNo = line.RowId is { } id && rowIds.TryGetValue(id, out var n) && original.Lines.Any(o => o.LineNo == n && o.ItemCode == line.ItemCode)
                    ? n
                    : null;
                lineNo ??= original.Lines.Where(o => o.ItemCode == line.ItemCode)
                    .OrderBy(o => left[o.LineNo] > 0m ? 0 : 1)
                    .Select(o => (int?)o.LineNo)
                    .FirstOrDefault();
                if (lineNo is not { } no) continue;
                left[no] += line.Qty;
                lines.Add(receipt.Lines[i] with { LineNo = no });
            }
            result.Add(receipt with { ReturnAgainst = original.ClientId, Lines = lines });
        }
        return result;
    }

    private List<RemoteReceipt> Query(string where, string p)
    {
        using var c = db.Open();
        return c.Query($"SELECT json FROM remote_receipt {where}",
            r => JsonSerializer.Deserialize<RemoteReceipt>(r.GetString(0), ReceiptStore.Json)
                ?? throw new InvalidDataException("Stored remote receipt is empty."), ("@p", p));
    }
}
