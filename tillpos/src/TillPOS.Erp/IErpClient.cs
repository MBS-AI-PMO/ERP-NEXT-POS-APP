using System.Text.Json;

namespace TillPOS.Erp;

/// <summary>Read-only access to ERPNext. Writes live on <see cref="IErpWriter"/>, which the till only has in Live upload mode.</summary>
public interface IErpClient
{
    Task<IReadOnlyList<JsonElement>> GetListAsync(ListQuery query, CancellationToken ct = default);
    /// <summary>Number of rows of a doctype matching the filters (frappe.client.get_count, read-only).</summary>
    Task<int> GetCountAsync(string doctype, IReadOnlyList<object[]> filters, CancellationToken ct = default);
    Task<JsonElement> GetDocAsync(string doctype, string name, CancellationToken ct = default);
    Task<ServerInfo> PingAsync(CancellationToken ct = default);
}
