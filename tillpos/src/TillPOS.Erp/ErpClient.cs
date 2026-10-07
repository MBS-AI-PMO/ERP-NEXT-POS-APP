using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TillPOS.Erp;

/// <summary>Thin client for the Frappe REST API using token (API key/secret) auth. The till only uses its writer side in Live
/// upload mode; everything else gets it as a <see cref="ReadOnlyErpClient"/>.</summary>
public sealed class ErpClient : IErpClient, IErpWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly HttpClient http;

    public ErpClient(HttpClient http, ErpConnection conn)
    {
        this.http = http;
        http.BaseAddress = conn.BaseUrl;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", $"{conn.ApiKey}:{conn.ApiSecret}");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public static ErpClient Create(ErpConnection conn, TimeSpan? timeout = null) =>
        new(new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(30) }, conn);

    public async Task<IReadOnlyList<JsonElement>> GetListAsync(ListQuery query, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["doctype"] = query.Doctype,
            ["fields"] = query.Fields,
            ["filters"] = query.Filters,
            ["order_by"] = query.OrderBy,
            ["limit_start"] = query.Start,
            ["limit_page_length"] = query.PageLength,
        };
        var root = await SendAsync(HttpMethod.Post, "api/method/frappe.client.get_list", body, ct);
        return root.GetProperty("message").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    public async Task<int> GetCountAsync(string doctype, IReadOnlyList<object[]> filters, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["doctype"] = doctype, ["filters"] = filters };
        var root = await SendAsync(HttpMethod.Post, "api/method/frappe.client.get_count", body, ct);
        return root.GetProperty("message").GetInt32();
    }

    public async Task<JsonElement> GetDocAsync(string doctype, string name, CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Get, ResourcePath(doctype, name), null, ct)).GetProperty("data").Clone();

    public async Task<ServerInfo> PingAsync(CancellationToken ct = default)
    {
        using var resp = await http.GetAsync("api/method/frappe.auth.get_logged_user", ct);
        var root = await ReadAsync(resp, ct);
        return new ServerInfo(root.GetProperty("message").GetString() ?? "", resp.Headers.Date);
    }

    public async Task<JsonElement> InsertAsync(string doctype, object doc, CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Post, ResourcePath(doctype, null), doc, ct)).GetProperty("data").Clone();

    /// <summary>frappe.client.submit needs the whole document (it rebuilds it from the dict), so the saved draft is read first.</summary>
    public async Task<JsonElement> SubmitAsync(string doctype, string name, CancellationToken ct = default)
    {
        var doc = await GetDocAsync(doctype, name, ct);
        return await CallAsync("frappe.client.submit", new Dictionary<string, object?> { ["doc"] = doc }, ct);
    }

    public async Task<JsonElement> CallAsync(string method, object args, CancellationToken ct = default)
    {
        var root = await SendAsync(HttpMethod.Post, $"api/method/{method}", args, ct);
        return root.TryGetProperty("message", out var message) ? message.Clone() : root;
    }

    /// <summary>Deletes a document (tools only; not part of <see cref="IErpWriter"/>, the till never deletes).</summary>
    public async Task DeleteAsync(string doctype, string name, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Delete, ResourcePath(doctype, name), null, ct);

    private static string ResourcePath(string doctype, string? name) =>
        name is null
            ? $"api/resource/{Uri.EscapeDataString(doctype)}"
            : $"api/resource/{Uri.EscapeDataString(doctype)}/{Uri.EscapeDataString(name)}";

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body, options: JsonOptions);
        using var resp = await http.SendAsync(req, ct);
        return await ReadAsync(resp, ct);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonElement? root = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                root = doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        if (!resp.IsSuccessStatusCode) throw ErpException.From((int)resp.StatusCode, root, text);
        return root ?? throw new ErpException((int)resp.StatusCode, "Empty or non-JSON response from ERPNext.", null);
    }
}
