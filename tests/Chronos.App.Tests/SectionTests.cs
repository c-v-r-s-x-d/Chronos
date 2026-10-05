using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.ViewModels;

namespace Chronos.App.Tests;

/// <summary>Sections in the sidebar are navigation, not state.</summary>
[Collection(CultureBound.Name)]
public sealed class SectionTests
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheWindowOpensOnTheSessionSection()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment));

        Assert.Equal(Section.Session, app.Shell.Section);
        Assert.Same(app.Shell.CurrentScreen, app.Shell.Page);
    }

    [Fact]
    public void TheSectionSurvivesTheSessionStarting()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment));
        app.Shell.Section = Section.Sites;

        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddHours(1)));

        Assert.Equal(Section.Sites, app.Shell.Section);
        var page = Assert.IsType<SitesSection>(app.Shell.Page);
        Assert.IsType<ActiveViewModel>(page.Rules);
    }

    [Fact]
    public void TheSectionSurvivesAWaitThatWasCancelled()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddHours(1)));
        app.Shell.Section = Section.Apps;

        app.Show(Say.Status(EngineState.UnlockPending, Moment, endsAt: Moment.AddHours(1), unlockAt: Moment.AddMinutes(15)));
        Assert.False(app.Shell.ShowsSidebar);
        Assert.IsType<WaitingViewModel>(app.Shell.Page);

        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddHours(1)));

        Assert.True(app.Shell.ShowsSidebar);
        var page = Assert.IsType<AppsSection>(app.Shell.Page);
        Assert.IsType<ActiveViewModel>(page.Rules);
    }

    [Fact]
    public void WithNoSessionTheListSectionsAreOverTheSetupScreen()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment));

        app.Shell.Section = Section.Apps;
        Assert.Same(app.Shell.CurrentScreen, Assert.IsType<AppsSection>(app.Shell.Page).Rules);
        Assert.IsType<SetupViewModel>(app.Shell.CurrentScreen);

        app.Shell.Section = Section.Sites;
        Assert.Same(app.Shell.CurrentScreen, Assert.IsType<SitesSection>(app.Shell.Page).Rules);
    }

    [Fact]
    public void ChoosingASectionAnnouncesThePageAndTheSidebarSelection()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment));
        var announced = Watch(app.Shell);

        app.Shell.Section = Section.Sites;

        Assert.Contains(nameof(ShellViewModel.Section), announced);
        Assert.Contains(nameof(ShellViewModel.Page), announced);
        Assert.Contains(nameof(ShellViewModel.SelectedSectionIndex), announced);
    }

    [Fact]
    public void AChangeOfStateAnnouncesThePageTheSidebarAndTheLock()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment));
        var announced = Watch(app.Shell);

        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddHours(1)));

        Assert.Contains(nameof(ShellViewModel.Page), announced);
        Assert.Contains(nameof(ShellViewModel.ShowsSidebar), announced);
        Assert.Contains(nameof(ShellViewModel.RulesLocked), announced);
        Assert.Contains(nameof(ShellViewModel.Sections), announced);
    }

    /// <summary>A list control handed new items drops its selection, so a status that moves no lock hands it none.</summary>
    [Fact]
    public void AStatusThatMovesNoLockLeavesTheSidebarListAlone()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddHours(1)));
        var sections = app.Shell.Sections;
        var announced = Watch(app.Shell);

        app.Show(Say.Status(EngineState.Active, Moment.AddSeconds(15), endsAt: Moment.AddHours(1)));

        Assert.Same(sections, app.Shell.Sections);
        Assert.DoesNotContain(nameof(ShellViewModel.Sections), announced);
        Assert.DoesNotContain(nameof(ShellViewModel.SelectedSectionIndex), announced);
    }

    [Fact]
    public void TheSettingsSectionShowsTheSameSettingsInBothStates()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment));
        app.Shell.Section = Section.Settings;
        var idle = app.Shell.Page;

        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddHours(1)));

        Assert.Same(idle, app.Shell.Page);
        Assert.Same(app.Shell.Settings, app.Shell.Page);
    }

    [Fact]
    public void TheListsAreLockedInTheSidebarOnlyDuringASession()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment));
        Assert.All(app.Shell.Sections, line => Assert.False(line.Locked));

        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddHours(1)));

        Assert.True(app.Shell.RulesLocked);
        Assert.True(app.Shell.Sections.Single(l => l.Section == Section.Sites).Locked);
        Assert.True(app.Shell.Sections.Single(l => l.Section == Section.Apps).Locked);
        Assert.False(app.Shell.Sections.Single(l => l.Section == Section.Session).Locked);
    }

    [Fact]
    public void GoingToASectionFromAPageMovesTheSidebarToo()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment));

        app.Shell.GoToCommand.Execute(Section.Apps);

        Assert.Equal((int)Section.Apps, app.Shell.SelectedSectionIndex);
    }

    [Fact]
    public void AnIndexOutsideTheSidebarIsIgnored()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment));
        app.Shell.Section = Section.Sites;

        app.Shell.SelectedSectionIndex = -1;

        Assert.Equal(Section.Sites, app.Shell.Section);
    }

    [Fact]
    public void TheSectionNamesFollowTheLanguage()
    {
        using var culture = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment));
        var english = app.Shell.Sections[1].Name;

        app.Text.SwitchTo(LanguageChoice.Russian);

        Assert.NotEqual(english, app.Shell.Sections[1].Name);
    }

    private static List<string> Watch(System.ComponentModel.INotifyPropertyChanged source)
    {
        var names = new List<string>();
        source.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);

        return names;
    }
}
