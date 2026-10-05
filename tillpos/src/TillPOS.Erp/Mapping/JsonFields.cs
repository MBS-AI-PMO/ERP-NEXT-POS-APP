using System.Globalization;
using System.Text.Json;

namespace TillPOS.Erp.Mapping;

/// <summary>Tolerant accessors: Frappe returns numbers as numbers or strings, and blanks as "" or null.</summary>
public static class JsonFields
{
    public static string Str(this JsonElement e, string p) =>
        e.StrOrNull(p) ?? throw new FormatException($"Missing text field '{p}'.");

    public static string? StrOrNull(this JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    public static decimal Dec(this JsonElement e, string p)
    {
        if (!e.TryGetProperty(p, out var v)) return 0m;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0m,
        };
    }

    public static int Int(this JsonElement e, string p) => (int)e.Dec(p);

    public static bool Bool(this JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && (v.ValueKind == JsonValueKind.True || e.Dec(p) != 0);

    public static DateOnly? Date(this JsonElement e, string p) =>
        e.StrOrNull(p) is { Length: >= 10 } s ? DateOnly.ParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    public static IEnumerable<JsonElement> Rows(this JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
}
