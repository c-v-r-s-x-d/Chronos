using System.Text.RegularExpressions;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.Time;
using Chronos.App.ViewModels;
using Chronos.Core.Rules;
using Chronos.Ipc;
using Chronos.Service.Rules;

namespace Chronos.App.Tests;

/// <summary>Editing the lists, and saying why removal is unavailable during a session instead of hiding it.</summary>
[Collection(CultureBound.Name)]
public sealed class RuleEditingTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Moment = ServiceHarness.Moment;

    private readonly ServiceHarness _service = new();

    private readonly RecordedWaits _waits = new();

    private readonly LanguageSwitch _language = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _service.DisposeAsync().AsTask();

    [Fact]
    public async Task DuringASession_RemovingARuleIsUnavailableAndSaysWhy()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(50)));

        var screen = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);
        await screen.Loading;

        Assert.False(screen.CanRemoveRules);
        Assert.False(string.IsNullOrWhiteSpace(screen.RemovalUnavailable));

        await screen.RemoveSiteCommand.ExecuteAsync(new SiteRuleMessage("blocked.example", true));
        await screen.RemoveAppCommand.ExecuteAsync(new AppRuleMessage("FileName", "game.exe"));

        Assert.DoesNotContain(app.Link.Sent, request => request.Command == "RemoveSiteRule");
        Assert.DoesNotContain(app.Link.Sent, request => request.Command == "RemoveAppRule");
    }

    [Fact]
    public async Task WithNoSessionRunning_ARuleCanBeRemoved()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Idle", Moment));

        var screen = Assert.IsType<SetupViewModel>(app.Shell.CurrentScreen);
        await screen.Loading;

        Assert.True(screen.CanRemoveRules);

        await screen.RemoveSiteCommand.ExecuteAsync(new SiteRuleMessage("blocked.example", true));

        Assert.Contains(app.Link.Sent, request => request.Command == "RemoveSiteRule");
    }

    /// <summary>"At once" means the rule reaches the snapshot the session is enforced from, not only the next configuration.</summary>
    [Fact]
    public async Task DuringASession_AddingARuleWorksAndTakesEffectAtOnce()
    {
        const string domain = "added-mid-session.example";

        var (link, text, running) = await StartedSessionAsync();
        await using var owned = link;

        using var screen = Running(link, text, running);
        await screen.Loading;

        screen.NewSiteDomain = domain;
        await screen.AddSiteCommand.ExecuteAsync(null);

        Assert.Contains(screen.Sites, site => site.Domain == domain);

        // The session is already running, so this is a block and not a note for later.
        await LinkWait.ForAsync(
            link,
            snapshot => snapshot.Status?.Sites.Any(site => site.Domain == domain) == true,
            "saw the rule reach the running session");
    }

    [Fact]
    public async Task DuringASession_TheSessionCanBeExtendedButNotShortened()
    {
        var (link, text, running) = await StartedSessionAsync();
        await using var owned = link;

        using var screen = Running(link, text, running);
        await screen.Loading;

        Assert.False(string.IsNullOrWhiteSpace(screen.ShorteningUnavailable));

        var before = running.Status!.EndsAt;
        Assert.NotNull(before);

        screen.ExtendMinutes = 15;
        await screen.ExtendCommand.ExecuteAsync(null);

        var after = await LinkWait.ForAsync(
            link, snapshot => snapshot.Status?.EndsAt > before, "saw the session get longer");

        Assert.Equal(before!.Value.AddMinutes(15), after.Status!.EndsAt);

        // Nothing this screen offers can send a shortening, whatever is in the box.
        screen.ExtendMinutes = -30;
        await screen.ExtendCommand.ExecuteAsync(null);

        Assert.Equal(after.Status!.EndsAt, link.Snapshot.Status!.EndsAt);
    }

    /// <summary>The interface's own check refuses before anything is sent; the service's answer is the one that counts.</summary>
    [Fact]
    public async Task AProtectedApplicationIsRefusedByTheInterfaceBeforeAnythingIsSent()
    {
        var link = new RecordingLink();
        using var screen = new SetupViewModel(link, new Text(_language), new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));

        await screen.AddAppRuleAsync(new AppRuleMessage("FileName", "explorer.exe"));

        Assert.Empty(link.Sent);
        Assert.Contains("explorer.exe", screen.Notice, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The interface lets it through and the service refuses; the code reaches the user in this interface's words.</summary>
    [Fact]
    public async Task AProtectedApplicationIsRefusedWithTheReasonFromTheService()
    {
        var rule = new AppRuleMessage(
            "FullPath",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "solitaire.exe"));

        // Premise: this rule is past the interface's own check, so what follows is the service's doing.
        Assert.Null(ProtectedApps.Refuses(rule));

        var reason = new WindowsProtectedAppPolicy()
            .Evaluate(new AppRule(AppMatchKind.FullPath, rule.Value))
            .Reason;

        Assert.False(string.IsNullOrWhiteSpace(reason));

        _service.Start();
        await using var link = new ServiceLink(_service.PipeName, _waits.NoWaitAsync);
        link.Start();

        var idle = await LinkWait.ForAsync(
            link, static snapshot => snapshot.Status is not null, "reached the service");

        using var screen = new SetupViewModel(link, new Text(_language), new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));
        screen.Show(idle.Status!);
        await screen.Loading;

        await screen.AddAppRuleAsync(rule);

        Assert.Equal(IpcCodes.RulesAppProtectedSystemDirectory, reason);
        Assert.Equal(ServiceNotice.Text(reason), screen.Notice);
        Assert.NotEqual(reason, screen.Notice);
        Assert.DoesNotContain(screen.Apps, app => app.Value == rule.Value);
    }

    /// <summary>The service sends warnings as codes; the words are this interface's.</summary>
    [Fact]
    public async Task WarningsFromTheServiceAreShownRatherThanSwallowed()
    {
        var link = new RecordingLink
        {
            Answer = request => request.Command == "RemoveSiteRule"
                ? IpcResponse.Ok(
                    Say.Status("Idle", Moment),
                    Say.Config(),
                    [new IpcNotice(IpcCodes.RulesRemovalWaitsForNextSession, [])])
                : IpcResponse.Ok(Say.Status("Idle", Moment), Say.Config(sites: ["one.example"])),
        };

        using var screen = new SetupViewModel(link, new Text(_language), new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));
        screen.Show(Say.Status("Idle", Moment));
        await screen.Loading;

        Assert.Empty(screen.Notice);

        await screen.RemoveSiteCommand.ExecuteAsync(new SiteRuleMessage("one.example", true));

        Assert.Equal(ServiceNotice.Text(IpcCodes.RulesRemovalWaitsForNextSession), screen.Notice);
        Assert.NotEqual(IpcCodes.RulesRemovalWaitsForNextSession, screen.Notice);
    }

    /// <summary>A configuration that lost an element on the wire is not shown; a null rule would throw on the interface thread.</summary>
    [Fact]
    public async Task AConfigurationWithAMissingRuleIsNotTakenAndTheLastWholeOneStays()
    {
        var broken = Say.Config(sites: ["kept.example"]) with { Sites = [new SiteRuleMessage("added.example", true), null!] };
        var link = new RecordingLink
        {
            Answer = request => request.Command == "AddSiteRule"
                ? IpcResponse.Ok(Say.Status("Idle", Moment), broken)
                : IpcResponse.Ok(Say.Status("Idle", Moment), Say.Config(sites: ["kept.example"])),
        };

        using var screen = new SetupViewModel(link, new Text(_language), new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));
        screen.Show(Say.Status("Idle", Moment));
        await screen.Loading;

        screen.NewSiteDomain = "added.example";
        await screen.AddSiteCommand.ExecuteAsync(null);

        var site = Assert.Single(screen.Sites);
        Assert.Equal("kept.example", site.Domain);
    }

    [Fact]
    public async Task AConfigurationWithoutItsListsIsNotTakenOnTheFirstRead()
    {
        var link = new RecordingLink
        {
            Answer = _ => IpcResponse.Ok(Say.Status("Idle", Moment), Say.Config() with { Apps = null! }),
        };

        using var screen = new SetupViewModel(link, new Text(_language), new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));
        screen.Show(Say.Status("Idle", Moment));
        await screen.Loading;

        Assert.Empty(screen.Sites);
        Assert.Empty(screen.Apps);
    }

    [Fact]
    public async Task ARefusalIsShownWithTheWarningsThatCameWithIt()
    {
        using var english = new UiCulture("en-US");

        var link = new RecordingLink
        {
            Answer = _ => new IpcResponse
            {
                Accepted = false,
                Error = IpcCodes.SessionEmptyBlockList,
                Warnings = [new IpcNotice(IpcCodes.SessionDurationClamped, ["5"])],
            },
        };

        using var screen = new SetupViewModel(link, new Text(_language), new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));
        await screen.AddAppRuleAsync(new AppRuleMessage("FileName", "game.exe"));

        Assert.Equal(
            ServiceNotice.Text(IpcCodes.SessionEmptyBlockList)
                + Environment.NewLine
                + ServiceNotice.Text(IpcCodes.SessionDurationClamped, ["5"]),
            screen.Notice);
    }

    /// <summary>The wire can carry <c>"warnings": null</c>; it reads as a refusal with nothing to add.</summary>
    [Fact]
    public async Task ARefusalWhoseWarningsAreNullIsShownWithoutThem()
    {
        using var english = new UiCulture("en-US");

        var link = new RecordingLink
        {
            Answer = _ => new IpcResponse
            {
                Accepted = false,
                Error = IpcCodes.SessionEmptyBlockList,
                Warnings = null!,
            },
        };

        using var screen = new SetupViewModel(link, new Text(_language), new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));
        await screen.AddAppRuleAsync(new AppRuleMessage("FileName", "game.exe"));

        Assert.Equal(ServiceNotice.Text(IpcCodes.SessionEmptyBlockList), screen.Notice);
    }

    /// <summary>Answers are kept as codes and worded on read, so changing language rewrites them.</summary>
    [Fact]
    public async Task ARefusalFromTheServiceIsRewrittenWhenTheLanguageChanges()
    {
        using var english = new UiCulture("en-US");

        var link = new RecordingLink { Answer = _ => IpcResponse.Fail(IpcCodes.RulesFrozen) };

        using var screen = new SetupViewModel(link, new Text(_language), new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));
        await screen.AddAppRuleAsync(new AppRuleMessage("FileName", "game.exe"));

        var before = screen.Notice;
        _language.Use("ru-RU");

        Assert.Equal(ServiceNotice.Text(IpcCodes.RulesFrozen), screen.Notice);
        Assert.NotEqual(before, screen.Notice);
    }

    [Fact]
    public async Task AWarningWithNoCodeIsSkippedRatherThanThrown()
    {
        var link = new RecordingLink
        {
            Answer = _ => IpcResponse.Ok(
                Say.Status("Idle", Moment),
                Say.Config(),
                [new IpcNotice(null!, null!), null!, new IpcNotice(IpcCodes.RulesFrozen, [])]),
        };

        using var screen = new SetupViewModel(link, new Text(_language), new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));
        await screen.AddAppRuleAsync(new AppRuleMessage("FileName", "game.exe"));

        Assert.Equal(ServiceNotice.Text(IpcCodes.RulesFrozen), screen.Notice);
    }

    [Fact]
    public async Task ARefusalThisInterfaceCannotWordIsShownAsItsCode()
    {
        var link = new RecordingLink { Answer = _ => IpcResponse.Fail("rules.too-many") };

        using var screen = new SetupViewModel(link, new Text(_language), new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));
        await screen.AddAppRuleAsync(new AppRuleMessage("FileName", "game.exe"));

        Assert.Equal("rules.too-many", screen.Notice);
    }

    /// <summary>A session starts under a settings screen drawn from a status with none. Accepted is not refused; the warning says the block has not moved.</summary>
    [Fact]
    public async Task ARemovalTheServiceAcceptsWithAWarningIsNotTreatedAsAFailure()
    {
        _service.Start();
        await using var link = new ServiceLink(_service.PipeName, _waits.NoWaitAsync);
        link.Start();

        var idle = await LinkWait.ForAsync(
            link, static snapshot => snapshot.Status is not null, "reached the service");

        using var screen = new SetupViewModel(link, new Text(_language), new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));
        screen.Show(idle.Status!);
        await screen.Loading;

        var rule = Assert.Single(screen.Sites, site => site.Domain == ServiceHarness.KnownSite);

        // Underneath the screen: nothing here has been told.
        await link.SendAsync(
            new IpcRequest { Command = "StartSession", DurationMinutes = 60 }, CancellationToken.None);
        await LinkWait.ForAsync(
            link, static snapshot => snapshot.Status?.State == "Active", "saw the session start");

        Assert.True(screen.CanRemoveRules);

        await screen.RemoveSiteCommand.ExecuteAsync(rule);

        Assert.DoesNotContain(screen.Sites, site => site.Domain == ServiceHarness.KnownSite);
        Assert.False(string.IsNullOrWhiteSpace(screen.Notice));
    }

    /// <summary>A warning from a real service on a carried-out command: more than a day gets the day.</summary>
    [Fact]
    public async Task AWarningARealServiceProducesReachesTheScreen()
    {
        var (link, text, running) = await StartedSessionAsync();
        await using var owned = link;

        using var screen = Running(link, text, running);
        await screen.Loading;

        screen.ExtendMinutes = (int)Chronos.Core.Sessions.SessionLimits.MaxSessionDuration.TotalMinutes;
        await screen.ExtendCommand.ExecuteAsync(null);

        Assert.False(string.IsNullOrWhiteSpace(screen.Notice));
    }

    /// <summary>No view model can be asked this: the control stays and is turned off with the reason beside it.</summary>
    [Theory]
    [InlineData("SitesPage.axaml", "Site")]
    [InlineData("AppsPage.axaml", "App")]
    public void TheRemoveControlIsTurnedOffAndExplainedRatherThanHidden(string page, string kind)
    {
        var markup = File.ReadAllText(Path.Combine(Markup(), "Views", page));

        // One removal button per list, each turned off rather than removed.
        var buttons = Regex.Matches(markup, @"Command\s*=\s*""[^""]*Remove" + kind + @"Command\}""").Count;
        Assert.Equal(1, buttons);
        Assert.Equal(
            buttons,
            Regex.Matches(markup, @"IsEnabled\s*=\s*""\{Binding[^""]*\bCanRemoveRules\}""").Count);

        // The reason is on the screen beside them.
        Assert.Matches(new Regex(@"\{Binding\s+Rules\.RemovalUnavailable\}"), markup);
        Assert.Matches(new Regex(@"IsVisible\s*=\s*""\{Binding\s*!Rules\.CanRemoveRules\}"""), markup);

        // Never hidden by the same flag.
        Assert.DoesNotMatch(new Regex(@"IsVisible\s*=\s*""\{Binding[^""!]*\bCanRemoveRules\}"""), markup);
    }

    [Theory]
    [InlineData("SitesPage.axaml", "SitesSection")]
    [InlineData("AppsPage.axaml", "AppsSection")]
    public void EachRowShowsALockInPlaceOfTheRemoveButton(string page, string section)
    {
        var markup = File.ReadAllText(Path.Combine(Markup(), "Views", page));

        var locked = Regex.Escape($"!$parent[ItemsControl].((vm:{section})DataContext).Rules.CanRemoveRules");

        // Inside the row template, beside the button, holding the padlock.
        Assert.Matches(
            new Regex(
                @"<DataTemplate[^>]*>.*?Remove\w+Command\}.*?IsVisible\s*=\s*""\{Binding\s+" + locked
                + @"\}""[^>]*>\s*<PathIcon\s+Classes=""lock""[^>]*/>.*?</DataTemplate>",
                RegexOptions.Singleline),
            markup);
    }

    [Fact]
    public void TheSetupPageShowsWhatTheServiceSaidAboutTheStart()
    {
        var markup = File.ReadAllText(Path.Combine(Markup(), "Views", "SessionSetupPage.axaml"));

        Assert.Matches(
            new Regex(@"<TextBlock\s+Text=""\{Binding\s+Notice\}""[^>]*IsVisible=""\{Binding\s+HasNotice\}"""),
            markup);
    }

    [Fact]
    public void TheActiveScreenSaysWhyASessionCannotBeMadeShorter()
    {
        var markup = File.ReadAllText(Path.Combine(Markup(), "Views", "SessionActivePage.axaml"));

        Assert.Matches(new Regex(@"\{Binding\s+Text\.ShorteningUnavailable\}"), markup);
    }

    private static ActiveViewModel Running(IServiceLink link, Text text, ServiceSnapshot running)
    {
        var screen = new ActiveViewModel(link, text, new ServiceClock(new FakeClock(Moment)));
        screen.Show(running.Status!);

        return screen;
    }

    private async Task<(ServiceLink Link, Text Text, ServiceSnapshot Running)> StartedSessionAsync()
    {
        _service.Start();

        var link = new ServiceLink(_service.PipeName, _waits.NoWaitAsync);
        link.Start();

        await LinkWait.ForAsync(link, static snapshot => snapshot.Status is not null, "reached the service");

        var text = new Text(_language);
        using var setup = new SetupViewModel(link, text, new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));
        await setup.StartCommand.ExecuteAsync(null);

        var running = await LinkWait.ForAsync(
            link, static snapshot => snapshot.Status?.State == "Active", "saw a session start");

        return (link, text, running);
    }

    private static string Markup()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Chronos.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "The solution root is not above the build output.");

        return Path.Combine(directory!.FullName, "src", "Chronos.App");
    }
}
