using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TillPOS.Sync.Upload;

/// <summary>How payloads are written for ERPNext: snake_case keys (the dictionaries carry ERPNext's field names as they are),
/// dates and times in Frappe's text formats, in ERPNext's time zone (<see cref="ErpOffset"/>) whatever the till PC's own time
/// zone is: a PC on another zone (e.g. Pakistan, UTC+5) would otherwise post its bills an hour ahead of ERPNext's clock, and
/// the closing's consolidation then refuses a credit note dated before the sales it returns.</summary>
public static class ErpFormat
{
    /// <summary>ERPNext's site time zone: Asia/Dubai (UTC+4, no daylight saving).</summary>
    public static readonly TimeSpan ErpOffset = TimeSpan.FromHours(4);

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Date(DateTimeOffset at) => at.ToOffset(ErpOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Time(DateTimeOffset at) => at.ToOffset(ErpOffset).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public static string DateTime(DateTimeOffset at) => at.ToOffset(ErpOffset).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The payload as JSON (compact for sending, indented for the DryRun preview files).</summary>
    public static string Json(object payload, bool indented = false) => JsonSerializer.Serialize(payload, indented ? Indented : Compact);
}
