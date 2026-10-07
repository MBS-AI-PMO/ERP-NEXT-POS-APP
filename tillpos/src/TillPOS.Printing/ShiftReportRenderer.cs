using System.Globalization;
using TillPOS.Core.Shifts;
using static TillPOS.Printing.ReceiptRenderer;

namespace TillPOS.Printing;

/// <summary>The shift (Z) report printed when a shift closes: bills, totals, VAT and expected / counted / difference per
/// payment mode. Printed through the same styled-line pipeline as receipts, never with a drawer kick.</summary>
public static class ShiftReportRenderer
{
    private const int LabelWidth = 8;    // "Cashier "
    private const string Signature = "Signature: ____________________";

    /// <param name="firstCountDifference">The first count's cash difference when a recount changed it; printed when given.</param>
    public static IReadOnlyList<PrintLine> Layout(ShiftOpening opening, ShiftClosing closing, ReceiptHeader h, string cashierName,
        string? approvedBy, PaperWidth paper, decimal? firstCountDifference = null)
    {
        var w = (int)paper;
        var lines = new List<PrintLine>();
        void Add(string text, LineStyle style = LineStyle.Normal) => lines.Add(new PrintLine(text, style));
        void Rule() => Add(new string('-', w));

        foreach (var part in Wrap(h.CompanyName, w)) Add(Center(part, w), LineStyle.Bold);
        foreach (var part in Wrap("SHIFT REPORT (Z)", w / 2)) Add(part, LineStyle.Title);
        foreach (var text in Field("Shift", opening.ClientId, w, LabelWidth)) Add(text);
        foreach (var text in Field("Till", h.TillName, w, LabelWidth)) Add(text);
        foreach (var text in Field("Cashier", cashierName, w, LabelWidth)) Add(text);
        foreach (var text in Field("Opened", Time(opening.OpenedAt), w, LabelWidth)) Add(text);
        foreach (var text in Field("Closed", Time(closing.ClosedAt), w, LabelWidth)) Add(text);
        Rule();

        Add(Pair("Bills (sales)", closing.Sales.ToString(CultureInfo.InvariantCulture), w));
        if (closing.Returns > 0) Add(Pair("Returns", closing.Returns.ToString(CultureInfo.InvariantCulture), w));
        Add(Pair("Total incl. VAT", Money(closing.GrandTotal), w));
        Add(Pair("VAT", Money(closing.TotalTaxes), w));
        Rule();

        var table = ModeTable.For(paper);
        foreach (var text in table.Header()) Add(text, LineStyle.Bold);
        foreach (var mode in closing.Modes)
            foreach (var text in table.Rows(mode)) Add(text);
        Rule();

        if (firstCountDifference is { } first)
            foreach (var part in Wrap("First count difference: " + Money(first), w)) Add(part);
        if (!string.IsNullOrWhiteSpace(approvedBy))
            foreach (var part in Wrap("Variance approved by: " + approvedBy, w)) Add(part);
        Add(Fit(Signature, w));
        return lines;
    }

    /// <summary>Plain text (the report file when no printer is configured).</summary>
    public static IReadOnlyList<string> TextLines(ShiftOpening opening, ShiftClosing closing, ReceiptHeader h, string cashierName,
        string? approvedBy, PaperWidth paper, decimal? firstCountDifference = null) =>
        PlainText(Layout(opening, closing, h, cashierName, approvedBy, paper, firstCountDifference), paper);

    /// <summary>ESC/POS bytes for the thermal printer; the drawer is never opened.</summary>
    public static byte[] EscPosBytes(ShiftOpening opening, ShiftClosing closing, ReceiptHeader h, string cashierName, string? approvedBy,
        PaperWidth paper, decimal? firstCountDifference = null) =>
        StyledBytes(Layout(opening, closing, h, cashierName, approvedBy, paper, firstCountDifference), openDrawer: false);

    private static string Time(DateTimeOffset at) => at.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Mode / Expected / Counted / Difference. At 80 mm a mode is one row (name, then the three amounts); at 58 mm the
    /// name has its own line and the amounts follow, indented. A name too long for its column also gets its own line(s).
    /// Every amount is right-aligned in its cell; if an amount is too wide for the row, the mode falls back to one
    /// "label  amount" line per value, so nothing is cut and nothing passes the paper edge.</summary>
    private sealed record ModeTable(int Width, int NameWidth, int ExpectedWidth, int CountedWidth, int DifferenceWidth)
    {
        // 80 mm: name 15 + 11 + 11 + 11 = 48. 58 mm: indent 2 + 9 + 10 + 11 = 32 (the name is on its own line).
        public static ModeTable For(PaperWidth paper) =>
            paper == PaperWidth.Mm58 ? new(32, 0, 9, 10, 11) : new((int)paper, 15, 11, 11, 11);

        private bool NameOnOwnLine => NameWidth == 0;
        private int Indent => NameOnOwnLine ? Width - ExpectedWidth - CountedWidth - DifferenceWidth : NameWidth;

        public IEnumerable<string> Header()
        {
            if (NameOnOwnLine) yield return "Mode";
            yield return (NameOnOwnLine ? "" : "Mode").PadRight(Indent) + Amounts("Expected", "Counted", "Difference");
        }

        public IEnumerable<string> Rows(ShiftModeSummary mode)
        {
            var amounts = Amounts(Money(mode.Expected), Money(mode.Counted), Money(mode.Difference));
            if (Indent + amounts.Length > Width)
            {
                foreach (var part in Wrap(mode.ModeOfPayment, Width)) yield return part;
                yield return Pair("  Expected", Money(mode.Expected), Width);
                yield return Pair("  Counted", Money(mode.Counted), Width);
                yield return Pair("  Difference", Money(mode.Difference), Width);
                yield break;
            }
            if (!NameOnOwnLine && mode.ModeOfPayment.Length < NameWidth)
            {
                yield return mode.ModeOfPayment.PadRight(Indent) + amounts;
                yield break;
            }
            foreach (var part in Wrap(mode.ModeOfPayment, Width)) yield return part;
            yield return new string(' ', Indent) + amounts;
        }

        private string Amounts(string expected, string counted, string difference) =>
            Cell(expected, ExpectedWidth) + Cell(counted, CountedWidth) + Cell(difference, DifferenceWidth);

        /// <summary>Right-aligned with at least one leading space (so neighbouring cells never touch).</summary>
        private static string Cell(string text, int width) => text.Length < width ? text.PadLeft(width) : " " + text;
    }
}
