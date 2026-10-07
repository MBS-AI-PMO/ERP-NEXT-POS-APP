using System.Text.Json;

namespace TillPOS.Erp;

/// <summary>The only way to change data in ERPNext. The till builds a real writer only when Upload = Live
/// (<c>UploadPipeline.LiveWriter</c>); in Off and DryRun mode nothing holds one, and <see cref="NoWriteErpWriter"/> stands in.</summary>
public interface IErpWriter
{
    /// <summary>Inserts a document (POST /api/resource/{doctype}); with docstatus 1 ERPNext submits it too. Returns the saved document.</summary>
    Task<JsonElement> InsertAsync(string doctype, object doc, CancellationToken ct = default);

    /// <summary>Submits a saved draft (frappe.client.submit). Returns the submitted document.</summary>
    Task<JsonElement> SubmitAsync(string doctype, string name, CancellationToken ct = default);

    /// <summary>Calls a whitelisted server method (POST /api/method/{method}) and returns its "message".</summary>
    Task<JsonElement> CallAsync(string method, object args, CancellationToken ct = default);
}

/// <summary>The writer of the Off and DryRun upload modes: every call throws, so a code path that tries to write fails loudly
/// instead of reaching ERPNext.</summary>
public sealed class NoWriteErpWriter : IErpWriter
{
    public static NoWriteErpWriter Instance { get; } = new();

    public Task<JsonElement> InsertAsync(string doctype, object doc, CancellationToken ct = default) => throw Off();
    public Task<JsonElement> SubmitAsync(string doctype, string name, CancellationToken ct = default) => throw Off();
    public Task<JsonElement> CallAsync(string method, object args, CancellationToken ct = default) => throw Off();

    private static InvalidOperationException Off() => new("Upload is off");
}

/// <summary>Hides the writer side of a client: what the till hands to the catalog sync and the uploader for reading cannot
/// be cast back to an <see cref="IErpWriter"/>.</summary>
public sealed class ReadOnlyErpClient(IErpClient inner) : IErpClient
{
    public Task<IReadOnlyList<JsonElement>> GetListAsync(ListQuery query, CancellationToken ct = default) => inner.GetListAsync(query, ct);
    public Task<int> GetCountAsync(string doctype, IReadOnlyList<object[]> filters, CancellationToken ct = default) =>
        inner.GetCountAsync(doctype, filters, ct);
    public Task<JsonElement> GetDocAsync(string doctype, string name, CancellationToken ct = default) => inner.GetDocAsync(doctype, name, ct);
    public Task<ServerInfo> PingAsync(CancellationToken ct = default) => inner.PingAsync(ct);
}
