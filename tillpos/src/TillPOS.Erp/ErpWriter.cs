using System.Text.Json;

namespace TillPOS.Erp;

/// <summary>The writer over an <see cref="ErpClient"/>: a separate object, so what holds the writer cannot reach the client's
/// other side, and what holds the client cannot write.</summary>
public sealed class ErpWriter(ErpClient client) : IErpWriter
{
    public Task<JsonElement> InsertAsync(string doctype, object doc, CancellationToken ct = default) => client.InsertAsync(doctype, doc, ct);

    /// <summary>frappe.client.submit needs the whole document (it rebuilds it from the dict), so the saved draft is read first.</summary>
    public async Task<JsonElement> SubmitAsync(string doctype, string name, CancellationToken ct = default)
    {
        var doc = await client.GetDocAsync(doctype, name, ct);
        return await client.CallAsync("frappe.client.submit", new Dictionary<string, object?> { ["doc"] = doc }, ct);
    }

    public Task<JsonElement> CallAsync(string method, object args, CancellationToken ct = default) => client.CallAsync(method, args, ct);

    /// <summary>Deletes a document (tools only; not part of <see cref="IErpWriter"/>: the till never deletes).</summary>
    public Task DeleteAsync(string doctype, string name, CancellationToken ct = default) => client.DeleteAsync(doctype, name, ct);
}
