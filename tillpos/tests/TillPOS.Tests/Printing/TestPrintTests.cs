using System.Text;
using TillPOS.Printing;

namespace TillPOS.Tests.Printing;

public class TestPrintTests
{
    private static readonly DateTime At = new(2026, 10, 7, 13, 45, 0);

    [Fact]
    public void Lines_name_the_printer_paper_and_date_with_a_full_width_ruler()
    {
        var lines = TestPrint.Lines("EPSON TM-T20III", PaperWidth.Mm80, At);

        Assert.Equal("TillPOS test print", lines[0]);
        Assert.Contains("Printer: EPSON TM-T20III", lines);
        Assert.Contains("Paper: 80 mm (48 columns)", lines);
        Assert.Contains("123456789012345678901234567890123456789012345678", lines);
        Assert.Contains("07/10/2026 13:45", lines);
    }

    [Fact]
    public void At_58_mm_the_ruler_is_32_columns_and_a_long_printer_name_wraps_inside_the_paper()
    {
        var lines = TestPrint.Lines("POS-58 Thermal Receipt Printer on USB001 (copy 2)", PaperWidth.Mm58, At);

        Assert.Contains("12345678901234567890123456789012", lines);
        Assert.Contains("Paper: 58 mm (32 columns)", lines);
        Assert.All(lines, l => Assert.True(l.Length <= 32, $"'{l}' is wider than the paper"));
        Assert.Contains("USB001", string.Join(" ", lines));
    }

    [Fact]
    public void Bytes_print_a_bold_centred_title_then_feed_cut_and_kick_the_drawer()
    {
        var bytes = TestPrint.EscPosBytes("EPSON", PaperWidth.Mm80, At);

        Assert.Equal(new byte[] { 0x1B, 0x40 }, bytes[..2]);
        var title = new EscPos().Align(Alignment.Center).Bold(true).Line("TillPOS test print").ToArray();
        Assert.True(EscPosTests.Contains(bytes, title));
        Assert.True(EscPosTests.Contains(bytes, Encoding.ASCII.GetBytes("123456789012345678901234567890123456789012345678\n")));
        var end = new EscPos().Cut().KickDrawer().ToArray();
        Assert.Equal(end, bytes[^end.Length..]);
    }
}
