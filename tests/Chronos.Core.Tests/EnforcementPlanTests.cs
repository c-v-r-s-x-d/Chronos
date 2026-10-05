using Chronos.Core.Enforcement;
using Chronos.Core.Rules;
using Chronos.Core.Sessions;

namespace Chronos.Core.Tests;

public sealed class EnforcementPlanTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteRule Site = new("example.com", includeSubdomains: true);

    private static SessionEngine StartedEngine(TestClock clock)
    {
        var engine = new SessionEngine(clock, FakeProtectedAppPolicy.AllowAll());
        engine.StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(2), SessionLimits.DefaultCoolDown);
        return engine;
    }

    [Fact]
    public void CurrentPlan_IsEmptyWhenIdle()
    {
        var engine = new SessionEngine(new TestClock(Start), FakeProtectedAppPolicy.AllowAll());

        var plan = engine.CurrentPlan();

        Assert.True(plan.IsEmpty);
        Assert.Equal(Guid.Empty, plan.SessionId);
    }

    [Fact]
    public void CurrentPlan_ContainsSnapshotRulesWhileActive()
    {
        var engine = StartedEngine(new TestClock(Start));

        var plan = engine.CurrentPlan();

        Assert.False(plan.IsEmpty);
        Assert.Contains(Site, plan.Sites);
        Assert.Equal(engine.Session!.Id, plan.SessionId);
        Assert.Equal(engine.Session.EndsAt, plan.EndsAt);
    }

    [Fact]
    public void CurrentPlan_StillBlocksWhileUnlockIsPending()
    {
        var engine = StartedEngine(new TestClock(Start));
        engine.RequestUnlock();

        var plan = engine.CurrentPlan();

        Assert.False(plan.IsEmpty);
        Assert.Contains(Site, plan.Sites);
    }

    [Fact]
    public void CurrentPlan_IsEmptyOnceSessionEnded()
    {
        var clock = new TestClock(Start);
        var engine = StartedEngine(clock);

        clock.Advance(TimeSpan.FromHours(2));
        engine.Tick();

        Assert.True(engine.CurrentPlan().IsEmpty);
    }
}
