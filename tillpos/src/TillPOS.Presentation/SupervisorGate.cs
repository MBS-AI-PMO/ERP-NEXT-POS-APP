using System.Globalization;
using TillPOS.Core.Security;

namespace TillPOS.Presentation;

/// <summary>Asks for a supervisor PIN, throttles wrong PINs, and logs every approval and every failed supervisor PIN
/// with the requesting cashier and the shift (spec §13b.5).</summary>
public sealed class SupervisorGate(TillContext ctx, SessionState session)
{
    /// <summary>The approving supervisor's id, or null when refused.</summary>
    public async Task<string?> ApproveAsync(ApprovalAction action, string reason, string? receiptClientId = null, string? itemCode = null,
        decimal amount = 0m) =>
        (await ApproveBySupervisorAsync(action, reason, receiptClientId, itemCode, amount))?.Id;

    /// <summary>The approving supervisor (id and name), or null when refused.</summary>
    public async Task<Cashier?> ApproveBySupervisorAsync(ApprovalAction action, string reason, string? receiptClientId = null,
        string? itemCode = null, decimal amount = 0m)
    {
        if (ctx.SupervisorLimiter.IsLocked)
        {
            ctx.Dialogs.Info($"Too many wrong PINs. Try again in {Math.Ceiling(ctx.SupervisorLimiter.Remaining.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s.");
            return null;
        }

        var pin = await ctx.Dialogs.AskPinAsync("Supervisor approval", reason);
        if (pin is null) return null;

        var supervisor = ctx.Authenticator.Supervisor(pin);
        if (supervisor is null)
        {
            ctx.SupervisorLimiter.Failed();
            Log(ApprovalAction.FailedSupervisorPin, "", reason, receiptClientId, itemCode, amount);
            ctx.Dialogs.Info("That is not a supervisor PIN.");
            return null;
        }

        ctx.SupervisorLimiter.Succeeded();
        Log(action, supervisor.Id, reason, receiptClientId, itemCode, amount);
        return supervisor;
    }

    private void Log(ApprovalAction action, string supervisorId, string reason, string? receiptClientId, string? itemCode, decimal amount) =>
        ctx.Approvals.Add(new ApprovalRecord(Guid.NewGuid().ToString("N"), action, session.Cashier?.Id ?? "", supervisorId,
            session.Shift?.ClientId ?? "", receiptClientId, itemCode, amount, reason, ctx.Clock.Now));
}
