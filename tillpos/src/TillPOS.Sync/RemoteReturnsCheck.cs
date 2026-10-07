using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Sync.Feeds;

namespace TillPOS.Sync;

/// <summary>Checks, just before a bill of another till is refunded (or its return uploaded), what ERPNext has returned against it
/// by now, so the same goods are not refunded twice on two tills.</summary>
public interface IRemoteReturnsCheck
{
    /// <summary>Re-reads (read-only) the sale <paramref name="erpName"/> and the submitted return invoices against it, replacing
    /// the stored copies. False when the sale is no longer a submitted bill in ERPNext (cancelled, or not there). Network and
    /// ERPNext errors propagate.</summary>
    Task<bool> RefreshAsync(string erpName, CancellationToken ct);
}

/// <summary>The <see cref="IRemoteReturnsCheck"/> over the read-only ERPNext client: two list queries (the sale, its returns)
/// plus the item and payment rows. This till's own returns are left out: they are counted from the till's own bills.</summary>
public sealed class RemoteReturnsCheck(IErpClient reader, RemoteReceiptStore store, int tillNumber, Func<DateTimeOffset> now)
    : IRemoteReturnsCheck
{
    public async Task<bool> RefreshAsync(string erpName, CancellationToken ct)
    {
        IReadOnlyList<string> fields = ["name", .. InvoiceDownload.Fields];
        var sale = await reader.GetListAsync(new ListQuery(InvoiceDownload.Doctype, fields,
            [["name", "=", erpName], ["docstatus", "=", "1"], ["is_return", "=", "0"]], "name asc", 0, 1), ct);
        var returns = await reader.GetListAsync(new ListQuery(InvoiceDownload.Doctype, fields,
            [["docstatus", "=", "1"], ["is_return", "=", "1"], ["return_against", "=", erpName]], "name asc", 0, 0), ct);
        var read = await InvoiceDownload.WithLinesAsync(reader,
            [.. sale, .. returns.Where(r => !InvoiceDownload.IsOwn(r, tillNumber))], ct);
        var original = sale.Count > 0 ? read[0] : null;
        store.ReplaceReturnsAgainst(original?.ErpName ?? erpName, original, read.Skip(sale.Count), now());
        return original is not null;
    }
}
