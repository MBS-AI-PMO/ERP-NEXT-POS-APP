using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;

namespace TillPOS.Presentation;

/// <summary>Opening float → POS Opening Shift (uploaded in Plan 2b).</summary>
public sealed class OpenShiftViewModel : ObservableObject
{
    private readonly TillContext ctx;
    private readonly SessionState session;
    private readonly Func<object> newSale;
    private string message = "";

    public OpenShiftViewModel(TillContext ctx, SessionState session, Func<object> newSale)
    {
        this.ctx = ctx;
        this.session = session;
        this.newSale = newSale;
        OpenCommand = new RelayCommand(Open);
        KeyCommand = new RelayCommand<string>(key =>
        {
            if (key == ".") OpeningCash.Dot();
            else if (key == "⌫") OpeningCash.Backspace();
            else if (key is { Length: 1 }) OpeningCash.Digit(key[0]);
        });
    }

    public NumericEntry OpeningCash { get; } = new();
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public RelayCommand OpenCommand { get; }
    public RelayCommand<string> KeyCommand { get; }

    public void Open()
    {
        if (OpeningCash.Value is not { } amount || amount < 0m)
        {
            Message = "Enter the cash in the drawer.";
            return;
        }
        var now = ctx.Clock.Now;
        var shift = new ShiftOpening(ClientIds.Shift(ctx.TillNumber, now), session.Cashier!.Id, now,
            [new ReceiptPayment(ctx.Modes.Cash, amount)]);
        ctx.Shifts.Open(shift);
        session.Shift = shift;
        ctx.Navigator.Show(newSale());
    }
}
