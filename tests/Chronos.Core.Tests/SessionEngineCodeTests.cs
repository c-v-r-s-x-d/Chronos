using Chronos.Core.Rules;
using Chronos.Core.Sessions;

namespace Chronos.Core.Tests;

/// <summary>The engine answers in codes and the reader words them; each refusal and warning has its own code.</summary>
public sealed class SessionEngineCodeTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteRule Site = new("example.com", includeSubdomains: true);

    private static SessionEngine Idle(IProtectedAppPolicy? policy = null) =>
        new(new TestClock(Start), policy ?? FakeProtectedAppPolicy.AllowAll());

    private static SessionEngine Started(IProtectedAppPolicy? policy = null)
    {
        var engine = Idle(policy);
        engine.StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(3), SessionLimits.DefaultCoolDown);

        return engine;
    }

    [Fact]
    public void StartingASecondSessionIsRefusedWithItsOwnCode()
    {
        var result = Started().StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(1), SessionLimits.DefaultCoolDown);

        Assert.Equal(SessionCodes.SessionStartWrongState, result.RejectionReason);
    }

    [Fact]
    public void StartingWithNothingToBlockIsRefusedWithItsOwnCode()
    {
        var result = Idle().StartSession(BlockList.Empty, TimeSpan.FromHours(1), SessionLimits.DefaultCoolDown);

        Assert.Equal(SessionCodes.SessionEmptyBlockList, result.RejectionReason);
    }

    /// <summary>The value it was clamped to rides as minutes, so either language can put it in a sentence.</summary>
    [Fact]
    public void AClampedStartSaysWhatItWasClampedToInMinutes()
    {
        var result = Idle().StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromMinutes(1), TimeSpan.FromHours(5));

        Assert.Equal(
            [
                new SessionNotice(SessionCodes.SessionDurationClamped, ["5"]),
                new SessionNotice(SessionCodes.SessionCoolDownClamped, ["60"]),
            ],
            result.Warnings,
            NoticeComparer.Instance);
    }

    [Fact]
    public void AnExtensionPastTheLongestSessionSaysTheLimitInMinutes()
    {
        var result = Started().SetEndsAt(Start.AddDays(3));

        var notice = Assert.Single(result.Warnings);
        Assert.Equal(SessionCodes.SessionEndClamped, notice.Code);
        Assert.Equal(["1440"], notice.Arguments);
    }

    [Fact]
    public void ShorteningIsRefusedWithItsOwnCode()
    {
        var result = Started().SetEndsAt(Start.AddMinutes(10));

        Assert.Equal(SessionCodes.SessionCannotShorten, result.RejectionReason);
    }

    [Fact]
    public void ExtendingWithNoSessionIsRefusedWithItsOwnCode()
    {
        var result = Idle().SetEndsAt(Start.AddHours(5));

        Assert.Equal(SessionCodes.SessionExtendWrongState, result.RejectionReason);
    }

    [Fact]
    public void AskingToUnlockWithNoSessionIsRefusedWithItsOwnCode()
    {
        Assert.Equal(SessionCodes.UnlockRequestWrongState, Idle().RequestUnlock().RejectionReason);
    }

    [Fact]
    public void CancellingAnUnlockNobodyAskedForIsRefusedWithItsOwnCode()
    {
        Assert.Equal(SessionCodes.UnlockCancelWrongState, Started().CancelUnlock().RejectionReason);
    }

    [Fact]
    public void AddingASiteWithNoSessionIsRefusedWithItsOwnCode()
    {
        Assert.Equal(SessionCodes.RulesAddWrongState, Idle().AddSiteRule(Site).RejectionReason);
    }

    [Fact]
    public void AddingAnAppWithNoSessionIsRefusedWithItsOwnCode()
    {
        var result = Idle().AddAppRule(new AppRule(AppMatchKind.FileName, "game.exe"));

        Assert.Equal(SessionCodes.RulesAddWrongState, result.RejectionReason);
    }

    /// <summary>The policy knows why an application is protected, and its code is what goes back.</summary>
    [Fact]
    public void AProtectedAppIsRefusedWithThePolicysOwnCode()
    {
        var engine = Started(FakeProtectedAppPolicy.AllowAll().Protecting("explorer.exe"));

        var result = engine.AddAppRule(new AppRule(AppMatchKind.FileName, "explorer.exe"));

        Assert.Equal(FakeProtectedAppPolicy.Code, result.RejectionReason);
    }

    [Fact]
    public void AProtectedAppWhosePolicyGaveNoReasonIsRefusedWithTheGeneralCode()
    {
        var engine = Started(new SilentPolicy());

        var result = engine.AddAppRule(new AppRule(AppMatchKind.FileName, "explorer.exe"));

        Assert.Equal(SessionCodes.RulesAppProtected, result.RejectionReason);
    }

    /// <summary>One notice per application left out, named by file name (from the user's own list) and never by path.</summary>
    [Theory]
    [InlineData(AppMatchKind.FileName, "explorer.exe")]
    [InlineData(AppMatchKind.FullPath, @"C:\Windows\explorer.exe")]
    [InlineData(AppMatchKind.FullPath, "/usr/bin/explorer.exe")]
    public void AProtectedAppLeftOutOfASessionIsNamedByItsFileNameOnly(AppMatchKind kind, string value)
    {
        var engine = Idle(FakeProtectedAppPolicy.AllowAll().Protecting(value));
        var rules = BlockList.Empty.WithSite(Site).WithApp(new AppRule(kind, value));

        var result = engine.StartSession(rules, TimeSpan.FromHours(1), SessionLimits.DefaultCoolDown);

        var notice = Assert.Single(result.Warnings);
        Assert.Equal(SessionCodes.SessionProtectedAppSkipped, notice.Code);
        Assert.Equal(["explorer.exe"], notice.Arguments);
    }

    private sealed class SilentPolicy : IProtectedAppPolicy
    {
        public ProtectionVerdict Evaluate(AppRule rule) => new(true, null);
    }

    /// <summary>Records compare their argument lists by reference; this compares them by content.</summary>
    private sealed class NoticeComparer : IEqualityComparer<SessionNotice>
    {
        public static NoticeComparer Instance { get; } = new();

        public bool Equals(SessionNotice? x, SessionNotice? y) =>
            x is not null && y is not null && x.Code == y.Code && x.Arguments.SequenceEqual(y.Arguments);

        public int GetHashCode(SessionNotice obj) => obj.Code.GetHashCode(StringComparison.Ordinal);
    }
}
