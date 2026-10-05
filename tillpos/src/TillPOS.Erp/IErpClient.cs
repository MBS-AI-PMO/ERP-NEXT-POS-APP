using System.Text.Json;

namespace TillPOS.Erp;

public interface IErpClient
{
    Task<IReadOnlyList<JsonElement>> GetListAsync(ListQuery query, CancellationToken ct = default);
    Task<JsonElement> GetDocAsync(string doctype, string name, CancellationToken ct = default);
    Task<ServerInfo> PingAsync(CancellationToken ct = default);
    Task<JsonElement> InsertAsync(string doctype, object doc, CancellationToken ct = default);
    Task DeleteAsync(string doctype, string name, CancellationToken ct = default);
}
