using System.Text.Json;
using System.Text.RegularExpressions;

namespace TillPOS.Erp;

public sealed partial class ErpException(int statusCode, string message, string? excType) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? ExcType { get; } = excType;

    public static ErpException From(int status, JsonElement? body, string raw)
    {
        string? excType = null, message = null;
        if (body is { ValueKind: JsonValueKind.Object } b)
        {
            if (b.TryGetProperty("exc_type", out var t) && t.ValueKind == JsonValueKind.String) excType = t.GetString();
            if (b.TryGetProperty("_server_messages", out var sm) && sm.ValueKind == JsonValueKind.String) message = FirstServerMessage(sm.GetString()!);
            if (message is null && b.TryGetProperty("exception", out var ex) && ex.ValueKind == JsonValueKind.String) message = ex.GetString();
            if (message is null && b.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
        }
        return new ErpException(status, message ?? $"ERPNext returned HTTP {status}: {Truncate(raw, 200)}", excType);
    }

    // _server_messages is a JSON array of JSON-encoded objects: "[\"{\\\"message\\\": \\\"...\\\"}\"]"
    private static string? FirstServerMessage(string serverMessages)
    {
        try
        {
            using var outer = JsonDocument.Parse(serverMessages);
            foreach (var item in outer.RootElement.EnumerateArray())
            {
                if (item.GetString() is not { } inner) continue;
                using var msg = JsonDocument.Parse(inner);
                if (msg.RootElement.TryGetProperty("message", out var m) && m.GetString() is { } text) return Html().Replace(text, "");
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    [GeneratedRegex("<.*?>")]
    private static partial Regex Html();
}
