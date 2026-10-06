using TillPOS.Core.Security;
using TillPOS.Presentation;

namespace TillPOS.Tests.Presentation;

public sealed class SupervisorGateTests : IDisposable
{
    private readonly PresentationFixture f = new();
    private readonly SupervisorGate gate;

    public SupervisorGateTests()
    {
        f.LogInWithOpenShift();
        gate = new SupervisorGate(f.Ctx, f.Session);
    }

    public void Dispose() => f.Dispose();

    [Fact]
    public async Task Supervisor_pin_approves_and_logs_who_asked_and_who_approved()
    {
        f.Dialogs.Pins.Enqueue("9999");

        var approver = await gate.ApproveAsync(ApprovalAction.LineVoid, "Remove Milk", null, "MILK", 6.79m);

        Assert.Equal("sup", approver);
        var record = Assert.Single(f.Ctx.Approvals.Unsynced());
        Assert.Equal(ApprovalAction.LineVoid, record.Action);
        Assert.Equal("simran", record.CashierId);
        Assert.Equal("sup", record.SupervisorId);
        Assert.Equal("TILL2-SHIFT-20261007080000", record.ShiftClientId);
    }

    [Fact]
    public async Task Cashier_pin_is_refused_and_the_failure_is_logged()
    {
        f.Dialogs.Pins.Enqueue("1111");

        Assert.Null(await gate.ApproveAsync(ApprovalAction.BillVoid, "Void bill"));

        var record = Assert.Single(f.Ctx.Approvals.Unsynced());
        Assert.Equal(ApprovalAction.FailedSupervisorPin, record.Action);
        Assert.Equal("simran", record.CashierId);
        Assert.Equal("TILL2-SHIFT-20261007080000", record.ShiftClientId);
    }

    [Fact]
    public async Task A_successful_login_does_not_reset_supervisor_pin_failures()
    {
        for (var i = 0; i < 4; i++)
        {
            f.Dialogs.Pins.Enqueue("0000");
            await gate.ApproveAsync(ApprovalAction.LineVoid, "x");
        }
        var login = new LoginViewModel(f.Ctx, f.Session, () => new object());
        foreach (var c in "1111") login.DigitCommand.Execute(c.ToString());
        login.LoginCommand.Execute(null);
        f.Dialogs.Pins.Enqueue("0000");
        await gate.ApproveAsync(ApprovalAction.LineVoid, "x");
        f.Dialogs.Pins.Enqueue("9999");
        var asked = f.Dialogs.PinRequests;

        Assert.Null(await gate.ApproveAsync(ApprovalAction.LineVoid, "x"));

        Assert.Equal(asked, f.Dialogs.PinRequests);
        Assert.Contains(f.Dialogs.Infos, m => m.Contains("Too many wrong PINs"));
    }

    [Fact]
    public async Task Cancelled_prompt_approves_nothing_and_logs_nothing()
    {
        Assert.Null(await gate.ApproveAsync(ApprovalAction.BillVoid, "Void bill"));
        Assert.Empty(f.Ctx.Approvals.Unsynced());
    }

    [Fact]
    public async Task Five_wrong_supervisor_pins_lock_the_prompt()
    {
        for (var i = 0; i < 5; i++)
        {
            f.Dialogs.Pins.Enqueue("0000");
            await gate.ApproveAsync(ApprovalAction.LineVoid, "x");
        }
        f.Dialogs.Pins.Enqueue("9999");
        var asked = f.Dialogs.PinRequests;

        Assert.Null(await gate.ApproveAsync(ApprovalAction.LineVoid, "x"));

        Assert.Equal(asked, f.Dialogs.PinRequests);
        Assert.Contains(f.Dialogs.Infos, m => m.Contains("Too many wrong PINs"));
        Assert.Equal(5, f.Ctx.Approvals.Unsynced().Count);
    }
}
