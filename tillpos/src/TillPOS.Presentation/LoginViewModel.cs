using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TillPOS.Presentation;

public sealed class LoginViewModel : ObservableObject
{
    private readonly TillContext ctx;
    private readonly SessionState session;
    private readonly Func<object> newSale;
    private string pin = "";
    private string message = "";

    public LoginViewModel(TillContext ctx, SessionState session, Func<object> newSale)
    {
        this.ctx = ctx;
        this.session = session;
        this.newSale = newSale;
        DigitCommand = new RelayCommand<string>(d => { if (d is { Length: 1 } && char.IsAsciiDigit(d[0]) && Pin.Length < 6) Pin += d; });
        BackspaceCommand = new RelayCommand(() => { if (Pin.Length > 0) Pin = Pin[..^1]; });
        ClearCommand = new RelayCommand(() => Pin = "");
        LoginCommand = new RelayCommand(Login);
    }

    public string Pin
    {
        get => pin;
        set { if (SetProperty(ref pin, value)) OnPropertyChanged(nameof(PinMask)); }
    }

    public string PinMask => new('●', pin.Length);
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public RelayCommand<string> DigitCommand { get; }
    public RelayCommand BackspaceCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand LoginCommand { get; }

    public void Login()
    {
        var typed = Pin;
        Pin = "";
        if (ctx.LoginLimiter.IsLocked)
        {
            Message = $"Too many wrong PINs — wait {Math.Ceiling(ctx.LoginLimiter.Remaining.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s.";
            return;
        }

        var cashier = ctx.Authenticator.Login(typed);
        if (cashier is null)
        {
            ctx.LoginLimiter.Failed();
            Message = "Wrong PIN.";
            return;
        }

        ctx.LoginLimiter.Succeeded();
        Message = "";
        session.Cashier = cashier;
        session.Shift = ctx.Shifts.Current();
        ctx.Navigator.Show(session.Shift is null ? new OpenShiftViewModel(ctx, session, newSale) : newSale());
    }
}
