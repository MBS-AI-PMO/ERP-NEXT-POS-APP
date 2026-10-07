using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;

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
        SettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);
        ShiftInfo = OpenShiftInfo();
    }

    /// <summary>"Counter 2 · shift open since 08:00" when a shift is open on this till (logging in joins it, at its counter),
    /// otherwise "".</summary>
    public string ShiftInfo { get; }

    /// <summary>Till setup (printer, paper, till number), supervisor only. Nobody is logged in here, so the approval is
    /// logged without a cashier or shift.</summary>
    public AsyncRelayCommand SettingsCommand { get; }

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

    private async Task OpenSettingsAsync()
    {
        Pin = "";
        var gate = new SupervisorGate(ctx, new SessionState());
        if (await gate.ApproveAsync(ApprovalAction.SettingsChange, "Change till settings") is not { } supervisor) return;
        // The upload mode decides whether the till writes to ERPNext: its change is logged with the approving supervisor.
        ctx.Dialogs.ShowSetup(change => ctx.Approvals.Add(new ApprovalRecord(Guid.NewGuid().ToString("N"), ApprovalAction.UploadModeChange,
            "", supervisor, "", null, null, 0m, change, ctx.Clock.Now)));
    }

    private string OpenShiftInfo()
    {
        try
        {
            if (ctx.Shifts.Current() is not { } shift) return "";
            var since = shift.OpenedAt.ToString("HH:mm", CultureInfo.InvariantCulture);
            return $"{CounterSettings.ForShift(ctx.Counters, shift).DisplayName} · shift open since {since}";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>A shift opened before counters existed (blank Counter) is saved once with the counter it belongs to (the
    /// default counter, with the cash mode its float was counted in), so it no longer depends on the settings. A failure only
    /// means the shift keeps working it out each time.</summary>
    private ShiftOpening? BackfillCounter(ShiftOpening? shift)
    {
        if (shift is null || !string.IsNullOrWhiteSpace(shift.Counter)) return shift;
        var counter = CounterSettings.ForShift(ctx.Counters, shift);
        var filled = shift with
        {
            Counter = counter.PosProfile,
            CounterName = counter.DisplayName,
            CashMode = counter.CashMode,
            CardMode = counter.CardMode,
        };
        try
        {
            return ctx.Shifts.UpdateOpening(filled) ? filled : shift;
        }
        catch (Exception)
        {
            return shift;
        }
    }

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
        session.Shift = BackfillCounter(ctx.Shifts.Current());
        session.Counter = session.Shift is null ? null : ctx.CounterOf(session);
        ctx.Navigator.Show(session.Shift is null ? new OpenShiftViewModel(ctx, session, newSale) : newSale());
    }
}
