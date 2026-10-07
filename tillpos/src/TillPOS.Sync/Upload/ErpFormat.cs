using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TillPOS.Sync.Upload;

/// <summary>How payloads are written for ERPNext: snake_case keys (the dictionaries carry ERPNext's field names as they are),
/// dates and times in Frappe's text formats, taken from the till's wall clock when the event happened (the offset stored with it).</summary>
public static class ErpFormat
{
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Date(DateTimeOffset at) => at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Time(DateTimeOffset at) => at.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public static string DateTime(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The payload as JSON (compact for sending, indented for the DryRun preview files).</summary>
    public static string Json(object payload, bool indented = false) => JsonSerializer.Serialize(payload, indented ? Indented : Compact);
}
