using TillPOS.Core.Security;

namespace TillPOS.Tests.Core;

public class PinAttemptLimiterTests
{
    private DateTimeOffset now = new(2026, 10, 7, 9, 0, 0, TimeSpan.FromHours(4));

    [Fact]
    public void Locks_after_five_failures_for_a_minute()
    {
        var limiter = new PinAttemptLimiter(() => now);
        for (var i = 0; i < 4; i++) limiter.Failed();
        Assert.False(limiter.IsLocked);

        limiter.Failed();

        Assert.True(limiter.IsLocked);
        Assert.Equal(TimeSpan.FromSeconds(60), limiter.Remaining);
        now = now.AddSeconds(61);
        Assert.False(limiter.IsLocked);
        Assert.Equal(TimeSpan.Zero, limiter.Remaining);
    }

    [Fact]
    public void Success_resets_the_count()
    {
        var limiter = new PinAttemptLimiter(() => now);
        for (var i = 0; i < 4; i++) limiter.Failed();
        limiter.Succeeded();
        for (var i = 0; i < 4; i++) limiter.Failed();
        Assert.False(limiter.IsLocked);
    }

    [Fact]
    public void After_a_lockout_the_count_starts_again()
    {
        var limiter = new PinAttemptLimiter(() => now);
        for (var i = 0; i < 5; i++) limiter.Failed();
        now = now.AddSeconds(61);
        for (var i = 0; i < 4; i++) limiter.Failed();
        Assert.False(limiter.IsLocked);
        limiter.Failed();
        Assert.True(limiter.IsLocked);
    }

    [Fact]
    public void Failed_supervisor_pin_is_an_approval_action() =>
        Assert.Equal("FailedSupervisorPin", ApprovalAction.FailedSupervisorPin.ToString());
}
