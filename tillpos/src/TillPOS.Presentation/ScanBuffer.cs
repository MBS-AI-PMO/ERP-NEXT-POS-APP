using System.Text;

namespace TillPOS.Presentation;

/// <summary>Tells barcode-scanner input (a fast burst of characters ending in Enter) apart from a person typing,
/// whatever control has focus (Master Spec §2: scanner as keyboard wedge, &lt;50 ms between keys).</summary>
public sealed class ScanBuffer(Func<DateTimeOffset> now, TimeSpan? maxGap = null, int minLength = 4)
{
    private readonly TimeSpan gap = maxGap ?? TimeSpan.FromMilliseconds(50);
    private readonly StringBuilder buffer = new();
    private DateTimeOffset last = DateTimeOffset.MinValue;

    /// <summary>Feed every typed character (Enter as '\r'); returns the barcode when a scan completes, otherwise null.</summary>
    public string? OnChar(char c)
    {
        var time = now();
        var fast = time - last <= gap;
        last = time;

        if (c is '\r' or '\n')
        {
            var code = fast && buffer.Length >= minLength ? buffer.ToString() : null;
            buffer.Clear();
            return code;
        }

        if (!fast) buffer.Clear();
        buffer.Append(c);
        return null;
    }
}
