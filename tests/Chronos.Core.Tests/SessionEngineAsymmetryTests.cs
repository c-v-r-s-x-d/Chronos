using System.Reflection;
using Chronos.Core.Rules;
using Chronos.Core.Sessions;

namespace Chronos.Core.Tests;

public sealed class SessionEngineAsymmetryTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteRule Site = new("example.com", includeSubdomains: true);

    private static SessionEngine StartedEngine(TestClock clock, IProtectedAppPolicy? policy = null)
    {
        var engine = new SessionEngine(clock, policy ?? FakeProtectedAppPolicy.AllowAll());
        engine.StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(3), SessionLimits.DefaultCoolDown);
        return engine;
    }

    [Fact]
    public void SetEndsAt_AcceptsLaterTime()
    {
        var engine = StartedEngine(new TestClock(Start));

        var result = engine.SetEndsAt(Start.AddHours(5));

        Assert.True(result.Accepted);
        Assert.Equal(Start.AddHours(5), engine.Session!.EndsAt);
    }

    [Fact]
    public void SetEndsAt_RejectsEarlierTime()
    {
        var engine = StartedEngine(new TestClock(Start));

        var result = engine.SetEndsAt(Start.AddMinutes(10));

        Assert.False(result.Accepted);
        Assert.Equal(Start.AddHours(3), engine.Session!.EndsAt);
    }

    [Fact]
    public void SetEndsAt_RejectsIdenticalTime()
    {
        var engine = StartedEngine(new TestClock(Start));

        var result = engine.SetEndsAt(Start.AddHours(3));

        Assert.False(result.Accepted);
    }

    [Fact]
    public void SetEndsAt_RespectsMaximumSessionDuration()
    {
        var engine = StartedEngine(new TestClock(Start));

        var result = engine.SetEndsAt(Start.AddDays(3));

        Assert.True(result.Accepted);
        Assert.Equal(Start + SessionLimits.MaxSessionDuration, engine.Session!.EndsAt);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void SetEndsAt_IsAllowedWhileUnlockIsPending()
    {
        var engine = StartedEngine(new TestClock(Start));
        engine.RequestUnlock();

        var result = engine.SetEndsAt(Start.AddHours(5));

        Assert.True(result.Accepted);
        Assert.Equal(SessionState.UnlockPending, engine.State);
    }

    [Fact]
    public void AddSiteRule_AppliesToCurrentSessionImmediately()
    {
        var engine = StartedEngine(new TestClock(Start));
        var added = new SiteRule("reddit.com", includeSubdomains: true);

        var result = engine.AddSiteRule(added);

        Assert.True(result.Accepted);
        Assert.Contains(added, engine.Session!.Rules.Sites);
        Assert.Equal(2, engine.Session.Rules.Sites.Count);
    }

    [Fact]
    public void AddAppRule_AppliesToCurrentSessionImmediately()
    {
        var engine = StartedEngine(new TestClock(Start));
        var added = new AppRule(AppMatchKind.FileName, "game.exe");

        var result = engine.AddAppRule(added);

        Assert.True(result.Accepted);
        Assert.Contains(added, engine.Session!.Rules.Apps);
    }

    [Fact]
    public void AddAppRule_RejectsProtectedApplication()
    {
        var policy = FakeProtectedAppPolicy.AllowAll().Protecting("explorer.exe");
        var engine = StartedEngine(new TestClock(Start), policy);

        var result = engine.AddAppRule(new AppRule(AppMatchKind.FileName, "explorer.exe"));

        Assert.False(result.Accepted);
        Assert.Empty(engine.Session!.Rules.Apps);
        Assert.Equal(FakeProtectedAppPolicy.Code, result.RejectionReason);
    }

    [Fact]
    public void AddSiteRule_RejectsWhenIdle()
    {
        var engine = new SessionEngine(new TestClock(Start), FakeProtectedAppPolicy.AllowAll());

        var result = engine.AddSiteRule(Site);

        Assert.False(result.Accepted);
    }

    [Fact]
    public void SetEndsAt_RejectsWhenEnded()
    {
        var clock = new TestClock(Start);
        var engine = StartedEngine(clock);
        clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromSeconds(1));
        engine.Tick();

        var result = engine.SetEndsAt(Start.AddHours(10));

        Assert.Equal(SessionState.Ended, engine.State);
        Assert.False(result.Accepted);
    }

    [Fact]
    public void AddSiteRule_RejectsWhenEnded()
    {
        var clock = new TestClock(Start);
        var engine = StartedEngine(clock);
        clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromSeconds(1));
        engine.Tick();

        var result = engine.AddSiteRule(new SiteRule("reddit.com", includeSubdomains: true));

        Assert.Equal(SessionState.Ended, engine.State);
        Assert.False(result.Accepted);
    }

    [Fact]
    public void SessionEngine_ExposesExactlyTheApprovedCommandSurface()
    {
        var approved = new[]
        {
            "StartSession",
            "Tick",
            "ConfirmCleared",
            "RequestUnlock",
            "CancelUnlock",
            "SetEndsAt",
            "AddSiteRule",
            "AddAppRule",
            "CurrentPlan",
        };

        // IsSpecialName excludes compiler-generated property accessors, which are not commands.
        // It also excludes setters, so property writability is pinned by the companion test below.
        var declared = typeof(SessionEngine)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .ToList();

        Assert.True(
            approved.OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(declared.OrderBy(name => name, StringComparer.Ordinal)),
            "SessionEngine's public command surface changed. Adding a method here is a " +
            "deliberate decision about the product's core guarantee that sessions can only be " +
            "tightened, never weakened - not a routine edit. Update the approved list only if " +
            "that decision was intentional.");
    }

    [Fact]
    public void SessionEngine_ExposesNoPubliclyWritableState()
    {
        var writable = typeof(SessionEngine)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(property => property.GetSetMethod(nonPublic: false) is not null)
            .Select(property => property.Name)
            .ToList();

        Assert.True(
            writable.Count == 0,
            $"SessionEngine exposes publicly writable state: {string.Join(", ", writable)}. " +
            "The command-surface test pins methods only, so a settable property would be a way to " +
            "weaken a running session without tripping it.");
    }
}
