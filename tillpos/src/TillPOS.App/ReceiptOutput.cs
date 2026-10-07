using System.IO;
using TillPOS.Core.Sales;
using TillPOS.Data;
using TillPOS.Presentation;
using TillPOS.Printing;

namespace TillPOS.App;

/// <summary>Prints to the configured thermal printer; with no printer configured (testing) the receipt text is written
/// to a file next to the database instead.</summary>
public sealed class ReceiptOutput(TillSettings settings, CatalogStore store) : IReceiptOutput
{
    public void Print(Receipt receipt, bool openDrawer)
    {
        var header = Header();
        if (string.IsNullOrWhiteSpace(settings.PrinterName))
        {
            var folder = Path.Combine(Path.GetDirectoryName(settings.DbPath)!, "receipts");
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, receipt.ClientId + ".txt"), ReceiptRenderer.TextLines(receipt, header, settings.PaperWidth));
            return;
        }
        RawPrinter.Send(settings.PrinterName, ReceiptRenderer.EscPosBytes(receipt, header, settings.PaperWidth, openDrawer));
    }

    /// <summary>The receipt header printed on every bill (also used by the on-screen invoice).</summary>
    public ReceiptHeader Header()
    {
        var pos = store.LoadPosSettings() ?? throw new InvalidOperationException("POS settings are not downloaded yet.");
        return new ReceiptHeader(pos.CompanyName, FirstNonBlank(pos.AddressText, settings.ShopAddress), FirstNonBlank(pos.TaxId, settings.Trn),
            $"Till {settings.TillNumber}", settings.ReceiptFooter, FirstNonBlank(null, settings.ShopPhone), SampleQr: settings.SampleQr);
    }

    /// <summary>ERPNext's value, else the settings fallback; blank counts as missing (a blank TRN prints no TRN line and no real
    /// QR; with SampleQr set it prints the marked sample QR instead).</summary>
    private static string? FirstNonBlank(string? erp, string? fallback) =>
        !string.IsNullOrWhiteSpace(erp) ? erp : !string.IsNullOrWhiteSpace(fallback) ? fallback : null;
}
