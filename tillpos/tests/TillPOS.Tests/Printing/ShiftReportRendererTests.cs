using System.Text;
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using TillPOS.Printing;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Printing;

public class ShiftReportRendererTests
{
    private static readonly ReceiptHeader Header = new("AL AIN MARKETING L.L.C", "Nuaimiya 1, Al Ain Market, Ajman, UAE", "100000000000003",
        "Till 1", "Thank you for shopping with us", "+971 6 000 0000");

    private static readonly ShiftOpening Opening = new("TILL1-SHIFT-20261007080000", "simran", "",
        new DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeSpan.FromHours(4)), [new ReceiptPayment("Cash Counter 2", 200m)]);

    private static ShiftClosing Closing(params ShiftModeSummary[] modes) => new("TILL1-SHIFT-20261007080000",
        new DateTimeOffset(2026, 10, 7, 16, 5, 0, TimeSpan.FromHours(4)),
        modes.Length > 0
            ? modes
            : [
                new ShiftModeSummary("Cash Counter 2", 200m, M("850.25"), M("850.00"), M("-0.25")),
                new ShiftModeSummary("Credit Card", 0m, M("584.25"), M("584.25"), 0m),
            ],
        42, 0, M("1234.50"), M("1175.71"), M("58.79"));

    private static List<PrintLine> Layout(PaperWidth paper, string? approvedBy = null, ShiftClosing? closing = null) =>
        ShiftReportRenderer.Layout(Opening, closing ?? Closing(), Header, "Test Cashier", approvedBy, paper).ToList();

    private static void AssertFits(IReadOnlyList<PrintLine> lines, PaperWidth paper)
    {
        var w = (int)paper;
        foreach (var line in lines)
            Assert.True(line.Style == LineStyle.Title ? line.Text.Length <= w / 2 : line.Text.Length <= w,
                $"Too wide for {w}: \"{line.Text}\"");
    }

    [Fact]
    public void The_80mm_report_has_the_header_totals_and_mode_rows()
    {
        var lines = Layout(PaperWidth.Mm80);
        var text = lines.Select(l => l.Text).ToList();

        Assert.Equal(new PrintLine("             AL AIN MARKETING L.L.C", LineStyle.Bold), lines[0]);
        Assert.Equal(new PrintLine("SHIFT REPORT (Z)", LineStyle.Title), lines[1]);
        Assert.Equal(
        [
            "Shift   : TILL1-SHIFT-20261007080000",
            "Till    : Till 1",
            "Cashier : Test Cashier",
            "Opened  : 07/10/2026 08:00",
            "Closed  : 07/10/2026 16:05",
            new string('-', 48),
            "Bills (sales)                                 42",
            "Total incl. VAT                          1234.50",
            "VAT                                        58.79",
            new string('-', 48),
            "Mode              Expected    Counted Difference",
            "Cash Counter 2      850.25     850.00      -0.25",
            "Credit Card         584.25     584.25       0.00",
            new string('-', 48),
            "Signature: ____________________",
        ], text.Skip(2));
        Assert.Equal(LineStyle.Bold, lines.Single(l => l.Text.StartsWith("Mode", StringComparison.Ordinal)).Style);
        Assert.DoesNotContain(text, t => t.Contains("approved", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(text, t => t.StartsWith("Returns", StringComparison.Ordinal));
        AssertFits(lines, PaperWidth.Mm80);
    }

    [Fact]
    public void The_58mm_report_puts_each_mode_on_two_lines()
    {
        var lines = Layout(PaperWidth.Mm58, "supervisor1");
        var text = lines.Select(l => l.Text).ToList();

        AssertFits(lines, PaperWidth.Mm58);
        var mode = text.IndexOf("Mode");
        Assert.True(mode > 0);
        Assert.Equal(
        [
            "Mode",
            "   Expected   Counted Difference",
            "Cash Counter 2",
            "     850.25    850.00      -0.25",
            "Credit Card",
            "     584.25    584.25       0.00",
        ], text.Skip(mode).Take(6));
        Assert.Contains("Total incl. VAT          1234.50", text);
        Assert.Contains("Bills (sales)                 42", text);
        Assert.Contains("Variance approved by:", text);
        Assert.Contains("supervisor1", text);
    }

    [Fact]
    public void The_approval_line_appears_only_with_an_approver()
    {
        var with = Layout(PaperWidth.Mm80, "supervisor1").Select(l => l.Text).ToList();
        Assert.Equal("Variance approved by: supervisor1", with[^2]);
        Assert.Equal("Signature: ____________________", with[^1]);

        Assert.DoesNotContain(Layout(PaperWidth.Mm80, null).Select(l => l.Text), t => t.Contains("approved", StringComparison.Ordinal));
        Assert.DoesNotContain(Layout(PaperWidth.Mm80, " ").Select(l => l.Text), t => t.Contains("approved", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PaperWidth.Mm80)]
    [InlineData(PaperWidth.Mm58)]
    public void The_first_count_difference_is_printed_only_when_given(PaperWidth paper)
    {
        var lines = ShiftReportRenderer.Layout(Opening, Closing(), Header, "Test Cashier", "supervisor1", paper, M("-10")).ToList();
        AssertFits(lines, paper);
        var text = lines.Select(l => l.Text).ToList();
        var first = text.IndexOf("First count difference: -10.00");
        Assert.True(first > 0);
        Assert.True(first < text.FindIndex(t => t.StartsWith("Variance approved by:", StringComparison.Ordinal)));

        Assert.DoesNotContain(Layout(paper, "supervisor1").Select(l => l.Text), t => t.StartsWith("First count", StringComparison.Ordinal));
        Assert.Contains("First count difference: 2.50",
            ShiftReportRenderer.TextLines(Opening, Closing(), Header, "Test Cashier", null, paper, M("2.5")));
    }

    [Theory]
    [InlineData(PaperWidth.Mm80)]
    [InlineData(PaperWidth.Mm58)]
    public void Long_names_huge_amounts_and_returns_still_fit(PaperWidth paper)
    {
        var closing = Closing(
            new ShiftModeSummary("Cash Counter 2 Main Entrance A", 0m, M("-1234567.125"), M("9999999.999"), M("11234567.124")),
            new ShiftModeSummary("Credit Card", 0m, M("584.25"), 0m, M("-584.25"))) with { Returns = 3 };
        var lines = Layout(paper, "a-supervisor-with-a-very-long-user-name@quickgroc.com", closing);
        var text = lines.Select(l => l.Text).ToList();

        AssertFits(lines, paper);
        Assert.Contains(text, t => t.Contains("9999999.999", StringComparison.Ordinal));
        Assert.Contains(text, t => t.Contains("11234567.124", StringComparison.Ordinal));
        Assert.Contains(text, t => t.Contains("-1234567.125", StringComparison.Ordinal));
        Assert.Contains(text, t => t.StartsWith("Returns", StringComparison.Ordinal) && t.EndsWith('3'));
        Assert.Contains(text, t => t.Contains("Main Entrance", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_lines_centre_the_title_and_bytes_never_kick_the_drawer()
    {
        var text = ShiftReportRenderer.TextLines(Opening, Closing(), Header, "Test Cashier", null, PaperWidth.Mm80);
        Assert.Equal("                SHIFT REPORT (Z)", text[1]);

        var bytes = ShiftReportRenderer.EscPosBytes(Opening, Closing(), Header, "Test Cashier", "supervisor1", PaperWidth.Mm80);
        Assert.DoesNotContain(bytes.Zip(bytes.Skip(1)), p => p.First == 0x1B && p.Second == 0x70);      // ESC p: drawer kick
        Assert.Equal(new byte[] { 0x1D, 0x56, 0x42, 0x00 }, bytes[^4..]);           // ends with the cut
        Assert.Contains("Cash Counter 2      850.25", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
    }
}
