using System.Globalization;
using System.Text;

namespace TillPOS.Printing;

/// <summary>TLV Base64 payload for the receipt QR code: 1 seller name, 2 TRN, 3 timestamp (UTC, yyyy-MM-ddTHH:mm:ssZ),
/// 4 invoice total incl. VAT, 5 VAT amount (Master Spec §3).</summary>
public static class FtaQr
{
    public static string Encode(string sellerName, string trn, DateTimeOffset timestamp, decimal total, decimal vat)
    {
        using var stream = new MemoryStream();
        Write(stream, 1, sellerName);
        Write(stream, 2, trn);
        Write(stream, 3, timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        Write(stream, 4, total.ToString("0.00#", CultureInfo.InvariantCulture));
        Write(stream, 5, vat.ToString("0.00#", CultureInfo.InvariantCulture));
        return Convert.ToBase64String(stream.ToArray());
    }

    private static void Write(Stream stream, byte tag, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > 255) throw new ArgumentException($"QR field {tag} is longer than 255 bytes.", nameof(value));
        stream.WriteByte(tag);
        stream.WriteByte((byte)bytes.Length);
        stream.Write(bytes);
    }
}
