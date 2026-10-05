using System.Globalization;
using Chronos.Core.Enforcement;
using Chronos.Core.Rules;
using Chronos.Core.Sessions;
using Chronos.Ipc;
using Chronos.Service.Configuration;
using Chronos.Service.Ipc;
using Chronos.Service.Reconciliation;
using Chronos.Service.Rules;
using Chronos.Service.Sessions;
using Chronos.Service.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

public sealed class CommandDispatcherTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceTestClock _clock = new(Start);
    private readonly EventBus _events = new();

    private ConfigStore _config = null!;

    private LayerStatusRegistry _layers = null!;

    private (CommandDispatcher Dispatcher, SessionEngine Engine) Build(IProtectedAppPolicy? policy = null)
    {
        var paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();

        var config = new ConfigStore(paths, NullLogger<ConfigStore>.Instance);
        config.Save(new ChronosConfig
        {
            Sites = [new SiteRuleDto("example.com", true)],
            Apps = [new AppRuleDto("FileName", "steam.exe")],
            CoolDownMinutes = 5,
            DefaultSessionMinutes = 90,
        });

        _config = config;
        _layers = new LayerStatusRegistry(_clock);

        var engine = new SessionEngine(_clock, policy ?? new WindowsProtectedAppPolicy());
        var gate = new SessionGate();
        var runner = new ReconcileRunner(
            engine,
            new ReconcileCoordinator([]),
            _layers,
            new StateStore(paths, NullLogger<StateStore>.Instance),
            gate,
            new EventBus(),
            TestStatus.Blank,
            NullLogger<ReconcileRunner>.Instance);
        var scheduler = new ReconcileScheduler(runner, NullLogger<ReconcileScheduler>.Instance);

        var dispatcher = new CommandDispatcher(
            engine,
            config,
            scheduler,
            _layers,
            gate,
            _clock,
            policy ?? new WindowsProtectedAppPolicy(),
            _events,
            NullLogger<CommandDispatcher>.Instance);

        return (dispatcher, engine);
    }

    private static IpcRequest Request(
        string command,
        int? minutes = null,
        SiteRuleMessage? site = null,
        AppRuleMessage? app = null,
        SettingsMessage? settings = null) => new()
    {
        ProtocolVersion = IpcProtocol.Version,
        Command = command,
        DurationMinutes = minutes,
        Site = site,
        App = app,
        Settings = settings,
    };

    /// <summary>Runs a session out, leaving the engine in Ended until the loop clears it.</summary>
    private void RunTheSessionOut(CommandDispatcher dispatcher)
    {
        dispatcher.Dispatch(Request("StartSession", minutes: 60));
        _clock.Advance(TimeSpan.FromMinutes(90));

        Assert.Equal(nameof(SessionState.Ended), dispatcher.Dispatch(Request("GetStatus")).Status!.State);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void GetStatus_ReportsIdleBeforeAnySession()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("GetStatus"));

        Assert.True(response.Accepted);
        Assert.Equal(nameof(SessionState.Idle), response.Status!.State);
        Assert.Null(response.Status.EndsAt);
    }

    [Fact]
    public void StartSession_UsesTheConfiguredListAndDuration()
    {
        var (dispatcher, engine) = Build();

        var response = dispatcher.Dispatch(Request("StartSession", minutes: 120));

        Assert.True(response.Accepted);
        Assert.Equal(SessionState.Active, engine.State);
        Assert.Equal(Start.AddHours(2), engine.Session!.EndsAt);
        Assert.Contains(new SiteRule("example.com", includeSubdomains: true), engine.Session.Rules.Sites);
    }

    [Fact]
    public void StartSession_FallsBackToTheConfiguredDefaultDuration()
    {
        var (dispatcher, engine) = Build();

        dispatcher.Dispatch(Request("StartSession"));

        Assert.Equal(Start.AddMinutes(90), engine.Session!.EndsAt);
    }

    [Fact]
    public void StartSession_TakesTheCoolDownFromConfiguration()
    {
        var (dispatcher, engine) = Build();

        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        Assert.Equal(TimeSpan.FromMinutes(5), engine.Session!.CoolDown);
    }

    [Fact]
    public void RequestUnlock_ThenCancel_ReturnsToActive()
    {
        var (dispatcher, engine) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        Assert.True(dispatcher.Dispatch(Request("RequestUnlock")).Accepted);
        Assert.Equal(SessionState.UnlockPending, engine.State);

        Assert.True(dispatcher.Dispatch(Request("CancelUnlock")).Accepted);
        Assert.Equal(SessionState.Active, engine.State);
    }

    [Fact]
    public void ExtendSession_MovesTheEndTimeForward()
    {
        var (dispatcher, engine) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        var response = dispatcher.Dispatch(Request("ExtendSession", minutes: 30));

        Assert.True(response.Accepted);
        Assert.Equal(Start.AddMinutes(90), engine.Session!.EndsAt);
    }

    [Fact]
    public void ExtendSession_RejectsANonPositiveAmount()
    {
        var (dispatcher, engine) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        var response = dispatcher.Dispatch(Request("ExtendSession", minutes: -30));

        Assert.False(response.Accepted);
        Assert.Equal(Start.AddMinutes(60), engine.Session!.EndsAt);
    }

    [Fact]
    public void ExtendSession_RejectsWhenThereIsNoSession()
    {
        var (dispatcher, engine) = Build();

        var response = dispatcher.Dispatch(Request("ExtendSession", minutes: 30));

        Assert.False(response.Accepted);
        Assert.NotNull(response.Error);
        Assert.Null(engine.Session);
    }

    [Fact]
    public void ExtendSession_RejectsZeroMinutes()
    {
        var (dispatcher, engine) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        var response = dispatcher.Dispatch(Request("ExtendSession", minutes: 0));

        Assert.False(response.Accepted);
        Assert.Equal(Start.AddMinutes(60), engine.Session!.EndsAt);
    }

    [Fact]
    public void StartSession_PassesTheEnginesRejectionBackUnchanged()
    {
        var (dispatcher, engine) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));
        var endsAtBefore = engine.Session!.EndsAt;

        var second = dispatcher.Dispatch(Request("StartSession", minutes: 120));

        Assert.False(second.Accepted);
        Assert.NotNull(second.Error);
        Assert.Equal(endsAtBefore, engine.Session!.EndsAt);
    }

    [Fact]
    public void RequestUnlock_PassesTheEnginesRejectionBackWhenAlreadyPending()
    {
        var (dispatcher, engine) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));
        dispatcher.Dispatch(Request("RequestUnlock"));
        var deadlineBefore = engine.Session!.Unlock!.EffectiveAt;

        var second = dispatcher.Dispatch(Request("RequestUnlock"));

        Assert.False(second.Accepted);
        Assert.NotNull(second.Error);
        Assert.Equal(deadlineBefore, engine.Session!.Unlock!.EffectiveAt);
    }

    [Fact]
    public void RejectsAnUnknownProtocolVersion()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(new IpcRequest
        {
            ProtocolVersion = IpcProtocol.Version + 1,
            Command = "GetStatus",
        });

        Assert.False(response.Accepted);
        Assert.Contains("protocol", response.Error!, StringComparison.OrdinalIgnoreCase);

        // Against the constant, never a literal 1, so raising the version cannot turn this into a test of
        // nothing. The client is told which version it should have spoken.
        Assert.Contains(
            IpcProtocol.Version.ToString(CultureInfo.InvariantCulture),
            response.Error!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GetStatus_CarriesTheServicesOwnClock()
    {
        // The interface counts down from EndsAt on its own clock; without the service's moment a machine
        // with a different clock shows the wrong countdown.
        var (dispatcher, _) = Build();

        Assert.Equal(Start, dispatcher.Dispatch(Request("GetStatus")).Status!.Now);
    }

    [Fact]
    public void GetStatus_ReportsNoLayersBeforeTheFirstReconcilePass()
    {
        var (dispatcher, _) = Build();

        Assert.Empty(dispatcher.Dispatch(Request("GetStatus")).Status!.Layers);
    }

    [Fact]
    public void GetStatus_ReportsTheAvailabilityOfEachLayerFromTheLastPass()
    {
        var (dispatcher, _) = Build();
        _layers.Record(
        [
            ReconcileResult.Unchanged("hosts"),
            ReconcileResult.Skipped("wfp", "wfp.engine-unavailable"),
        ]);

        var layers = dispatcher.Dispatch(Request("GetStatus")).Status!.Layers;

        Assert.Equal(["hosts", "wfp"], layers.Select(layer => layer.Name));
        Assert.True(layers[0].IsAvailable);
        Assert.False(layers[1].IsAvailable);
        Assert.Equal("wfp.engine-unavailable", layers[1].ReasonCode);
        Assert.Equal(Start, layers[1].ObservedAt);
    }

    [Fact]
    public void GetStatus_ListsTheRulesOfTheRunningSession()
    {
        var (dispatcher, _) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        var status = dispatcher.Dispatch(Request("GetStatus")).Status!;

        Assert.Equal(new SiteRuleMessage("example.com", true), Assert.Single(status.Sites));
        Assert.Equal(new AppRuleMessage(nameof(AppMatchKind.FileName), "steam.exe"), Assert.Single(status.Apps));
    }

    [Fact]
    public void GetStatus_ReportsWhenTheSessionStarted()
    {
        var (dispatcher, _) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        Assert.Equal(Start, dispatcher.Dispatch(Request("GetStatus")).Status!.StartedAt);
    }

    [Fact]
    public void GetStatus_ReportsTheCoolDownOfTheRunningSessionRatherThanTheConfiguredOne()
    {
        // A session keeps the cool-down it began with; changing the setting mid-session must not change the promised wait.
        var (dispatcher, _) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));
        _config.Save(_config.Load() with { CoolDownMinutes = 25 });

        Assert.Equal(5, dispatcher.Dispatch(Request("GetStatus")).Status!.CoolDownMinutes);
    }

    [Fact]
    public void GetStatus_FallsBackToTheConfiguredCoolDownWhenThereIsNoSession()
    {
        var (dispatcher, _) = Build();
        _config.Save(_config.Load() with { CoolDownMinutes = 25 });

        Assert.Equal(25, dispatcher.Dispatch(Request("GetStatus")).Status!.CoolDownMinutes);
    }

    [Fact]
    public void RejectsAnUnknownCommand()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("DeleteEverything"));

        Assert.False(response.Accepted);
        Assert.NotNull(response.Error);
    }

    [Fact]
    public void CarriesEngineWarningsBackToTheCaller()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("StartSession", minutes: 100000));

        Assert.True(response.Accepted);
        Assert.NotEmpty(response.Warnings);
    }

    [Fact]
    public void GetStatus_SortsTheRulesSoTheOrderIsAPropertyOfTheProtocol()
    {
        // BlockList is an ImmutableHashSet with arbitrary enumeration order; the dispatcher must sort so
        // clients do not depend on a coincidence.
        var (dispatcher, _) = Build();
        _config.Save(_config.Load() with
        {
            Sites = [new SiteRuleDto("zulip.com", false), new SiteRuleDto("anilist.co", true)],
            Apps =
            [
                new AppRuleDto(nameof(AppMatchKind.FileName), "steam.exe"),
                new AppRuleDto(nameof(AppMatchKind.FileName), "discord.exe"),
                new AppRuleDto(nameof(AppMatchKind.FullPath), @"C:\Games\dota.exe"),
            ],
        });
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        var status = dispatcher.Dispatch(Request("GetStatus")).Status!;

        Assert.Equal(["anilist.co", "zulip.com"], status.Sites.Select(site => site.Domain));
        Assert.Equal(
            [
                (nameof(AppMatchKind.FullPath), @"C:\Games\dota.exe"),
                (nameof(AppMatchKind.FileName), "discord.exe"),
                (nameof(AppMatchKind.FileName), "steam.exe"),
            ],
            status.Apps.Select(app => (app.MatchKind, app.Value)));
    }

    [Fact]
    public void GetStatus_KeepsTheOrderOfTheOtherRulesWhenOneIsAddedMidSession()
    {
        // A rule added mid-session takes its place in the order, so a list already on screen gains one row
        // instead of being rearranged.
        var (dispatcher, engine) = Build();
        _config.Save(_config.Load() with
        {
            Sites =
            [
                new SiteRuleDto("reddit.com", true),
                new SiteRuleDto("news.ycombinator.com", false),
                new SiteRuleDto("twitter.com", true),
            ],
        });
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        engine.AddSiteRule(new SiteRule("soundcloud.com", includeSubdomains: false));

        var after = dispatcher.Dispatch(Request("GetStatus")).Status!.Sites.Select(site => site.Domain);

        Assert.Equal(["news.ycombinator.com", "reddit.com", "soundcloud.com", "twitter.com"], after);
    }

    [Fact]
    public void GetStatus_ReportsTheSessionEndedOnceItsEndTimeHasPassed()
    {
        // CurrentStatus must advance the engine, or a status request could report Active with Now past
        // EndsAt. Bounded by the fifteen-second reconcile period, but the interface has no other source.
        var (dispatcher, _) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        _clock.Advance(TimeSpan.FromMinutes(90));

        Assert.Equal(nameof(SessionState.Ended), dispatcher.Dispatch(Request("GetStatus")).Status!.State);
    }

    [Fact]
    public void GetStatus_ReportsTheUnlockElapsedOnceItsDeadlineHasPassed()
    {
        var (dispatcher, _) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));
        dispatcher.Dispatch(Request("RequestUnlock"));

        _clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(nameof(SessionState.Ended), dispatcher.Dispatch(Request("GetStatus")).Status!.State);
    }

    [Fact]
    public void StatusReportsTheUnlockDeadline()
    {
        var (dispatcher, _) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));
        dispatcher.Dispatch(Request("RequestUnlock"));

        var status = dispatcher.Dispatch(Request("GetStatus")).Status!;

        Assert.Equal(nameof(SessionState.UnlockPending), status.State);
        Assert.Equal(Start.AddMinutes(5), status.UnlockEffectiveAt);
    }

    [Fact]
    public void GetConfig_ReportsTheStoredRulesAndSettings()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("GetConfig"));

        Assert.True(response.Accepted);
        var config = response.Config!;
        Assert.Equal(new SiteRuleMessage("example.com", true), Assert.Single(config.Sites));
        Assert.Equal(new AppRuleMessage(nameof(AppMatchKind.FileName), "steam.exe"), Assert.Single(config.Apps));
        Assert.Equal(5, config.CoolDownMinutes);
        Assert.Equal(90, config.DefaultSessionMinutes);
        Assert.True(config.WfpEnabled);
    }

    [Fact]
    public void AddSiteRule_WhileIdle_ReachesTheConfigurationAndThereIsNoSnapshotToReach()
    {
        var (dispatcher, engine) = Build();

        var response = dispatcher.Dispatch(Request("AddSiteRule", site: new SiteRuleMessage("reddit.com", true)));

        Assert.True(response.Accepted);
        Assert.Contains(_config.Load().Sites, rule => rule.Domain == "reddit.com");
        Assert.Null(engine.Session);
    }

    [Fact]
    public void AddSiteRule_DuringASession_ReachesTheSnapshotAndTheConfiguration()
    {
        var (dispatcher, engine) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        var response = dispatcher.Dispatch(Request("AddSiteRule", site: new SiteRuleMessage("reddit.com", true)));

        Assert.True(response.Accepted);
        Assert.Contains(engine.Session!.Rules.Sites, rule => rule.Domain == "reddit.com");
        Assert.Contains(_config.Load().Sites, rule => rule.Domain == "reddit.com");
    }

    [Fact]
    public void AddAppRule_DuringASession_ReachesTheSnapshotAndTheConfiguration()
    {
        var (dispatcher, engine) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        var response = dispatcher.Dispatch(Request(
            "AddAppRule",
            app: new AppRuleMessage(nameof(AppMatchKind.FileName), "discord.exe")));

        Assert.True(response.Accepted);
        Assert.Contains(engine.Session!.Rules.Apps, rule => rule.Value == "discord.exe");
        Assert.Contains(_config.Load().Apps, rule => rule.Value == "discord.exe");
    }

    [Fact]
    public void AddAppRule_WhileIdle_RefusesAProtectedApplication()
    {
        // The double check. With no session the engine refuses on state alone, so its own protection check
        // never runs and the dispatcher must ask the policy itself, or the configuration accepts
        // explorer.exe and only the watchdog stands between the next session and a machine with no shell.
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request(
            "AddAppRule",
            app: new AppRuleMessage(nameof(AppMatchKind.FileName), "explorer.exe")));

        Assert.False(response.Accepted);
        Assert.NotNull(response.Error);
        Assert.DoesNotContain(_config.Load().Apps, rule => rule.Value == "explorer.exe");
    }

    [Fact]
    public void AddAppRule_DuringASession_RefusesAProtectedApplication()
    {
        var (dispatcher, engine) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        var response = dispatcher.Dispatch(Request(
            "AddAppRule",
            app: new AppRuleMessage(nameof(AppMatchKind.FullPath), @"C:\Windows\System32\notepad.exe")));

        Assert.False(response.Accepted);
        Assert.DoesNotContain(
            engine.Session!.Rules.Apps,
            rule => rule.Value.Contains("notepad", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            _config.Load().Apps,
            rule => rule.Value.Contains("notepad", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("7")]
    [InlineData("Registry")]
    [InlineData("")]
    public void AddAppRule_RefusesAMatchKindItDoesNotKnow(string kind)
    {
        // Enum.TryParse accepts any number, so "7" would pass as a kind and turn a path rule into a name rule.
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("AddAppRule", app: new AppRuleMessage(kind, "discord.exe")));

        Assert.False(response.Accepted);
        Assert.NotNull(response.Error);
        Assert.Single(_config.Load().Apps);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddSiteRule_RefusesABlankDomain(string domain)
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("AddSiteRule", site: new SiteRuleMessage(domain, true)));

        Assert.False(response.Accepted);
        Assert.Single(_config.Load().Sites);
    }

    [Fact]
    public void AddSiteRule_RefusesARequestWithNoRuleInIt()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("AddSiteRule"));

        Assert.False(response.Accepted);
        Assert.NotNull(response.Error);
    }

    [Fact]
    public void AddSiteRule_TwiceStoresTheRuleOnce()
    {
        var (dispatcher, _) = Build();
        var rule = new SiteRuleMessage("reddit.com", true);

        Assert.True(dispatcher.Dispatch(Request("AddSiteRule", site: rule)).Accepted);
        Assert.True(dispatcher.Dispatch(Request("AddSiteRule", site: rule)).Accepted);

        Assert.Single(_config.Load().Sites, stored => stored.Domain == "reddit.com");
    }

    [Fact]
    public void RemoveSiteRule_WhileIdle_TakesTheRuleOutOfTheConfiguration()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("RemoveSiteRule", site: new SiteRuleMessage("example.com", true)));

        Assert.True(response.Accepted);
        Assert.Empty(response.Warnings);
        Assert.DoesNotContain(_config.Load().Sites, rule => rule.Domain == "example.com");
    }

    [Fact]
    public void RemoveSiteRule_DuringASession_LeavesTheSnapshotAloneAndWarns()
    {
        // The removal is accepted, but it cannot loosen a block already in force. The warning becomes
        // "this takes effect from the next session" in the interface.
        var (dispatcher, engine) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        var response = dispatcher.Dispatch(Request("RemoveSiteRule", site: new SiteRuleMessage("example.com", true)));

        Assert.True(response.Accepted);
        Assert.NotEmpty(response.Warnings);
        Assert.Contains(engine.Session!.Rules.Sites, rule => rule.Domain == "example.com");
        Assert.Contains(response.Status!.Sites, rule => rule.Domain == "example.com");
        Assert.DoesNotContain(response.Config!.Sites, rule => rule.Domain == "example.com");
        Assert.DoesNotContain(_config.Load().Sites, rule => rule.Domain == "example.com");
    }

    [Fact]
    public void RemoveAppRule_DuringASession_LeavesTheSnapshotAloneAndWarns()
    {
        var (dispatcher, engine) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        var response = dispatcher.Dispatch(Request(
            "RemoveAppRule",
            app: new AppRuleMessage(nameof(AppMatchKind.FileName), "steam.exe")));

        Assert.True(response.Accepted);
        Assert.NotEmpty(response.Warnings);
        Assert.Contains(engine.Session!.Rules.Apps, rule => rule.Value == "steam.exe");
        Assert.Contains(response.Status!.Apps, rule => rule.Value == "steam.exe");
        Assert.DoesNotContain(_config.Load().Apps, rule => rule.Value == "steam.exe");
    }

    [Fact]
    public void RemoveSiteRule_MatchesARuleThatDiffersOnlyByCaseAndSpacing()
    {
        // SiteRule normalises on construction, so both sides need it: the request, because it arrives as
        // typed, and the stored rule, because a hand-edited file may be untidy.
        var (dispatcher, _) = Build();
        _config.Save(_config.Load() with { Sites = [new SiteRuleDto("  ExAmPle.CoM. ", true)] });

        var response = dispatcher.Dispatch(Request(
            "RemoveSiteRule",
            site: new SiteRuleMessage(" example.COM ", true)));

        Assert.True(response.Accepted);
        Assert.Empty(_config.Load().Sites);
    }

    [Fact]
    public void RemoveAppRule_MatchesARuleThatDiffersOnlyByCaseAndSpacing()
    {
        var (dispatcher, _) = Build();
        _config.Save(_config.Load() with { Apps = [new AppRuleDto("FileName", "  Steam.EXE ")] });

        var response = dispatcher.Dispatch(Request("RemoveAppRule", app: new AppRuleMessage("filename", " STEAM.exe ")));

        Assert.True(response.Accepted);
        Assert.Empty(_config.Load().Apps);
    }

    [Fact]
    public void RemoveSiteRule_RefusesARuleTheConfigurationDoesNotHave()
    {
        // A command that arrived deserves an answer; accepting silently would leave a client showing a stale list.
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request(
            "RemoveSiteRule",
            site: new SiteRuleMessage("nowhere.example", true)));

        Assert.False(response.Accepted);
        Assert.NotNull(response.Error);
        Assert.Single(_config.Load().Sites);
    }

    [Fact]
    public void AddSiteRule_AfterTheSessionHasEndedIsRefused()
    {
        // Ended means the layers still hold the block and the loop has not cleared them. A rule added now
        // would belong to neither the finished session nor the next one.
        var (dispatcher, _) = Build();
        RunTheSessionOut(dispatcher);

        var response = dispatcher.Dispatch(Request("AddSiteRule", site: new SiteRuleMessage("reddit.com", true)));

        Assert.False(response.Accepted);
        Assert.NotNull(response.Error);
        Assert.DoesNotContain(_config.Load().Sites, rule => rule.Domain == "reddit.com");
    }

    [Fact]
    public void AddSiteRule_AfterTheEndTimeHasPassedIsRefusedWithoutWaitingForTheLoop()
    {
        // The end time passing ends a session; the reconcile loop only notices. Without advancing the
        // engine first, a rule could be added to a finished session for up to fifteen seconds.
        var (dispatcher, _) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        _clock.Advance(TimeSpan.FromMinutes(90));
        var response = dispatcher.Dispatch(Request("AddSiteRule", site: new SiteRuleMessage("reddit.com", true)));

        Assert.False(response.Accepted);
        Assert.DoesNotContain(_config.Load().Sites, rule => rule.Domain == "reddit.com");
    }

    [Fact]
    public void RemoveSiteRule_AfterTheSessionHasEndedIsRefused()
    {
        var (dispatcher, _) = Build();
        RunTheSessionOut(dispatcher);

        var response = dispatcher.Dispatch(Request("RemoveSiteRule", site: new SiteRuleMessage("example.com", true)));

        Assert.False(response.Accepted);
        Assert.Contains(_config.Load().Sites, rule => rule.Domain == "example.com");
    }

    [Fact]
    public void UpdateSettings_ClampsOutOfRangeValuesAndSaysSo()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request(
            "UpdateSettings",
            settings: new SettingsMessage(CoolDownMinutes: 9000, DefaultSessionMinutes: 1)));

        Assert.True(response.Accepted);
        Assert.Equal(2, response.Warnings.Count);
        Assert.Equal(60, _config.Load().CoolDownMinutes);
        Assert.Equal(5, _config.Load().DefaultSessionMinutes);
        Assert.Equal(60, response.Config!.CoolDownMinutes);
    }

    [Fact]
    public void UpdateSettings_ChangesOnlyTheFieldsItWasGiven()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("UpdateSettings", settings: new SettingsMessage(VerboseLogging: true)));

        Assert.True(response.Accepted);
        var stored = _config.Load();
        Assert.True(stored.VerboseLogging);
        Assert.Equal(5, stored.CoolDownMinutes);
        Assert.Equal(90, stored.DefaultSessionMinutes);
        Assert.Equal("system", stored.Language);
        Assert.True(stored.WfpEnabled);
        Assert.Single(stored.Sites);
    }

    [Fact]
    public void UpdateSettings_RefusesABlankLanguage()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("UpdateSettings", settings: new SettingsMessage(Language: "  ")));

        Assert.False(response.Accepted);
        Assert.Equal("system", _config.Load().Language);
    }

    [Fact]
    public void UpdateSettings_DuringASession_LeavesTheRunningSessionsCoolDownAlone()
    {
        var (dispatcher, engine) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        var response = dispatcher.Dispatch(Request("UpdateSettings", settings: new SettingsMessage(CoolDownMinutes: 30)));

        Assert.True(response.Accepted);
        Assert.Equal(TimeSpan.FromMinutes(5), engine.Session!.CoolDown);
        Assert.Equal(5, response.Status!.CoolDownMinutes);
        Assert.Equal(30, response.Config!.CoolDownMinutes);
        Assert.NotEmpty(response.Warnings);
    }

    [Fact]
    public void UpdateSettings_IsAcceptedAfterTheSessionHasEnded()
    {
        // Unlike rules, settings belong to the next session and cannot contradict the finished one.
        var (dispatcher, _) = Build();
        RunTheSessionOut(dispatcher);

        var response = dispatcher.Dispatch(Request("UpdateSettings", settings: new SettingsMessage(WfpEnabled: false)));

        Assert.True(response.Accepted);
        Assert.False(_config.Load().WfpEnabled);
    }

    [Fact]
    public void Subscribe_IsAnsweredWithTheCurrentStatus()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request(IpcProtocol.SubscribeCommand));

        // The acknowledgement is the first frame of the stream, leaving no gap between subscribing and knowing the state.
        Assert.True(response.Accepted);
        Assert.Equal(nameof(SessionState.Idle), response.Status!.State);
    }

    [Fact]
    public void AnInterfaceFromBeforeSiteNoticesIsRefusedWithTheVersionItShouldSpeak()
    {
        var (dispatcher, _) = Build();

        // Version 2 has no SiteBlocked; its subscription is refused, not fed events it cannot read.
        var response = dispatcher.Dispatch(new IpcRequest
        {
            ProtocolVersion = 2,
            Command = IpcProtocol.SubscribeCommand,
        });

        Assert.False(response.Accepted);
        Assert.Contains("version 4", response.Error!, StringComparison.Ordinal);
    }

    /// <summary>A version 3 client shows Error as a sentence and reads Warnings as strings, so its refusal is a sentence, not a code.</summary>
    [Fact]
    public void AnInterfaceFromBeforeCodesIsRefusedInWordsItCanRead()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(new IpcRequest { ProtocolVersion = 3, Command = "GetStatus" });

        var json = System.Text.Json.JsonSerializer.Serialize(response, IpcJson.Options);
        var read = System.Text.Json.JsonSerializer.Deserialize<VersionThreeResponse>(json, IpcJson.Options)!;

        Assert.False(read.Accepted);
        Assert.Empty(read.Warnings);
        Assert.Equal(
            "Unsupported protocol version 3; this service speaks version 4.",
            read.Error);
    }

    /// <summary>The answer as a version 3 client declared it.</summary>
    private sealed record VersionThreeResponse(bool Accepted, string? Error, IReadOnlyList<string> Warnings);

    [Fact]
    public void AnUnknownCommandIsRefusedWithACode()
    {
        var (dispatcher, _) = Build();

        Assert.Equal(IpcCodes.CommandUnknown, dispatcher.Dispatch(Request("DeleteEverything")).Error);
    }

    [Fact]
    public void AnEngineRefusalGoesBackAsTheEnginesCode()
    {
        var (dispatcher, _) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        Assert.Equal(IpcCodes.SessionStartWrongState, dispatcher.Dispatch(Request("StartSession", minutes: 60)).Error);
    }

    [Fact]
    public void CancellingAnUnlockNobodyAskedForIsRefusedWithTheEnginesCode()
    {
        var (dispatcher, _) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        Assert.Equal(IpcCodes.UnlockCancelWrongState, dispatcher.Dispatch(Request("CancelUnlock")).Error);
    }

    /// <summary>Only protected applications configured: the engine refuses and the warnings explaining why come back with it.</summary>
    [Fact]
    public void AnEngineRefusalKeepsTheWarningsThatExplainIt()
    {
        var (dispatcher, _) = Build();
        _config.Save(_config.Load() with { Sites = [], Apps = [new AppRuleDto("FileName", "explorer.exe")] });

        var response = dispatcher.Dispatch(Request("StartSession", minutes: 60));

        Assert.Equal(IpcCodes.SessionEmptyBlockList, response.Error);
        var notice = Assert.Single(response.Warnings);
        Assert.Equal(IpcCodes.SessionProtectedAppSkipped, notice.Code);
        Assert.Equal(["explorer.exe"], notice.Arguments);
    }

    [Fact]
    public void AnEngineWarningGoesBackAsItsCodeAndMinutes()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("StartSession", minutes: 100000));

        var notice = Assert.Single(response.Warnings);
        Assert.Equal(IpcCodes.SessionDurationClamped, notice.Code);
        Assert.Equal(["1440"], notice.Arguments);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(0)]
    public void ExtendingByNothingIsRefusedWithACode(int minutes)
    {
        var (dispatcher, _) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        Assert.Equal(IpcCodes.SessionExtendNeedsMinutes, dispatcher.Dispatch(Request("ExtendSession", minutes: minutes)).Error);
    }

    [Fact]
    public void ExtendingWithNoSessionIsRefusedWithACode()
    {
        var (dispatcher, _) = Build();

        Assert.Equal(IpcCodes.SessionNoneToExtend, dispatcher.Dispatch(Request("ExtendSession", minutes: 30)).Error);
    }

    [Fact]
    public void ARuleCommandWithNoSiteIsRefusedWithACode()
    {
        var (dispatcher, _) = Build();

        Assert.Equal(IpcCodes.RulesSiteMissing, dispatcher.Dispatch(Request("AddSiteRule")).Error);
    }

    [Fact]
    public void ASiteWithNoDomainIsRefusedWithACode()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("AddSiteRule", site: new SiteRuleMessage(" ", true)));

        Assert.Equal(IpcCodes.RulesSiteDomainBlank, response.Error);
    }

    /// <summary>The code alone: the domain that was not found stays out of the answer.</summary>
    [Fact]
    public void RemovingASiteTheConfigurationDoesNotHaveIsRefusedWithACodeThatNamesNoDomain()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("RemoveSiteRule", site: new SiteRuleMessage("nowhere.example", true)));

        Assert.Equal(IpcCodes.RulesSiteNotFound, response.Error);
    }

    [Fact]
    public void ARuleCommandWithNoAppIsRefusedWithACode()
    {
        var (dispatcher, _) = Build();

        Assert.Equal(IpcCodes.RulesAppMissing, dispatcher.Dispatch(Request("AddAppRule")).Error);
    }

    [Fact]
    public void AnAppOfAKindTheServiceDoesNotKnowIsRefusedWithACode()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("AddAppRule", app: new AppRuleMessage("Registry", "discord.exe")));

        Assert.Equal(IpcCodes.RulesAppKindUnknown, response.Error);
    }

    [Fact]
    public void AnAppWithNoValueIsRefusedWithACode()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("AddAppRule", app: new AppRuleMessage("FileName", " ")));

        Assert.Equal(IpcCodes.RulesAppValueBlank, response.Error);
    }

    [Fact]
    public void RemovingAnAppTheConfigurationDoesNotHaveIsRefusedWithACode()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("RemoveAppRule", app: new AppRuleMessage("FileName", "nothing.exe")));

        Assert.Equal(IpcCodes.RulesAppNotFound, response.Error);
    }

    /// <summary>The policy's own code goes back, and the path it refused does not.</summary>
    [Fact]
    public void AProtectedAppIsRefusedWithThePolicysCodeAndNotItsPath()
    {
        var (dispatcher, _) = Build();

        var byName = dispatcher.Dispatch(Request("AddAppRule", app: new AppRuleMessage("FileName", "explorer.exe")));
        var byPlace = dispatcher.Dispatch(Request(
            "AddAppRule",
            app: new AppRuleMessage("FullPath", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "solitaire.exe"))));

        Assert.Equal(IpcCodes.RulesAppProtectedSystemFile, byName.Error);
        Assert.Equal(IpcCodes.RulesAppProtectedSystemDirectory, byPlace.Error);
    }

    [Fact]
    public void AProtectedAppWhosePolicyGaveNoReasonIsRefusedWithTheGeneralCode()
    {
        var (dispatcher, _) = Build(new SilentPolicy());

        var response = dispatcher.Dispatch(Request("AddAppRule", app: new AppRuleMessage("FileName", "game.exe")));

        Assert.Equal(IpcCodes.RulesAppProtected, response.Error);
    }

    /// <summary>Protects everything and says nothing about why.</summary>
    private sealed class SilentPolicy : IProtectedAppPolicy
    {
        public ProtectionVerdict Evaluate(AppRule rule) => new(true, null);
    }

    [Fact]
    public void ChangingRulesWhileASessionIsBeingClearedIsRefusedWithACode()
    {
        var (dispatcher, _) = Build();
        RunTheSessionOut(dispatcher);

        var response = dispatcher.Dispatch(Request("AddSiteRule", site: new SiteRuleMessage("reddit.com", true)));

        Assert.Equal(IpcCodes.RulesFrozen, response.Error);
    }

    [Theory]
    [InlineData("RemoveSiteRule")]
    [InlineData("RemoveAppRule")]
    public void ARemovalDuringASessionWarnsWithACode(string command)
    {
        var (dispatcher, _) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        var response = dispatcher.Dispatch(Request(
            command,
            site: new SiteRuleMessage("example.com", true),
            app: new AppRuleMessage("FileName", "steam.exe")));

        var notice = Assert.Single(response.Warnings);
        Assert.Equal(IpcCodes.RulesRemovalWaitsForNextSession, notice.Code);
        Assert.Empty(notice.Arguments);
    }

    [Fact]
    public void SettingsWithNoBlockAreRefusedWithACode()
    {
        var (dispatcher, _) = Build();

        Assert.Equal(IpcCodes.ConfigSettingsMissing, dispatcher.Dispatch(Request("UpdateSettings")).Error);
    }

    [Fact]
    public void ABlankLanguageIsRefusedWithACode()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("UpdateSettings", settings: new SettingsMessage(Language: " ")));

        Assert.Equal(IpcCodes.ConfigLanguageBlank, response.Error);
    }

    /// <summary>The clamped value rides as minutes, invariant.</summary>
    [Fact]
    public void ClampedSettingsWarnWithTheirCodesAndMinutes()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request(
            "UpdateSettings",
            settings: new SettingsMessage(CoolDownMinutes: 9000, DefaultSessionMinutes: 1)));

        Assert.Collection(
            response.Warnings,
            first =>
            {
                Assert.Equal(IpcCodes.ConfigCoolDownClamped, first.Code);
                Assert.Equal(["60"], first.Arguments);
            },
            second =>
            {
                Assert.Equal(IpcCodes.ConfigSessionDurationClamped, second.Code);
                Assert.Equal(["5"], second.Arguments);
            });
    }

    [Fact]
    public void ACoolDownChangedDuringASessionWarnsWithACode()
    {
        var (dispatcher, _) = Build();
        dispatcher.Dispatch(Request("StartSession", minutes: 60));

        var response = dispatcher.Dispatch(Request("UpdateSettings", settings: new SettingsMessage(CoolDownMinutes: 30)));

        var notice = Assert.Single(response.Warnings);
        Assert.Equal(IpcCodes.ConfigCoolDownAppliesNextSession, notice.Code);
    }

    [Fact]
    public void Subscribe_IsRefusedForAProtocolVersionThisServiceDoesNotSpeak()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(new IpcRequest
        {
            ProtocolVersion = IpcProtocol.Version + 1,
            Command = IpcProtocol.SubscribeCommand,
        });

        // Subscribing goes through the dispatcher so the one version check covers it too.
        Assert.False(response.Accepted);
    }

    [Fact]
    public void Dispatch_PublishesTheNewStatusAfterAnAcceptedCommand()
    {
        var (dispatcher, _) = Build();
        using var subscription = _events.Subscribe(out var published);

        Assert.True(dispatcher.Dispatch(Request("StartSession", minutes: 60)).Accepted);

        Assert.True(published.TryRead(out var one));
        Assert.Equal(IpcEventKind.StatusChanged, one!.Kind);
        Assert.Equal(nameof(SessionState.Active), one.Status!.State);
        Assert.False(published.TryRead(out _));
    }

    [Fact]
    public void Dispatch_PublishesTheNewStatusAfterAnAcceptedConfigurationChange()
    {
        var (dispatcher, _) = Build();
        using var subscription = _events.Subscribe(out var published);

        Assert.True(dispatcher.Dispatch(Request("AddSiteRule", site: new SiteRuleMessage("reddit.com", true))).Accepted);

        Assert.True(published.TryRead(out var one));
        Assert.Equal(IpcEventKind.StatusChanged, one!.Kind);
    }

    [Fact]
    public void Dispatch_PublishesNothingForACommandThatOnlyReads()
    {
        var (dispatcher, _) = Build();
        using var subscription = _events.Subscribe(out var published);

        dispatcher.Dispatch(Request("GetStatus"));
        dispatcher.Dispatch(Request("GetConfig"));
        dispatcher.Dispatch(Request(IpcProtocol.SubscribeCommand));

        // A question is not a change: an interface that polls must not make other clients redraw once per poll.
        Assert.False(published.TryRead(out _));
    }

    [Fact]
    public void Dispatch_PublishesNothingWhenTheCommandIsRefused()
    {
        var (dispatcher, _) = Build();
        using var subscription = _events.Subscribe(out var published);

        Assert.False(dispatcher.Dispatch(Request("ExtendSession", minutes: 10)).Accepted);

        Assert.False(published.TryRead(out _));
    }

    [Fact]
    public void UpdateSettings_RefusesARequestWithNoSettingsInIt()
    {
        var (dispatcher, _) = Build();

        var response = dispatcher.Dispatch(Request("UpdateSettings"));

        Assert.False(response.Accepted);
        Assert.NotNull(response.Error);
    }
}
