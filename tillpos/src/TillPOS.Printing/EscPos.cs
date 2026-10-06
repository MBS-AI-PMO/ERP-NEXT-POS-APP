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
