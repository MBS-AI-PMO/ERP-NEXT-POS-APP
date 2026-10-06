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
        var pos = store.LoadPosSettings() ?? throw new InvalidOperationException("POS settings are not downloaded yet.");
        var header = new ReceiptHeader(pos.CompanyName, pos.AddressText, pos.TaxId, $"Till {settings.TillNumber}", settings.ReceiptFooter);
        if (string.IsNullOrWhiteSpace(settings.PrinterName))
        {
            var folder = Path.Combine(Path.GetDirectoryName(settings.DbPath)!, "receipts");
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, receipt.ClientId + ".txt"), ReceiptRenderer.TextLines(receipt, header, settings.PaperWidth));
            return;
        }
        RawPrinter.Send(settings.PrinterName, ReceiptRenderer.EscPosBytes(receipt, header, settings.PaperWidth, openDrawer));
    }
}
