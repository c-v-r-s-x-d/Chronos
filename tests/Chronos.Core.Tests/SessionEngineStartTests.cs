using Chronos.Core.Rules;
using Chronos.Core.Sessions;

namespace Chronos.Core.Tests;

public sealed class SessionEngineStartTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteRule Site = new("example.com", includeSubdomains: true);

    private static SessionEngine CreateEngine(TestClock clock, IProtectedAppPolicy? policy = null) =>
        new(clock, policy ?? FakeProtectedAppPolicy.AllowAll());

    [Fact]
    public void NewEngine_IsIdle()
    {
        var engine = CreateEngine(new TestClock(Start));

        Assert.Equal(SessionState.Idle, engine.State);
        Assert.Null(engine.Session);
    }

    [Fact]
    public void StartSession_ActivatesAndComputesEndsAt()
    {
        var clock = new TestClock(Start);
        var engine = CreateEngine(clock);

        var result = engine.StartSession(
            BlockList.Empty.WithSite(Site),
            TimeSpan.FromHours(2),
            SessionLimits.DefaultCoolDown);

        Assert.True(result.Accepted);
        Assert.Equal(SessionState.Active, engine.State);
        Assert.Equal(Start, engine.Session!.StartedAt);
        Assert.Equal(Start.AddHours(2), engine.Session.EndsAt);
        Assert.NotEqual(Guid.Empty, engine.Session.Id);
    }

    [Fact]
    public void StartSession_ClampsDurationAndCoolDownToAllowedRange()
    {
        var engine = CreateEngine(new TestClock(Start));

        var result = engine.StartSession(
            BlockList.Empty.WithSite(Site),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromHours(5));

        Assert.True(result.Accepted);
        Assert.Equal(Start + SessionLimits.MinSessionDuration, engine.Session!.EndsAt);
        Assert.Equal(SessionLimits.MaxCoolDown, engine.Session.CoolDown);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void StartSession_TakesSnapshotIndependentOfLaterListChanges()
    {
        var engine = CreateEngine(new TestClock(Start));
        var rules = BlockList.Empty.WithSite(Site);

        engine.StartSession(rules, TimeSpan.FromHours(1), SessionLimits.DefaultCoolDown);
        var reducedAfterStart = rules.WithoutSite(Site);

        Assert.Empty(reducedAfterStart.Sites);
        Assert.Contains(Site, engine.Session!.Rules.Sites);
    }

    [Fact]
    public void StartSession_RejectsEmptyRules()
    {
        var engine = CreateEngine(new TestClock(Start));

        var result = engine.StartSession(BlockList.Empty, TimeSpan.FromHours(1), SessionLimits.DefaultCoolDown);

        Assert.False(result.Accepted);
        Assert.Equal(SessionState.Idle, engine.State);
        Assert.NotNull(result.RejectionReason);
    }

    [Fact]
    public void StartSession_RejectsWhenSessionAlreadyActive()
    {
        var engine = CreateEngine(new TestClock(Start));
        engine.StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(1), SessionLimits.DefaultCoolDown);

        var second = engine.StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(3), SessionLimits.DefaultCoolDown);

        Assert.False(second.Accepted);
        Assert.Equal(Start.AddHours(1), engine.Session!.EndsAt);
    }

    [Fact]
    public void StartSession_DropsProtectedAppRulesAndWarns()
    {
        var policy = FakeProtectedAppPolicy.AllowAll().Protecting("explorer.exe");
        var engine = CreateEngine(new TestClock(Start), policy);
        var rules = BlockList.Empty
            .WithSite(Site)
            .WithApp(new AppRule(AppMatchKind.FileName, "explorer.exe"))
            .WithApp(new AppRule(AppMatchKind.FileName, "game.exe"));

        var result = engine.StartSession(rules, TimeSpan.FromHours(1), SessionLimits.DefaultCoolDown);

        Assert.True(result.Accepted);
        Assert.Single(engine.Session!.Rules.Apps);
        Assert.Contains(engine.Session.Rules.Apps, app => app.Value == "game.exe");
        Assert.Contains(result.Warnings, warning => warning.Code == SessionCodes.SessionProtectedAppSkipped);
    }

    [Fact]
    public void StartSession_RejectionKeepsTheWarningsThatExplainIt()
    {
        var policy = FakeProtectedAppPolicy.AllowAll().Protecting("explorer.exe");
        var engine = CreateEngine(new TestClock(Start), policy);
        var rules = BlockList.Empty.WithApp(new AppRule(AppMatchKind.FileName, "explorer.exe"));

        var result = engine.StartSession(rules, TimeSpan.FromHours(1), SessionLimits.DefaultCoolDown);

        Assert.False(result.Accepted);
        Assert.Equal(SessionState.Idle, engine.State);
        Assert.Contains(result.Warnings, warning => warning.Code == SessionCodes.SessionProtectedAppSkipped);
    }
}
