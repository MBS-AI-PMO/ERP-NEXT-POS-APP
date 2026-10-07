using System.Text.Json;
using TillPOS.Erp;

namespace TillPOS.Tests.Fakes;

/// <summary>In-memory ERPNext: list queries filter/sort rows of a doctype; GetDoc returns Docs entries. As a writer it records
/// every insert, submit and call.</summary>
public sealed class FakeErp : IErpClient, IErpWriter
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

    /// <summary>Every insert attempt, refused ones included.</summary>
    public List<(string Doctype, JsonElement Doc)> Inserted { get; } = [];

    /// <summary>Fields ERPNext adds to an inserted document (e.g. grand_total); they are in the answer and on its stored row.</summary>
    public Func<string, JsonElement, Dictionary<string, object?>?>? OnInsert { get; set; }

    /// <summary>Return an exception to refuse an insert: nothing is saved.</summary>
    public Func<string, JsonElement, Exception?>? RejectInsert { get; set; }

    /// <summary>Return an exception to lose an insert's answer: the document IS saved (and found by later lookups), like a
    /// connection dropped after ERPNext committed.</summary>
    public Func<string, JsonElement, Exception?>? LoseInsertAnswer { get; set; }

    private int inserts;

    /// <summary>Saves the document as a row of its doctype (top-level fields, a generated name, docstatus from the document)
    /// and answers with the whole document plus the name and the <see cref="OnInsert"/> fields.</summary>
    public Task<JsonElement> InsertAsync(string doctype, object doc, CancellationToken ct = default)
    {
        var e = JsonSerializer.SerializeToElement(doc);
        Inserted.Add((doctype, e));
        if (RejectInsert?.Invoke(doctype, e) is { } refused) throw refused;

        var answer = e.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        answer["name"] = $"{doctype.Replace(" ", "-", StringComparison.Ordinal)}-{++inserts:00000}";
        foreach (var (key, value) in OnInsert?.Invoke(doctype, e) ?? []) answer[key] = value;
        AddRow(doctype, answer.Where(p => p.Value is not JsonElement { ValueKind: JsonValueKind.Array })
            .ToDictionary(p => p.Key, p => p.Value is JsonElement v ? Scalar(v) : p.Value));

        if (LoseInsertAnswer?.Invoke(doctype, e) is { } lost) throw lost;
        return Task.FromResult(JsonSerializer.SerializeToElement(answer));
    }

    private static object? Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetDecimal(),
        JsonValueKind.Null => null,
        _ => v.ToString(),
    };

    public List<(string Doctype, string Name)> Submitted { get; } = [];

    /// <summary>Return an exception to refuse a submit (the draft stays a draft).</summary>
    public Func<string, string, Exception?>? RejectSubmit { get; set; }

    /// <summary>Fields ERPNext changes when it submits (e.g. outstanding_amount); also kept on the stored row.</summary>
    public Func<string, string, Dictionary<string, object?>?>? OnSubmit { get; set; }

    /// <summary>Submits a stored draft: its row gets docstatus 1 (plus the <see cref="OnSubmit"/> fields) and is the answer.</summary>
    public Task<JsonElement> SubmitAsync(string doctype, string name, CancellationToken ct = default)
    {
        Submitted.Add((doctype, name));
        if (RejectSubmit?.Invoke(doctype, name) is { } refused) throw refused;
        var row = tables.GetValueOrDefault(doctype)?.FirstOrDefault(r => r.GetValueOrDefault("name")?.ToString() == name)
            ?? throw new ErpException(404, $"{doctype} {name} not found", "DoesNotExistError");
        row["docstatus"] = 1m;
        foreach (var (key, value) in OnSubmit?.Invoke(doctype, name) ?? []) row[key] = value;
        return Task.FromResult(JsonSerializer.SerializeToElement(row));
    }

    public List<(string Method, JsonElement Args)> Calls { get; } = [];

    public Task<JsonElement> CallAsync(string method, object args, CancellationToken ct = default)
    {
        Calls.Add((method, JsonSerializer.SerializeToElement(args)));
        return Task.FromResult(JsonSerializer.SerializeToElement<object?>(null));
    }

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
