using Chronos.Core.Rules;
using Chronos.Core.Sessions;

namespace Chronos.Core.Tests;

public sealed class SessionEngineTimeTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteRule Site = new("example.com", includeSubdomains: true);

    private static SessionEngine StartedEngine(TestClock clock, TimeSpan duration)
    {
        var engine = new SessionEngine(clock, FakeProtectedAppPolicy.AllowAll());
        engine.StartSession(BlockList.Empty.WithSite(Site), duration, SessionLimits.DefaultCoolDown);
        return engine;
    }

    [Fact]
    public void Tick_KeepsSessionActiveBeforeEndsAt()
    {
        var clock = new TestClock(Start);
        var engine = StartedEngine(clock, TimeSpan.FromHours(2));

        clock.Advance(TimeSpan.FromMinutes(119));
        engine.Tick();

        Assert.Equal(SessionState.Active, engine.State);
    }

    [Fact]
    public void Tick_EndsSessionAtEndsAt()
    {
        var clock = new TestClock(Start);
        var engine = StartedEngine(clock, TimeSpan.FromHours(2));

        clock.Advance(TimeSpan.FromHours(2));
        engine.Tick();

        Assert.Equal(SessionState.Ended, engine.State);
    }

    [Fact]
    public void ConfirmCleared_MovesFromEndedToIdle()
    {
        var clock = new TestClock(Start);
        var engine = StartedEngine(clock, TimeSpan.FromHours(1));
        clock.Advance(TimeSpan.FromHours(1));
        engine.Tick();

        engine.ConfirmCleared();

        Assert.Equal(SessionState.Idle, engine.State);
        Assert.Null(engine.Session);
    }

    [Fact]
    public void ConfirmCleared_ThrowsWhenSessionIsStillActive()
    {
        var clock = new TestClock(Start);
        var engine = StartedEngine(clock, TimeSpan.FromHours(1));

        Assert.Throws<InvalidOperationException>(engine.ConfirmCleared);
    }

    [Fact]
    public void Restore_ResumesSessionThatIsStillRunning()
    {
        var session = new BlockSession
        {
            Id = Guid.NewGuid(),
            StartedAt = Start,
            EndsAt = Start.AddHours(6),
            Rules = BlockList.Empty.WithSite(Site),
            CoolDown = SessionLimits.DefaultCoolDown,
        };
        var clock = new TestClock(Start.AddHours(3));

        var engine = SessionEngine.Restore(clock, FakeProtectedAppPolicy.AllowAll(), session);

        Assert.Equal(SessionState.Active, engine.State);
        Assert.Equal(session.EndsAt, engine.Session!.EndsAt);
    }

    [Fact]
    public void Restore_EndsSessionThatExpiredWhileMachineWasOff()
    {
        var session = new BlockSession
        {
            Id = Guid.NewGuid(),
            StartedAt = Start,
            EndsAt = Start.AddHours(6),
            Rules = BlockList.Empty.WithSite(Site),
            CoolDown = SessionLimits.DefaultCoolDown,
        };
        var clock = new TestClock(Start.AddHours(6).AddMinutes(30));

        var engine = SessionEngine.Restore(clock, FakeProtectedAppPolicy.AllowAll(), session);

        Assert.Equal(SessionState.Ended, engine.State);
    }

    [Fact]
    public void Restore_ResumesPendingUnlock()
    {
        var session = new BlockSession
        {
            Id = Guid.NewGuid(),
            StartedAt = Start,
            EndsAt = Start.AddHours(6),
            Rules = BlockList.Empty.WithSite(Site),
            CoolDown = TimeSpan.FromMinutes(10),
            Unlock = new UnlockRequest(Start.AddHours(1), Start.AddHours(1).AddMinutes(10)),
        };
        var clock = new TestClock(Start.AddHours(1).AddMinutes(5));

        var engine = SessionEngine.Restore(clock, FakeProtectedAppPolicy.AllowAll(), session);

        Assert.Equal(SessionState.UnlockPending, engine.State);
    }

    [Fact]
    public void Restore_EndsSessionWhoseUnlockDeadlinePassedWhileMachineWasOff()
    {
        var session = new BlockSession
        {
            Id = Guid.NewGuid(),
            StartedAt = Start,
            EndsAt = Start.AddHours(6),
            Rules = BlockList.Empty.WithSite(Site),
            CoolDown = TimeSpan.FromMinutes(10),
            Unlock = new UnlockRequest(Start.AddHours(1), Start.AddHours(1).AddMinutes(10)),
        };
        var clock = new TestClock(Start.AddHours(2));

        var engine = SessionEngine.Restore(clock, FakeProtectedAppPolicy.AllowAll(), session);

        Assert.Equal(SessionState.Ended, engine.State);
    }
}
