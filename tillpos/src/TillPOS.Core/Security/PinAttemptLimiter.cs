namespace TillPOS.Core.Security;

/// <summary>Throttles wrong PINs: after <c>maxFailures</c> consecutive failures the PIN prompt is locked for the lockout period.
/// Use one instance per PIN prompt: the login screen and the supervisor prompt each have their own.</summary>
public sealed class PinAttemptLimiter(Func<DateTimeOffset> now, int maxFailures = 5, TimeSpan? lockout = null)
{
    private readonly TimeSpan lockFor = lockout ?? TimeSpan.FromSeconds(60);
    private int failures;
    private DateTimeOffset? lockedUntil;

    public bool IsLocked => lockedUntil is { } until && now() < until;

    public TimeSpan Remaining
    {
        get
        {
            var time = now();
            return lockedUntil is { } until && time < until ? until - time : TimeSpan.Zero;
        }
    }

    public void Failed()
    {
        failures++;
        if (failures < maxFailures) return;
        lockedUntil = now() + lockFor;
        failures = 0;
    }

    public void Succeeded()
    {
        failures = 0;
        lockedUntil = null;
    }
}
