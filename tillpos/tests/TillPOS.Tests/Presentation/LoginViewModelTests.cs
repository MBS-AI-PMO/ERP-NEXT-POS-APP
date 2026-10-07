using TillPOS.Core.Security;
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using TillPOS.Data;
using TillPOS.Presentation;
using TillPOS.Sync.Upload;

namespace TillPOS.Tests.Presentation;

public sealed class LoginViewModelTests : IDisposable
{
    private readonly PresentationFixture f = new();
    private readonly object saleMarker = new();

    public void Dispose() => f.Dispose();

    private LoginViewModel Login() => new(f.Ctx, f.Session, () => saleMarker);

    private static void Enter(LoginViewModel vm, string pin)
    {
        foreach (var c in pin) vm.DigitCommand.Execute(c.ToString());
        vm.LoginCommand.Execute(null);
    }

    [Fact]
    public void Wrong_pin_shows_a_message_and_clears()
    {
        var vm = Login();
        Enter(vm, "4321");
        Assert.Equal("Wrong PIN.", vm.Message);
        Assert.Equal("", vm.Pin);
        Assert.Null(f.Session.Cashier);
    }

    [Fact]
    public void Five_wrong_pins_lock_login_even_for_the_right_pin()
    {
        var vm = Login();
        for (var i = 0; i < 5; i++) Enter(vm, "4321");
        Enter(vm, "1111");
        Assert.StartsWith("Too many wrong PINs", vm.Message);
        Assert.Null(f.Session.Cashier);
    }

    [Fact]
    public void Right_pin_without_an_open_shift_goes_to_open_shift()
    {
        Enter(Login(), "1111");
        Assert.Equal("simran", f.Session.Cashier!.Id);
        Assert.IsType<OpenShiftViewModel>(f.Navigator.Current);
    }

    [Fact]
    public void Right_pin_with_an_open_shift_goes_to_the_sale_screen()
    {
        f.LogInWithOpenShift();
        f.Session.Cashier = null;
        f.Session.Shift = null;

        Enter(Login(), "1111");

        Assert.Same(saleMarker, f.Navigator.Current);
        Assert.Equal("TILL2-SHIFT-20261007080000", f.Session.Shift!.ClientId);
    }

    [Fact]
    public void Pin_is_at_most_six_digits_and_masked()
    {
        var vm = Login();
        foreach (var c in "12345678") vm.DigitCommand.Execute(c.ToString());
        Assert.Equal("123456", vm.Pin);
        Assert.Equal("●●●●●●", vm.PinMask);
    }

    [Fact]
    public async Task Settings_with_a_supervisor_pin_opens_setup_once_and_logs_the_approval()
    {
        f.Dialogs.Pins.Enqueue("9999");

        await Login().SettingsCommand.ExecuteAsync(null);

        Assert.Equal(1, f.Dialogs.SetupRequests);
        var record = Assert.Single(f.Ctx.Approvals.Unsynced());
        Assert.Equal(ApprovalAction.SettingsChange, record.Action);
        Assert.Equal("sup", record.SupervisorId);
        Assert.Equal("", record.CashierId);
        Assert.Equal("", record.ShiftClientId);
    }

    [Fact]
    public async Task Changing_the_upload_mode_in_settings_is_logged_with_the_supervisor()
    {
        f.Dialogs.Pins.Enqueue("9999");
        f.Dialogs.SetupUploadChange = (UploadMode.Off, UploadMode.Live);

        await Login().SettingsCommand.ExecuteAsync(null);

        var change = Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.UploadModeChange);
        Assert.Equal("sup", change.SupervisorId);
        Assert.Equal("Upload mode Off → Live", change.Reason);
        Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.SettingsChange);
    }

    /// <summary>A closed shift from two days ago with one bill, still to upload.</summary>
    private void OldShift(string id, int daysAgo)
    {
        var opened = f.Clock.Now.AddDays(-daysAgo);
        f.Ctx.Shifts.Open(new ShiftOpening(id, "simran", "Al Ain Counter 2", opened, [new ReceiptPayment("Cash Counter 2", 100m)]));
        f.Ctx.Receipts.Save(new Receipt(id + "-BILL", ReceiptKind.Sale, null, id, "simran", opened.AddHours(1), [], 1m, 1m, 0m, 1m, false, 0m, 0m,
            [], 0m, 0m, null));
        f.Ctx.Shifts.Close(new ShiftClosing(id, opened.AddHours(2), [], 1, 0, 1m, 1m, 0m));
    }

    [Fact]
    public async Task First_switch_to_live_leaves_earlier_shifts_out_unless_included()
    {
        OldShift("OLD1", 2);
        OldShift("OLD2", 1);
        f.Dialogs.Pins.Enqueue("9999");
        f.Dialogs.SetupUploadChange = (UploadMode.DryRun, UploadMode.Live);
        f.Dialogs.ConfirmAnswers.Enqueue(false);

        await Login().SettingsCommand.ExecuteAsync(null);

        Assert.Contains("2 earlier shift(s) will NOT be uploaded (test data). Include them?", Assert.Single(f.Dialogs.Confirms));
        Assert.Equal(f.Clock.Now, UploadHistory.LiveSince(f.Ctx.Kv));
        Assert.Equal(UploadStatus.Excluded, f.Ctx.Shifts.SyncInfo("OLD1")!.OpeningStatus);
        Assert.Equal(ReceiptSyncStatus.Excluded, f.Ctx.Receipts.SyncInfo("OLD2-BILL").Status);
        Assert.Empty(f.Ctx.Shifts.Unfinished());
        Assert.DoesNotContain(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.UploadIncludeHistory);
    }

    [Fact]
    public async Task Including_earlier_shifts_needs_a_supervisor_and_is_logged()
    {
        OldShift("OLD1", 2);
        f.Dialogs.Pins.Enqueue("9999");
        f.Dialogs.Pins.Enqueue("9999");
        f.Dialogs.SetupUploadChange = (UploadMode.Off, UploadMode.Live);
        f.Dialogs.ConfirmAnswers.Enqueue(true);

        await Login().SettingsCommand.ExecuteAsync(null);

        Assert.Equal(f.Clock.Now.AddDays(-2), UploadHistory.LiveSince(f.Ctx.Kv));
        Assert.Equal(UploadStatus.Pending, f.Ctx.Shifts.SyncInfo("OLD1")!.OpeningStatus);
        var include = Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.UploadIncludeHistory);
        Assert.Equal("sup", include.SupervisorId);
    }

    [Fact]
    public async Task Including_with_a_wrong_pin_still_leaves_earlier_shifts_out()
    {
        OldShift("OLD1", 2);
        f.Dialogs.Pins.Enqueue("9999");
        f.Dialogs.Pins.Enqueue("1111");
        f.Dialogs.SetupUploadChange = (UploadMode.Off, UploadMode.Live);
        f.Dialogs.ConfirmAnswers.Enqueue(true);

        await Login().SettingsCommand.ExecuteAsync(null);

        Assert.Equal(UploadStatus.Excluded, f.Ctx.Shifts.SyncInfo("OLD1")!.OpeningStatus);
    }

    [Fact]
    public async Task Going_live_again_after_a_rollback_keeps_the_queue()
    {
        UploadHistory.SwitchToLive(f.Ctx.Shifts, f.Ctx.Kv, f.Clock.Now.AddDays(-5), includeHistory: false);
        OldShift("QUEUED", 1);   // taken while Upload was Off after going Live
        f.Dialogs.Pins.Enqueue("9999");
        f.Dialogs.SetupUploadChange = (UploadMode.Off, UploadMode.Live);

        await Login().SettingsCommand.ExecuteAsync(null);

        Assert.Empty(f.Dialogs.Confirms);
        Assert.Equal(UploadStatus.Pending, f.Ctx.Shifts.SyncInfo("QUEUED")!.OpeningStatus);
        Assert.Equal(f.Clock.Now.AddDays(-5), UploadHistory.LiveSince(f.Ctx.Kv));
    }

    [Fact]
    public async Task Settings_with_a_cashier_pin_does_not_open_setup()
    {
        f.Dialogs.Pins.Enqueue("1111");

        await Login().SettingsCommand.ExecuteAsync(null);

        Assert.Equal(0, f.Dialogs.SetupRequests);
        Assert.Equal(ApprovalAction.FailedSupervisorPin, Assert.Single(f.Ctx.Approvals.Unsynced()).Action);
    }

    [Fact]
    public void Opening_a_shift_records_the_float_and_goes_to_sale()
    {
        f.Session.Cashier = PresentationFixture.Simran;
        var vm = new OpenShiftViewModel(f.Ctx, f.Session, () => saleMarker);
        vm.OpeningCash.Text = "200";

        vm.OpenCommand.Execute(null);

        var shift = f.Ctx.Shifts.Current()!;
        Assert.Equal("TILL2-SHIFT-20261007100000", shift.ClientId);
        Assert.Equal(200m, Assert.Single(shift.OpeningAmounts).Amount);
        Assert.Equal("Cash Counter 2", shift.OpeningAmounts[0].ModeOfPayment);
        Assert.Same(saleMarker, f.Navigator.Current);
    }

    [Fact]
    public void Opening_a_shift_needs_an_amount()
    {
        f.Session.Cashier = PresentationFixture.Simran;
        var vm = new OpenShiftViewModel(f.Ctx, f.Session, () => saleMarker);
        vm.OpenCommand.Execute(null);
        Assert.Null(f.Ctx.Shifts.Current());
        Assert.Equal("Enter the cash in the drawer.", vm.Message);
    }
}
