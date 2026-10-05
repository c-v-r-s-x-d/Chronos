using System.ComponentModel;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.ViewModels;
using Chronos.Ipc;

namespace Chronos.App.Tests;

/// <summary>The setup and active screens are two objects under one Sites and one Apps page. What was typed and the lists go from one to the other with the state; the notice does not.</summary>
[Collection(CultureBound.Name)]
public sealed class ScreenHandoverTests
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);

    private static readonly AppRuleMessage Protected = new("FileName", "csrss.exe");

    [Fact]
    public void WhatWasTypedSurvivesTheSessionStarting()
    {
        using var app = Lists();
        app.Show(Idle());
        app.Shell.Section = Section.Sites;
        var setup = Assert.IsType<SitesSection>(app.Shell.Page).Rules;
        setup.SiteFilter = "exa";
        setup.AppFilter = "ga";
        setup.NewSiteDomain = "half.ty";

        app.Show(Active());

        var active = Assert.IsType<SitesSection>(app.Shell.Page).Rules;
        Assert.IsType<ActiveViewModel>(active);
        Assert.Equal("exa", active.SiteFilter);
        Assert.Equal("ga", active.AppFilter);
        Assert.Equal("half.ty", active.NewSiteDomain);
        Assert.Equal(["example.com"], active.SiteLines.Select(line => line.Rule.Domain));
        Assert.Equal(["game.exe"], active.AppLines.Select(line => line.Rule.Value));
    }

    [Fact]
    public void WhatWasTypedSurvivesTheSessionEnding()
    {
        using var app = Lists();
        app.Show(Active());
        app.Shell.Section = Section.Apps;
        var active = Assert.IsType<AppsSection>(app.Shell.Page).Rules;
        active.SiteFilter = "oth";
        active.AppFilter = "edit";
        active.NewSiteDomain = "typed.exa";

        app.Show(Idle());

        var setup = Assert.IsType<AppsSection>(app.Shell.Page).Rules;
        Assert.IsType<SetupViewModel>(setup);
        Assert.Equal("oth", setup.SiteFilter);
        Assert.Equal("edit", setup.AppFilter);
        Assert.Equal("typed.exa", setup.NewSiteDomain);
        Assert.Equal(["other.org"], setup.SiteLines.Select(line => line.Rule.Domain));
        Assert.Equal(["editor.exe"], setup.AppLines.Select(line => line.Rule.Value));
    }

    [Fact]
    public void WhatWasTypedSurvivesAWaitBetweenTheTwoScreens()
    {
        using var app = Lists();
        app.Show(Active());
        var active = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);
        active.SiteFilter = "exa";

        app.Show(Say.Status(EngineState.UnlockPending, Moment, endsAt: Moment.AddHours(1), unlockAt: Moment.AddMinutes(1)));
        app.Show(Idle());

        Assert.Equal("exa", app.Shell.SetupScreen().SiteFilter);
    }

    /// <summary>Before the first GetConfig of the new screen answers - here it never does.</summary>
    [Theory]
    [InlineData(EngineState.Idle, EngineState.Active)]
    [InlineData(EngineState.Active, EngineState.Idle)]
    public void TheListsAreThereTheMomentTheScreenChanges(string from, string to)
    {
        using var app = Lists();
        app.Show(Status(from));
        app.Shell.Section = Section.Sites;
        app.Link.Answer = _ => null;

        app.Show(Status(to));

        var rules = Assert.IsType<SitesSection>(app.Shell.Page).Rules;
        Assert.Equal(["example.com", "other.org"], rules.Sites.Select(site => site.Domain));
        Assert.Equal(["game.exe", "editor.exe"], rules.Apps.Select(rule => rule.Value));
        Assert.Equal(2, rules.SiteLines.Count);
        Assert.Equal(2, rules.AppLines.Count);
    }

    [Fact]
    public void TheSetupScreenCountsTheListsItWasHanded()
    {
        using var app = Lists();
        app.Show(Active());
        app.Link.Answer = _ => null;

        app.Show(Idle());

        var setup = app.Shell.SetupScreen();
        Assert.False(setup.NothingToBlock);
        Assert.NotEmpty(setup.Summary);
    }

    [Fact]
    public void TheScreenThatTakesOverSaysWhatItWasHanded()
    {
        using var app = Lists();
        app.Show(Idle());
        var setup = app.Shell.SetupScreen();
        app.Show(Active());
        var active = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);
        active.SiteFilter = "oth";
        active.AppFilter = "edit";
        active.NewSiteDomain = "typed.exa";
        app.Link.Answer = _ => null;
        var announced = Watch(setup);

        app.Show(Idle());

        foreach (var property in new[]
        {
            nameof(RuleScreenViewModel.SiteFilter),
            nameof(RuleScreenViewModel.AppFilter),
            nameof(RuleScreenViewModel.NewSiteDomain),
            nameof(RuleScreenViewModel.Sites),
            nameof(RuleScreenViewModel.Apps),
            nameof(RuleScreenViewModel.SiteLines),
            nameof(RuleScreenViewModel.AppLines),
            nameof(RuleScreenViewModel.ShowsSiteSearch),
            nameof(RuleScreenViewModel.ShowsAppSearch),
            nameof(SetupViewModel.NothingToBlock),
            nameof(SetupViewModel.Summary),
        })
        {
            Assert.Contains(property, announced);
        }
    }

    /// <summary>A status that leaves the screen where it is hands nothing over.</summary>
    [Fact]
    public void AStatusOfTheSameStateLeavesTheTypingAlone()
    {
        using var app = Lists();
        app.Show(Idle());
        var setup = app.Shell.SetupScreen();
        setup.SiteFilter = "exa";
        var announced = Watch(setup);

        app.Show(Idle());

        Assert.Equal("exa", setup.SiteFilter);
        Assert.DoesNotContain(nameof(RuleScreenViewModel.SiteFilter), announced);
    }

    [Fact]
    public async Task ANoticeIsForgottenWhenTheSectionChanges()
    {
        using var app = Lists();
        app.Show(Idle());
        app.Shell.Section = Section.Apps;
        var rules = Assert.IsType<AppsSection>(app.Shell.Page).Rules;
        await rules.AddAppRuleAsync(Protected);
        Assert.True(rules.HasNotice);
        var announced = Watch(rules);

        app.Shell.Section = Section.Session;

        Assert.False(rules.HasNotice);
        Assert.Empty(rules.Notice);
        Assert.Contains(nameof(RuleScreenViewModel.Notice), announced);
        Assert.Contains(nameof(RuleScreenViewModel.HasNotice), announced);
    }

    [Fact]
    public async Task ANoticeIsForgottenWhenTheScreenChanges()
    {
        using var app = Lists();
        app.Show(Idle());
        var setup = app.Shell.SetupScreen();
        await setup.AddAppRuleAsync(Protected);
        Assert.True(setup.HasNotice);

        app.Show(Active());

        Assert.False(setup.HasNotice);
        Assert.False(Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen).HasNotice);
    }

    /// <summary>The answer to a start lands after the screen has gone; it is not there hours later.</summary>
    [Fact]
    public async Task ANoticeThatCameAfterTheScreenLeftIsNotShownWhenItReturns()
    {
        using var english = new UiCulture("en-US");
        using var app = Lists();
        app.Show(Idle());
        var setup = app.Shell.SetupScreen();
        var config = app.Link.Answer!;
        app.Link.Service = request =>
        {
            if (request.Command == ServiceCommand.StartSession)
            {
                app.Show(Active());
            }
        };
        app.Link.Answer = request => request.Command == ServiceCommand.StartSession
            ? new IpcResponse { Accepted = true, Warnings = [new IpcNotice(IpcCodes.SessionDurationClamped, ["5"])] }
            : config(request);

        await setup.StartCommand.ExecuteAsync(null);
        Assert.True(setup.HasNotice);

        app.Show(Idle());

        Assert.False(app.Shell.SetupScreen().HasNotice);
    }

    [Fact]
    public async Task ANoticeForgottenWhileWaitingDoesNotComeBack()
    {
        using var app = Lists();
        app.Show(Active());
        var active = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);
        await active.AddAppRuleAsync(Protected);

        app.Show(Say.Status(EngineState.UnlockPending, Moment, endsAt: Moment.AddHours(1), unlockAt: Moment.AddMinutes(1)));
        app.Show(Active());

        Assert.False(active.HasNotice);
    }

    [Fact]
    public async Task ANoticeStaysOnThePageWhereTheCommandWasGiven()
    {
        using var app = Lists();
        app.Show(Active());
        app.Shell.Section = Section.Apps;
        var rules = Assert.IsType<AppsSection>(app.Shell.Page).Rules;

        await rules.AddAppRuleAsync(Protected);

        // The next status, and choosing the section that is already in front, change nothing.
        app.Show(Say.Status(EngineState.Active, Moment.AddSeconds(15), endsAt: Moment.AddHours(1), startedAt: Moment));
        app.Shell.Section = Section.Apps;
        app.Ticker.Tick();

        Assert.True(rules.HasNotice);
        Assert.Same(rules, Assert.IsType<AppsSection>(app.Shell.Page).Rules);
    }

    private static StatusPayload Idle() => Status(EngineState.Idle);

    private static StatusPayload Active() => Status(EngineState.Active);

    private static StatusPayload Status(string state) => state == EngineState.Idle
        ? Say.Status(state, Moment)
        : Say.Status(state, Moment, endsAt: Moment.AddHours(1), startedAt: Moment);

    // Follows the machine: a configuration that named a language would switch the one a test chose.
    private static AppUnderTest Lists()
    {
        var app = new AppUnderTest(Moment);
        var config = Say.Config(["example.com", "other.org"], ["game.exe", "editor.exe"]) with { Language = "system" };

        app.Link.Answer = _ => IpcResponse.Ok(Say.Status(EngineState.Idle, Moment), config);

        return app;
    }

    private static List<string> Watch(INotifyPropertyChanged source)
    {
        var names = new List<string>();
        source.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);

        return names;
    }
}
