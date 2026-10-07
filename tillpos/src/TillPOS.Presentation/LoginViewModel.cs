using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;
using TillPOS.Sync.Upload;

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
        UploadProblemsCommand = new AsyncRelayCommand(OpenUploadProblemsAsync);
        (UploadProblemsCount, HandledUploads) = UploadProblemsViewModel.Counts(ctx);
        ShiftInfo = OpenShiftInfo();
    }

    /// <summary>"Counter 2 · shift open since 08:00" when a shift is open on this till (logging in joins it, at its counter),
    /// otherwise "".</summary>
    public string ShiftInfo { get; }

    /// <summary>Till setup (printer, paper, till number), supervisor only. Nobody is logged in here, so the approval is
    /// logged without a cashier or shift.</summary>
    public AsyncRelayCommand SettingsCommand { get; }

    /// <summary>Upload problems (supervisor): documents ERPNext refused, or left out from before Live.</summary>
    public AsyncRelayCommand UploadProblemsCommand { get; }

    /// <summary>How many documents are on the Upload problems screen (the button shows when there are any).</summary>
    public int UploadProblemsCount { get; }

    /// <summary>How many documents were handled by hand (listed on the same screen).</summary>
    public int HandledUploads { get; }

    /// <summary>Failed, excluded and handled documents together: the button shows when there is any.</summary>
    public int UploadProblemsTotal => UploadProblemsCount + HandledUploads;

    /// <summary>"Upload problems (2)", or "Upload problems (0 · 3 handled)" when some were handled by hand.</summary>
    public string UploadProblemsLabel => HandledUploads == 0
        ? $"Upload problems ({UploadProblemsCount.ToString(CultureInfo.InvariantCulture)})"
        : $"Upload problems ({UploadProblemsCount.ToString(CultureInfo.InvariantCulture)} · {HandledUploads.ToString(CultureInfo.InvariantCulture)} handled)";

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

    private async Task OpenUploadProblemsAsync()
    {
        Pin = "";
        var gate = new SupervisorGate(ctx, new SessionState());
        if (await gate.ApproveAsync(ApprovalAction.SettingsChange, "Open Upload problems") is null) return;
        ctx.Navigator.Show(new UploadProblemsViewModel(ctx, gate, () => new LoginViewModel(ctx, session, newSale)));
    }

    private async Task OpenSettingsAsync()
    {
        Pin = "";
        var gate = new SupervisorGate(ctx, new SessionState());
        if (await gate.ApproveAsync(ApprovalAction.SettingsChange, "Change till settings") is not { } supervisor) return;
        await ctx.Dialogs.ShowSetupAsync((from, to) => UploadModeChangedAsync(gate, supervisor, from, to));
    }

    /// <summary>The upload mode decides whether the till writes to ERPNext: its change is logged with the approving supervisor.
    /// The first switch to Live waits until no shift is open (returns false: the old mode stays), and leaves the shifts closed
    /// before it (test data) out of the upload unless a supervisor includes them (logged).</summary>
    private async Task<bool> UploadModeChangedAsync(SupervisorGate gate, string supervisor, UploadMode from, UploadMode to)
    {
        if (to == UploadMode.Live && UploadHistory.MustCloseShiftFirst(ctx.Shifts, ctx.Kv))
        {
            ctx.Dialogs.Info(UploadHistory.CloseShiftFirst);
            return false;
        }
        var now = ctx.Clock.Now;
        ctx.Approvals.Add(new ApprovalRecord(Guid.NewGuid().ToString("N"), ApprovalAction.UploadModeChange, "", supervisor, "", null, null, 0m,
            $"Upload mode {from} → {to}", now));
        if (to != UploadMode.Live) return true;

        var earlier = UploadHistory.EarlierShifts(ctx.Shifts, ctx.Kv, now);
        var include = false;
        if (earlier > 0 && ctx.Dialogs.Confirm("Earlier shifts",
                $"{earlier.ToString(CultureInfo.InvariantCulture)} earlier shift(s) will NOT be uploaded (test data). Include them?"))
        {
            include = await gate.ApproveAsync(ApprovalAction.UploadIncludeHistory,
                $"Upload {earlier.ToString(CultureInfo.InvariantCulture)} earlier shift(s) to ERPNext") is not null;
            if (!include) ctx.Dialogs.Info("History stays excluded (test data).");
        }
        UploadHistory.SwitchToLive(ctx.Shifts, ctx.Kv, now, include);
        return true;
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
