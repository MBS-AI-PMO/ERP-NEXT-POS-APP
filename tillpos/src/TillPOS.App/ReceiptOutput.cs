using System.IO;
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using TillPOS.Data;
using TillPOS.Presentation;
using TillPOS.Printing;

namespace TillPOS.App;

/// <summary>Prints to the configured thermal printer; with no printer configured (testing) the receipt text is written
/// to a file next to the database instead.</summary>
public sealed class ReceiptOutput(TillSettings settings, CatalogStore store) : IReceiptOutput
{
    /// <summary>A copy written to a file goes next to the original as "{id}-COPY.txt", so the original file is kept.</summary>
    public void Print(Receipt receipt, bool openDrawer, bool copy = false)
    {
        var header = Header();
        if (string.IsNullOrWhiteSpace(settings.PrinterName))
        {
            File.WriteAllLines(Path.Combine(ReceiptsFolder(), receipt.ClientId + (copy ? "-COPY.txt" : ".txt")),
                ReceiptRenderer.TextLines(receipt, header, settings.PaperWidth, copy));
            return;
        }
        RawPrinter.Send(settings.PrinterName, ReceiptRenderer.EscPosBytes(receipt, header, settings.PaperWidth, openDrawer, copy));
    }

    /// <summary>The shift (Z) report; with no printer it is written to "SHIFT-{shift id}.txt" in the receipts folder.</summary>
    public void PrintShiftReport(ShiftOpening opening, ShiftClosing closing, string cashierName, string? approvedBy,
        decimal? firstCountDifference)
    {
        var header = Header();
        if (string.IsNullOrWhiteSpace(settings.PrinterName))
        {
            File.WriteAllLines(Path.Combine(ReceiptsFolder(), $"SHIFT-{opening.ClientId}.txt"),
                ShiftReportRenderer.TextLines(opening, closing, header, cashierName, approvedBy, settings.PaperWidth,
                    firstCountDifference));
            return;
        }
        RawPrinter.Send(settings.PrinterName,
            ShiftReportRenderer.EscPosBytes(opening, closing, header, cashierName, approvedBy, settings.PaperWidth, firstCountDifference));
    }

    private string ReceiptsFolder()
    {
        var folder = Path.Combine(Path.GetDirectoryName(settings.DbPath)!, "receipts");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>The receipt header printed on every bill (also used by the on-screen invoice).</summary>
    public ReceiptHeader Header()
    {
        var pos = store.LoadPosSettings() ?? throw new InvalidOperationException("POS settings are not downloaded yet.");
        var cardModes = settings.EffectiveCounters().Select(c => c.CardMode).Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        return new ReceiptHeader(pos.CompanyName, FirstNonBlank(pos.AddressText, settings.ShopAddress), FirstNonBlank(pos.TaxId, settings.Trn),
            $"Till {settings.TillNumber}", settings.ReceiptFooter, FirstNonBlank(null, settings.ShopPhone), SampleQr: settings.SampleQr,
            CardModes: cardModes.Count > 0 ? cardModes : null);
    }

    /// <summary>ERPNext's value, else the settings fallback; blank counts as missing (a blank TRN prints no TRN line and no real
    /// QR; with SampleQr set it prints the marked sample QR instead).</summary>
    private static string? FirstNonBlank(string? erp, string? fallback) =>
        !string.IsNullOrWhiteSpace(erp) ? erp : !string.IsNullOrWhiteSpace(fallback) ? fallback : null;
}
