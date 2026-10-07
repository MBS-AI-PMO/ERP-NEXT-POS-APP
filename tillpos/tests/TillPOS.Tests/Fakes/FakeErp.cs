using System.Text.Json;
using TillPOS.Erp;

namespace TillPOS.Tests.Fakes;

/// <summary>In-memory ERPNext: list queries filter/sort rows of a doctype; GetDoc returns Docs entries.</summary>
public sealed class FakeErp : IErpClient
{
    private readonly Dictionary<string, List<Dictionary<string, object?>>> tables = [];

    public Dictionary<(string Doctype, string Name), object> Docs { get; } = [];
    public List<ListQuery> ListCalls { get; } = [];
    /// <summary>Return a non-null list to answer a query yourself (e.g. child-table joins).</summary>
    public Func<ListQuery, IReadOnlyList<object>?>? Override { get; set; }
    /// <summary>Return an exception to make a query fail.</summary>
    public Func<ListQuery, Exception?>? Fail { get; set; }

    public void AddRow(string doctype, Dictionary<string, object?> row)
    {
        if (!tables.TryGetValue(doctype, out var t)) tables[doctype] = t = [];
        t.Add(row);
    }

    public Task<IReadOnlyList<JsonElement>> GetListAsync(ListQuery q, CancellationToken ct = default)
    {
        ListCalls.Add(q);
        if (Fail?.Invoke(q) is { } ex) throw ex;
        var rows = Override?.Invoke(q) ?? Filtered(q);
        return Task.FromResult<IReadOnlyList<JsonElement>>(rows.Select(r => JsonSerializer.SerializeToElement(r)).ToList());
    }

    public List<(string Doctype, IReadOnlyList<object[]> Filters)> CountCalls { get; } = [];
    /// <summary>Return an exception to make a count fail.</summary>
    public Func<string, Exception?>? FailCount { get; set; }

    /// <summary>Counts the doctype's rows matching the filters (same operators as list queries).</summary>
    public Task<int> GetCountAsync(string doctype, IReadOnlyList<object[]> filters, CancellationToken ct = default)
    {
        CountCalls.Add((doctype, filters));
        if (FailCount?.Invoke(doctype) is { } ex) throw ex;
        return Task.FromResult(Filtered(new ListQuery(doctype, ["name"], filters, "", 0, 0)).Count);
    }

    public Task<JsonElement> GetDocAsync(string doctype, string name, CancellationToken ct = default) =>
        Docs.TryGetValue((doctype, name), out var d)
            ? Task.FromResult(JsonSerializer.SerializeToElement(d))
            : throw new ErpException(404, $"{doctype} {name} not found", "DoesNotExistError");

    public Task<ServerInfo> PingAsync(CancellationToken ct = default) =>
        Task.FromResult(new ServerInfo("till1@shop.local", DateTimeOffset.UtcNow));

    public List<(string Doctype, JsonElement Doc)> Inserted { get; } = [];

    public Task<JsonElement> InsertAsync(string doctype, object doc, CancellationToken ct = default)
    {
        var e = JsonSerializer.SerializeToElement(doc);
        Inserted.Add((doctype, e));
        return Task.FromResult(e);
    }

    public Task DeleteAsync(string doctype, string name, CancellationToken ct = default) => Task.CompletedTask;

    private List<object> Filtered(ListQuery q)
    {
        IEnumerable<Dictionary<string, object?>> rows = tables.TryGetValue(q.Doctype, out var t) ? t : [];
        foreach (var f in q.Filters)
        {
            var field = (string)f[0];
            var op = (string)f[1];
            var value = f[2];
            rows = rows.Where(r => Match(r.GetValueOrDefault(field), op, value));
        }
        var ordered = rows
            .OrderBy(r => r.GetValueOrDefault("modified")?.ToString(), StringComparer.Ordinal)
            .ThenBy(r => r["name"]?.ToString(), StringComparer.Ordinal)
            .Skip(q.Start);
        return (q.PageLength > 0 ? ordered.Take(q.PageLength) : ordered).Cast<object>().ToList();
    }

    private static bool Match(object? actual, string op, object? expected) => op switch
    {
        ">=" => string.CompareOrdinal(actual?.ToString(), expected?.ToString()) >= 0,
        ">" => string.CompareOrdinal(actual?.ToString(), expected?.ToString()) > 0,
        "=" => Equals(actual?.ToString(), expected?.ToString()),
        "in" => expected is IEnumerable<string> set && set.Contains(actual?.ToString()),
        _ => throw new NotSupportedException($"FakeErp does not support operator '{op}'."),
    };
}
