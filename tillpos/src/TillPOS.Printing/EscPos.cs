namespace TillPOS.Printing;

public enum Alignment : byte { Left = 0, Center = 1, Right = 2 }

/// <summary>Builds a raw ESC/POS byte stream for 80/58 mm thermal printers. Text is printable ASCII;
/// any other character prints as '?' (printer code pages differ; item names are English on live).</summary>
public sealed class EscPos
{
    private readonly List<byte> bytes = [];

    public EscPos Init() => Raw(0x1B, 0x40);
    public EscPos Align(Alignment alignment) => Raw(0x1B, 0x61, (byte)alignment);
    public EscPos Bold(bool on) => Raw(0x1B, 0x45, on ? (byte)1 : (byte)0);
    public EscPos Size(bool doubleWidth, bool doubleHeight) =>
        Raw(0x1D, 0x21, (byte)((doubleWidth ? 0x10 : 0) | (doubleHeight ? 0x01 : 0)));

    /// <summary>Sets alignment, bold and character size for a receipt line style; Normal resets all three.</summary>
    public EscPos Style(LineStyle style) => style switch
    {
        LineStyle.Bold => Align(Alignment.Left).Bold(true).Size(false, false),
        LineStyle.Title => Align(Alignment.Center).Bold(true).Size(true, true),
        LineStyle.Big => Align(Alignment.Left).Bold(true).Size(false, true),
        _ => Align(Alignment.Left).Bold(false).Size(false, false),
    };

    public EscPos Line(string text = "")
    {
        foreach (var ch in text) bytes.Add(ch is >= ' ' and <= '~' ? (byte)ch : (byte)'?');
        bytes.Add(0x0A);
        return this;
    }

    /// <summary>QR code (model 2, error correction M) via GS ( k.</summary>
    public EscPos Qr(string data, byte moduleSize = 6)
    {
        var payload = System.Text.Encoding.ASCII.GetBytes(data);
        var length = payload.Length + 3;
        Raw(0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00);
        Raw(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, moduleSize);
        Raw(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31);
        Raw(0x1D, 0x28, 0x6B, (byte)(length % 256), (byte)(length / 256), 0x31, 0x50, 0x30);
        bytes.AddRange(payload);
        return Raw(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30);
    }

    /// <summary>CODE128 via GS k 73 (height GS h 60, module width GS w, human-readable text below with GS H 2). Data is printable
    /// ASCII. Text goes in code set B; runs of four or more digits go in code set C (two digits per symbol), which keeps a
    /// 27-character invoice number at about 255 modules so it fits 80 mm paper at width 2.</summary>
    public EscPos Barcode128(string data, byte moduleWidth = 2)
    {
        if (data.Length == 0 || data.Any(ch => ch is < ' ' or > '~'))
            throw new ArgumentException("A CODE128 barcode holds printable ASCII text only.", nameof(data));
        if (moduleWidth is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(moduleWidth), "Module width is 1 to 6 dots.");
        var payload = Code128Data(data);
        if (payload.Count > 255) throw new ArgumentException("The barcode text is too long.", nameof(data));
        Raw(0x1D, 0x68, 60).Raw(0x1D, 0x77, moduleWidth).Raw(0x1D, 0x48, 0x02);
        Raw(0x1D, 0x6B, 0x49, (byte)payload.Count);
        bytes.AddRange(payload);
        return this;
    }

    /// <summary>GS k 73 data: "{B" + characters ('{' doubled) or "{C" + one byte (0–99) per digit pair.</summary>
    private static List<byte> Code128Data(string data)
    {
        var payload = new List<byte>();
        char? set = null;
        void Use(char codeSet)
        {
            if (set == codeSet) return;
            payload.Add((byte)'{');
            payload.Add((byte)codeSet);
            set = codeSet;
        }
        void AddB(char ch)
        {
            Use('B');
            if (ch == '{') payload.Add((byte)'{');
            payload.Add((byte)ch);
        }

        var i = 0;
        while (i < data.Length)
        {
            var end = i;
            while (end < data.Length && char.IsAsciiDigit(data[end])) end++;
            if (end - i < 4)
            {
                AddB(data[i++]);
                continue;
            }
            if ((end - i) % 2 == 1) AddB(data[i++]);           // an odd run: its first digit stays in set B
            Use('C');
            for (; i < end; i += 2) payload.Add((byte)((data[i] - '0') * 10 + (data[i + 1] - '0')));
        }
        return payload;
    }

    public EscPos Feed(int lines) => Raw(0x1B, 0x64, (byte)Math.Clamp(lines, 0, 255));
    public EscPos Cut() => Raw(0x1D, 0x56, 0x42, 0x00);
    /// <summary>ESC p 0 25 250 — pulse on drawer pin 2.</summary>
    public EscPos KickDrawer() => Raw(0x1B, 0x70, 0x00, 0x19, 0xFA);

    public byte[] ToArray() => bytes.ToArray();

    private EscPos Raw(params byte[] values)
    {
        bytes.AddRange(values);
        return this;
    }
}
