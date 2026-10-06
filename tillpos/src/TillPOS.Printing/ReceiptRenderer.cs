using System.Globalization;
using TillPOS.Core.Sales;

namespace TillPOS.Printing;

public enum PaperWidth { Mm80 = 48, Mm58 = 32 }

public sealed record ReceiptHeader(string CompanyName, string? Address, string? Trn, string TillName, string? Footer, string? Phone = null);

/// <summary>How a receipt line is printed. Title = double width + double height + bold + centred (its text is unpadded and
/// at most half the paper columns); Big = double height + bold (full width); Bold = bold only.</summary>
public enum LineStyle { Normal, Bold, Title, Big }

/// <summary>One receipt line. Except for Title lines, the text is already padded for the paper width.</summary>
public sealed record PrintLine(string Text, LineStyle Style = LineStyle.Normal);

/// <summary>UAE simplified tax invoice / tax credit note layout (spec §9), as styled lines, plain text and ESC/POS bytes.</summary>
public static class ReceiptRenderer
{
    private const int LabelWidth = 10;   // "Invoice No"

    public static IReadOnlyList<PrintLine> Layout(Receipt r, ReceiptHeader h, PaperWidth paper)
    {
        var w = (int)paper;
        var lines = new List<PrintLine>();
        void Add(string text, LineStyle style = LineStyle.Normal) => lines.Add(new PrintLine(text, style));
        void Centered(string text, LineStyle style = LineStyle.Normal)
        {
            foreach (var part in Wrap(text, w)) Add(Center(part, w), style);
        }
        void Rule(char c) => Add(new string(c, w));

        Centered(h.CompanyName, LineStyle.Bold);
        if (h.Address is { } address) Centered(address);
        if (h.Phone is { } phone) Centered("Tel: " + phone);
        if (h.Trn is { } trn) Centered("TRN: " + trn);
        Rule('=');
        var isReturn = r.Kind == ReceiptKind.Return;
        foreach (var part in Wrap(isReturn ? "CREDIT NOTE" : "TAX INVOICE", w / 2)) Add(part, LineStyle.Title);
        if (isReturn) Centered("Tax Credit Note");
        Rule('=');

        foreach (var text in Field("Invoice No", r.ClientId, w)) Add(text);
        if (r.ReturnAgainst is { } original) foreach (var text in Field("Return of", original, w)) Add(text);
        foreach (var text in Field("Date", r.CreatedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), w)) Add(text);
        foreach (var text in Field("Cashier", r.CashierName ?? r.Cashier, w)) Add(text);
        foreach (var text in Field("Till", h.TillName, w)) Add(text);
        Rule('-');

        var columns = Columns.For(paper);
        Add(columns.Header(), LineStyle.Bold);
        Rule('-');
        foreach (var line in r.Lines)
        {
            foreach (var part in Wrap(line.ItemName, w)) Add(part);
            Add(columns.ShowsPrice
                ? columns.Row(Qty(line), Money(line.Rate), Money(line.Amount))
                : columns.Row($"{Qty(line)} x {Money(line.Rate)}", null, Money(line.Amount)));
            if (line.Rate < line.PriceListRate) Add(Fit($"  Offer: {Money(-Saved(line))}", w));
        }
        Rule('-');

        Add("Items: " + r.Lines.Count.ToString(CultureInfo.InvariantCulture));
        Add(Pair("Total excl. VAT", Money(r.NetTotal), w));
        Add(Pair(VatLabel(r), Money(r.TotalTaxes), w));
        Add(Pair("TOTAL AED", Money(r.GrandTotal), w), LineStyle.Big);
        // Cash bills use ERPNext's rounded total; split bills round only the cash part. Either way show what was
        // actually due, so the payment lines minus change add up on paper.
        var rounding = r.UsesErpRoundedTotal ? r.RoundingAdjustment : r.RoundingDifference;
        var amountDue = r.UsesErpRoundedTotal ? r.RoundedTotal : r.GrandTotal + r.RoundingDifference;
        if (amountDue != r.GrandTotal)
        {
            Add(Pair("Rounding", Money(rounding), w));
            Add(Pair("AMOUNT DUE", Money(amountDue), w), LineStyle.Big);
        }
        Rule('-');

        foreach (var payment in r.Payments) Add(Pair(payment.ModeOfPayment, Money(payment.Amount), w));
        if (r.Change != 0m) Add(Pair("Change", Money(r.Change), w));
        var saved = decimal.Round(r.Lines.Where(l => l.Rate < l.PriceListRate).Sum(Saved), 3);
        if (saved > 0m) Add(Pair("You saved", Money(saved), w));
        Rule('-');

        if (h.Footer is { } footer) Centered(footer);
        Centered("Prices include 5% VAT");
        return lines;
    }

    public static IReadOnlyList<string> TextLines(Receipt r, ReceiptHeader h, PaperWidth paper) =>
        Layout(r, h, paper).Select(l => l.Text).ToList();

    public static byte[] EscPosBytes(Receipt r, ReceiptHeader h, PaperWidth paper, bool openDrawer)
    {
        var printer = new EscPos().Init().Style(LineStyle.Normal);
        foreach (var line in Layout(r, h, paper))
        {
            if (line.Style == LineStyle.Normal) printer.Line(line.Text);
            else printer.Style(line.Style).Line(line.Text).Style(LineStyle.Normal);
        }
        if (h.Trn is { } trn)
        {
            // Field 4 is the VAT-inclusive invoice value (consistent with field 5); cash rounding is a payment adjustment.
            printer.Align(Alignment.Center).Qr(FtaQr.Encode(h.CompanyName, trn, r.CreatedAt, r.GrandTotal, r.TotalTaxes));
        }
        printer.Style(LineStyle.Normal).Feed(3).Cut();
        if (openDrawer) printer.KickDrawer();
        return printer.ToArray();
    }

    /// <summary>Item value row columns. Every cell is right-aligned to a fixed right edge, so a long quantity grows to the left
    /// (into the indent) and never pushes Price or Amount out of line.</summary>
    private sealed record Columns(int Width, int PriceWidth, int AmountWidth)
    {
        public bool ShowsPrice => PriceWidth > 0;

        // 80 mm: Qty ends at column 28, then Price 10 and Amount 10. 58 mm: Qty ends at 23 ("2 x 6.79"), Amount 9, no Price.
        public static Columns For(PaperWidth paper) => paper == PaperWidth.Mm58 ? new(32, 0, 9) : new((int)paper, 10, 10);

        public string Header()
        {
            var row = ShowsPrice ? Row("Qty", "Price", "Amount") : Row("Qty x Price", null, "Amount");
            return "Item" + row[4..];
        }

        public string Row(string qty, string? price, string amount)
        {
            var right = Cell(price ?? "", PriceWidth) + Cell(amount, AmountWidth);
            var qtyRoom = Width - right.Length;
            if (qtyRoom < 2) return right[^Width..];                       // absurd amounts: keep the amount's right edge
            return Cell(qty.Length < qtyRoom ? qty : qty[..(qtyRoom - 1)], qtyRoom) + right;
        }

        /// <summary>Right-aligned with at least one leading space (so neighbouring cells never touch).</summary>
        private static string Cell(string text, int width) =>
            width == 0 ? "" : text.Length < width ? text.PadLeft(width) : " " + text;
    }

    private static decimal Saved(ReceiptLine line) => decimal.Round((line.PriceListRate - line.Rate) * line.Qty, 3);

    /// <summary>"VAT 5%" when the bill's tax is one rate (TotalTaxes / NetTotal, whole percent), otherwise "VAT".</summary>
    private static string VatLabel(Receipt r)
    {
        if (r.NetTotal == 0m || r.Lines.Select(l => l.ItemTaxTemplate).Distinct().Count() > 1) return "VAT";
        var rate = decimal.Round(r.TotalTaxes / r.NetTotal * 100m, 0, MidpointRounding.AwayFromZero);
        return rate > 0m ? $"VAT {rate.ToString("0", CultureInfo.InvariantCulture)}%" : "VAT";
    }

    private static string Money(decimal value) => value.ToString("0.00#", CultureInfo.InvariantCulture);

    private static string Qty(ReceiptLine line) =>
        line.FromScaleLabel || line.Qty != decimal.Truncate(line.Qty)
            ? $"{line.Qty.ToString("0.000", CultureInfo.InvariantCulture)} {line.Uom}"
            : line.Qty.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>"Label     : value". The value is never cut: if it does not fit beside the label it goes on the next lines.</summary>
    private static IEnumerable<string> Field(string label, string value, int width)
    {
        var prefix = label.PadRight(LabelWidth) + ": ";
        if (prefix.Length + value.Length <= width)
        {
            yield return prefix + value;
            yield break;
        }
        yield return label + ":";
        foreach (var part in Wrap(value, width - 2)) yield return "  " + part;
    }

    private static string Pair(string left, string right, int width)
    {
        if (right.Length >= width) return right[..width];
        var room = width - right.Length - 1;
        if (left.Length > room) left = left[..room];
        return left + new string(' ', width - left.Length - right.Length) + right;
    }

    private static string Fit(string text, int width) => text.Length > width ? text[..width] : text;

    private static string Center(string text, int width) =>
        text.Length >= width ? text[..width] : new string(' ', (width - text.Length) / 2) + text;

    /// <summary>Word wrap; a word longer than the width is split rather than cut, so no text is lost.</summary>
    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var rest = word;
            while (rest.Length > width)
            {
                if (line.Length > 0) { yield return line; line = ""; }
                yield return rest[..width];
                rest = rest[width..];
            }
            if (rest.Length == 0) continue;
            if (line.Length == 0) line = rest;
            else if (line.Length + 1 + rest.Length <= width) line += " " + rest;
            else { yield return line; line = rest; }
        }
        if (line.Length > 0) yield return line;
    }
}
