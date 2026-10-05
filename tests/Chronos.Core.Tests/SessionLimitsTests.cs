using Chronos.Core.Sessions;

namespace Chronos.Core.Tests;

public sealed class SessionLimitsTests
{
    [Fact]
    public void Defaults_MatchSpecification()
    {
        Assert.Equal(TimeSpan.FromMinutes(60), SessionLimits.DefaultSessionDuration);
        Assert.Equal(TimeSpan.FromMinutes(5), SessionLimits.MinSessionDuration);
        Assert.Equal(TimeSpan.FromHours(24), SessionLimits.MaxSessionDuration);
        Assert.Equal(TimeSpan.FromMinutes(1), SessionLimits.DefaultCoolDown);
        Assert.Equal(TimeSpan.FromMinutes(1), SessionLimits.MinCoolDown);
        Assert.Equal(TimeSpan.FromMinutes(60), SessionLimits.MaxCoolDown);
    }

    [Fact]
    public void ClampSessionDuration_LeavesValueInRangeUntouched()
    {
        var result = SessionLimits.ClampSessionDuration(TimeSpan.FromMinutes(30));

        Assert.Equal(TimeSpan.FromMinutes(30), result.Value);
        Assert.False(result.WasClamped);
    }

    [Fact]
    public void ClampSessionDuration_RaisesValueBelowMinimum()
    {
        var result = SessionLimits.ClampSessionDuration(TimeSpan.FromMinutes(1));

        Assert.Equal(SessionLimits.MinSessionDuration, result.Value);
        Assert.True(result.WasClamped);
    }

    [Fact]
    public void ClampSessionDuration_LowersValueAboveMaximum()
    {
        var result = SessionLimits.ClampSessionDuration(TimeSpan.FromDays(3));

        Assert.Equal(SessionLimits.MaxSessionDuration, result.Value);
        Assert.True(result.WasClamped);
    }

    [Fact]
    public void ClampCoolDown_RaisesZeroToMinimum()
    {
        var result = SessionLimits.ClampCoolDown(TimeSpan.Zero);

        Assert.Equal(SessionLimits.MinCoolDown, result.Value);
        Assert.True(result.WasClamped);
    }

    [Fact]
    public void ClampCoolDown_LowersValueAboveMaximum()
    {
        var result = SessionLimits.ClampCoolDown(TimeSpan.FromHours(5));

        Assert.Equal(SessionLimits.MaxCoolDown, result.Value);
        Assert.True(result.WasClamped);
    }

    [Fact]
    public void ClampSessionDuration_LeavesExactMinimumUnclamped()
    {
        var result = SessionLimits.ClampSessionDuration(SessionLimits.MinSessionDuration);

        Assert.Equal(SessionLimits.MinSessionDuration, result.Value);
        Assert.False(result.WasClamped);
    }

    [Fact]
    public void ClampSessionDuration_LeavesExactMaximumUnclamped()
    {
        var result = SessionLimits.ClampSessionDuration(SessionLimits.MaxSessionDuration);

        Assert.Equal(SessionLimits.MaxSessionDuration, result.Value);
        Assert.False(result.WasClamped);
    }

    [Fact]
    public void ClampCoolDown_LeavesExactMinimumUnclamped()
    {
        var result = SessionLimits.ClampCoolDown(SessionLimits.MinCoolDown);

        Assert.Equal(SessionLimits.MinCoolDown, result.Value);
        Assert.False(result.WasClamped);
    }

    [Fact]
    public void ClampCoolDown_LeavesExactMaximumUnclamped()
    {
        var result = SessionLimits.ClampCoolDown(SessionLimits.MaxCoolDown);

        Assert.Equal(SessionLimits.MaxCoolDown, result.Value);
        Assert.False(result.WasClamped);
    }

    [Fact]
    public void TestClock_AdvancesTime()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));

        clock.Advance(TimeSpan.FromMinutes(15));

        Assert.Equal(new DateTimeOffset(2026, 8, 16, 12, 15, 0, TimeSpan.Zero), clock.UtcNow);
    }
}
