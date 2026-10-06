using System.Globalization;
using TillPOS.Core.Sales;

namespace TillPOS.Printing;

public enum PaperWidth { Mm80 = 48, Mm58 = 32 }

public sealed record ReceiptHeader(string CompanyName, string? Address, string? Trn, string TillName, string? Footer, string? Phone = null);

/// <summary>UAE simplified tax invoice / tax credit note layout (spec §9), as plain text lines and as ESC/POS bytes.</summary>
public static class ReceiptRenderer
{
    public static IReadOnlyList<string> TextLines(Receipt r, ReceiptHeader h, PaperWidth paper)
    {
        var w = (int)paper;
        var lines = new List<string>();
        lines.AddRange(Wrap(h.CompanyName, w).Select(x => Center(x, w)));
        if (h.Address is { } address) lines.AddRange(Wrap(address, w).Select(x => Center(x, w)));
        if (h.Phone is { } phone) lines.Add(Center("Tel: " + phone, w));
        if (h.Trn is { } trn) lines.Add(Center("TRN: " + trn, w));
        lines.Add(Center(r.Kind == ReceiptKind.Return ? "TAX CREDIT NOTE" : "TAX INVOICE", w));
        lines.Add(Pair("No.", r.ClientId, w));
        if (r.ReturnAgainst is { } original) lines.Add(Pair("Return of", original, w));
        lines.Add(Pair(r.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), h.TillName, w));
        lines.Add(Pair("Cashier", r.Cashier, w));
        lines.Add(new string('-', w));
        foreach (var line in r.Lines)
        {
            lines.AddRange(Wrap(line.ItemName, w));
            lines.Add(Pair($"  {Qty(line)} x {Money(line.Rate)}", Money(line.Amount), w));
        }
        lines.Add(new string('-', w));
        lines.Add(Pair("Total incl. VAT", Money(r.GrandTotal), w));
        lines.Add(Pair("VAT included", Money(r.TotalTaxes), w));
        // Cash bills use ERPNext's rounded total; split bills round only the cash part. Either way show what was
        // actually due, so the payment lines minus change add up on paper.
        var rounding = r.UsesErpRoundedTotal ? r.RoundingAdjustment : r.RoundingDifference;
        var amountDue = r.UsesErpRoundedTotal ? r.RoundedTotal : r.GrandTotal + r.RoundingDifference;
        if (amountDue != r.GrandTotal)
        {
            lines.Add(Pair("Rounding", Money(rounding), w));
            lines.Add(Pair("Amount due", Money(amountDue), w));
        }
        foreach (var payment in r.Payments) lines.Add(Pair(payment.ModeOfPayment, Money(payment.Amount), w));
        if (r.Change != 0m) lines.Add(Pair("Change", Money(r.Change), w));
        lines.Add(new string('-', w));
        if (h.Footer is { } footer) lines.AddRange(Wrap(footer, w).Select(x => Center(x, w)));
        return lines;
    }

    public static byte[] EscPosBytes(Receipt r, ReceiptHeader h, PaperWidth paper, bool openDrawer)
    {
        var printer = new EscPos().Init().Align(Alignment.Left);
        foreach (var line in TextLines(r, h, paper)) printer.Line(line);
        if (h.Trn is { } trn)
        {
            // Field 4 is the VAT-inclusive invoice value (consistent with field 5); cash rounding is a payment adjustment.
            printer.Align(Alignment.Center).Qr(FtaQr.Encode(h.CompanyName, trn, r.CreatedAt, r.GrandTotal, r.TotalTaxes)).Align(Alignment.Left);
        }
        printer.Feed(3).Cut();
        if (openDrawer) printer.KickDrawer();
        return printer.ToArray();
    }

    private static string Money(decimal value) => value.ToString("0.00#", CultureInfo.InvariantCulture);

    private static string Qty(ReceiptLine line) =>
        line.FromScaleLabel || line.Qty != decimal.Truncate(line.Qty)
            ? $"{line.Qty.ToString("0.000", CultureInfo.InvariantCulture)} {line.Uom}"
            : line.Qty.ToString("0", CultureInfo.InvariantCulture);

    private static string Pair(string left, string right, int width)
    {
        if (right.Length >= width) return right[..width];
        var room = width - right.Length - 1;
        if (left.Length > room) left = left[..room];
        return left + new string(' ', width - left.Length - right.Length) + right;
    }

    private static string Center(string text, int width) =>
        text.Length >= width ? text[..width] : new string(' ', (width - text.Length) / 2) + text;

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var piece = word.Length > width ? word[..width] : word;
            if (line.Length == 0) line = piece;
            else if (line.Length + 1 + piece.Length <= width) line += " " + piece;
            else { yield return line; line = piece; }
        }
        if (line.Length > 0) yield return line;
    }
}
