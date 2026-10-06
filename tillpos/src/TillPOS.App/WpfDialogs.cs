using System.Windows;
using TillPOS.App.Dialogs;
using TillPOS.Core.Sales;
using TillPOS.Presentation;
using TillPOS.Printing;

namespace TillPOS.App;

/// <param name="owner">The main window the dialogs are centred on.</param>
/// <param name="receiptLayout">The header and paper width the receipts are printed with (for the invoice popup).</param>
public sealed class WpfDialogs(Window owner, Func<(ReceiptHeader Header, PaperWidth Paper)> receiptLayout) : IDialogs
{
    public Task<string?> AskPinAsync(string title, string reason)
    {
        var dialog = new PinDialog(title, reason) { Owner = owner };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Pin : null);
    }

    public Task<decimal?> AskNumberAsync(string title, string prompt)
    {
        var dialog = new NumberDialog(title, prompt) { Owner = owner };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Value : null);
    }

    public void Info(string message) => MessageBox.Show(owner, message, "TillPOS");

    public string? ShowReceipt(Receipt receipt, string? printError, Func<string?> reprint)
    {
        var (header, paper) = receiptLayout();
        var dialog = new ReceiptDialog(ReceiptRenderer.Layout(receipt, header, paper), header.Trn is not null, printError, reprint) { Owner = owner };
        dialog.ShowDialog();
        return dialog.ScannedCode;
    }
}
