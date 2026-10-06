using System.Windows;
using TillPOS.App.Dialogs;
using TillPOS.Presentation;

namespace TillPOS.App;

public sealed class WpfDialogs(Window owner) : IDialogs
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
}
