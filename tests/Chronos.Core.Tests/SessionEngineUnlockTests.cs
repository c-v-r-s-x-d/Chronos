using Chronos.Core.Rules;
using Chronos.Core.Sessions;

namespace Chronos.Core.Tests;

public sealed class SessionEngineUnlockTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteRule Site = new("example.com", includeSubdomains: true);

    private static SessionEngine StartedEngine(TestClock clock, TimeSpan coolDown)
    {
        var engine = new SessionEngine(clock, FakeProtectedAppPolicy.AllowAll());
        engine.StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(4), coolDown);
        return engine;
    }

    [Fact]
    public void RequestUnlock_MovesToPendingAndKeepsBlockingActive()
    {
        var clock = new TestClock(Start);
        var engine = StartedEngine(clock, TimeSpan.FromMinutes(10));

        var result = engine.RequestUnlock();

        Assert.True(result.Accepted);
        Assert.Equal(SessionState.UnlockPending, engine.State);
        Assert.Equal(Start.AddMinutes(10), engine.Session!.Unlock!.EffectiveAt);
    }

    [Fact]
    public void RequestUnlock_UsesCoolDownCapturedAtSessionStart()
    {
        var clock = new TestClock(Start);
        var engine = StartedEngine(clock, TimeSpan.FromMinutes(10));

        clock.Advance(TimeSpan.FromMinutes(30));
        engine.RequestUnlock();

        Assert.Equal(Start.AddMinutes(40), engine.Session!.Unlock!.EffectiveAt);
    }

    [Fact]
    public void RequestUnlock_RejectsWhenAlreadyPending()
    {
        var clock = new TestClock(Start);
        var engine = StartedEngine(clock, TimeSpan.FromMinutes(10));
        engine.RequestUnlock();

        clock.Advance(TimeSpan.FromMinutes(5));
        var second = engine.RequestUnlock();

        Assert.False(second.Accepted);
        Assert.Equal(Start.AddMinutes(10), engine.Session!.Unlock!.EffectiveAt);
    }

    [Fact]
    public void RequestUnlock_RejectsWhenIdle()
    {
        var engine = new SessionEngine(new TestClock(Start), FakeProtectedAppPolicy.AllowAll());

        var result = engine.RequestUnlock();

        Assert.False(result.Accepted);
    }

    [Fact]
    public void CancelUnlock_ReturnsToActiveImmediately()
    {
        var clock = new TestClock(Start);
        var engine = StartedEngine(clock, TimeSpan.FromMinutes(10));
        engine.RequestUnlock();

        clock.Advance(TimeSpan.FromMinutes(9));
        var result = engine.CancelUnlock();

        Assert.True(result.Accepted);
        Assert.Equal(SessionState.Active, engine.State);
        Assert.Null(engine.Session!.Unlock);
    }

    [Fact]
    public void CancelUnlock_RejectsWhenNoRequestIsPending()
    {
        var engine = StartedEngine(new TestClock(Start), TimeSpan.FromMinutes(10));

        var result = engine.CancelUnlock();

        Assert.False(result.Accepted);
        Assert.Equal(SessionState.Active, engine.State);
    }

    [Fact]
    public void Tick_EndsSessionWhenCoolDownExpires()
    {
        var clock = new TestClock(Start);
        var engine = StartedEngine(clock, TimeSpan.FromMinutes(10));
        engine.RequestUnlock();

        clock.Advance(TimeSpan.FromMinutes(10));
        engine.Tick();

        Assert.Equal(SessionState.Ended, engine.State);
    }

    [Fact]
    public void Tick_KeepsBlockingWhileCoolDownIsRunning()
    {
        var clock = new TestClock(Start);
        var engine = StartedEngine(clock, TimeSpan.FromMinutes(10));
        engine.RequestUnlock();

        clock.Advance(TimeSpan.FromMinutes(9).Add(TimeSpan.FromSeconds(59)));
        engine.Tick();

        Assert.Equal(SessionState.UnlockPending, engine.State);
    }

    [Fact]
    public void CancelledRequest_DoesNotEndSessionWhenOriginalDeadlinePasses()
    {
        var clock = new TestClock(Start);
        var engine = StartedEngine(clock, TimeSpan.FromMinutes(10));
        engine.RequestUnlock();
        clock.Advance(TimeSpan.FromMinutes(5));
        engine.CancelUnlock();

        clock.Advance(TimeSpan.FromMinutes(10));
        engine.Tick();

        Assert.Equal(SessionState.Active, engine.State);
    }
}
