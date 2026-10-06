using System.Globalization;

namespace TillPOS.Printing;

/// <summary>The setup screen's test page: proves the printer, the paper width (a ruler as wide as the paper) and the
/// cash drawer. No receipt or shop data is needed.</summary>
public static class TestPrint
{
    public const string Title = "TillPOS test print";

    public static IReadOnlyList<string> Lines(string printerName, PaperWidth paper, DateTime at)
    {
        var width = (int)paper;
        var lines = new List<string> { Title, "" };
        lines.AddRange(Chunk($"Printer: {printerName}", width));
        lines.Add($"Paper: {(paper == PaperWidth.Mm58 ? 58 : 80)} mm ({width} columns)");
        lines.Add(string.Concat(Enumerable.Range(1, width).Select(i => (char)('0' + i % 10))));
        lines.Add(at.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));
        return lines;
    }

    /// <summary>Title bold and centred, then the lines, feed, cut and a drawer kick.</summary>
    public static byte[] EscPosBytes(string printerName, PaperWidth paper, DateTime at)
    {
        var lines = Lines(printerName, paper, at);
        var printer = new EscPos().Init().Align(Alignment.Center).Bold(true).Line(lines[0]).Style(LineStyle.Normal);
        foreach (var line in lines.Skip(1)) printer.Line(line);
        return printer.Feed(3).Cut().KickDrawer().ToArray();
    }

    /// <summary>Hard split so no line is wider than the paper (printer names have no useful word breaks).</summary>
    private static IEnumerable<string> Chunk(string text, int width)
    {
        for (var i = 0; i < text.Length; i += width) yield return text.Substring(i, Math.Min(width, text.Length - i));
    }
}
