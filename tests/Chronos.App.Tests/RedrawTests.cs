using System.ComponentModel;
using Chronos.App.Localization;
using Chronos.App.ViewModels;

namespace Chronos.App.Tests;

/// <summary>
/// What a tick and a language change are each allowed to touch. Neither is a correctness bug when it
/// over-redraws, but a list control handed new items once a second loses the user's selection.
/// </summary>
[Collection(CultureBound.Name)]
public sealed class RedrawTests
{
    private static readonly DateTimeOffset Moment = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The setup screen reads the session end off the clock; the settings screen reads none, so a tick is no news there.</summary>
    [Fact]
    public void ATickOnTheSetupScreenAnnouncesOnlyTheSummary()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Idle", Moment));

        var screen = Assert.IsType<SetupViewModel>(app.Shell.CurrentScreen);
        var announced = Watch(screen);
        var onSettings = Watch(app.Shell.Settings);

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        app.Ticker.Tick();

        Assert.Equal([nameof(SetupViewModel.Summary)], announced);
        Assert.Empty(onSettings);
    }

    /// <summary>A list control told its items were replaced drops its selection, so ticks leave the lists alone.</summary>
    [Fact]
    public void ATickDoesNotBuildTheListsAgain()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status(
            "Idle", Moment, layers: [Say.Layer("wfp"), Say.Layer("hosts", available: false, reason: "hosts.not-writable")]));

        var screen = Assert.IsType<SetupViewModel>(app.Shell.CurrentScreen);

        var layers = app.Shell.Settings.Layers;
        var presets = screen.Presets;
        using var dialog = new AddAppViewModel(screen, () => []);
        var kinds = dialog.MatchKinds;
        var languages = app.Shell.Settings.Languages;

        for (var second = 0; second < 3; second++)
        {
            app.Clock.Advance(TimeSpan.FromSeconds(1));
            app.Ticker.Tick();
        }

        Assert.Same(layers, app.Shell.Settings.Layers);
        Assert.Same(presets, screen.Presets);
        Assert.Same(kinds, dialog.MatchKinds);
        Assert.Same(languages, app.Shell.Settings.Languages);
    }

    [Fact]
    public void ATickOnTheActiveScreenAnnouncesOnlyTheClockReads()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(50)));

        var screen = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);
        var announced = Watch(screen);

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        app.Ticker.Tick();

        Assert.Equal(
            [nameof(ActiveViewModel.Progress), nameof(ActiveViewModel.Remaining), nameof(ActiveViewModel.RemainingDigits), nameof(ActiveViewModel.RemainingText)],
            announced.Order());
    }

    [Fact]
    public void ATickOnTheWaitingScreenAnnouncesOnlyTheClockReads()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status(
            "UnlockPending", Moment, endsAt: Moment.AddHours(2), unlockAt: Moment.AddMinutes(20)));

        var screen = Assert.IsType<WaitingViewModel>(app.Shell.CurrentScreen);
        var announced = Watch(screen);

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        app.Ticker.Tick();

        Assert.Equal(
            [nameof(WaitingViewModel.Progress), nameof(WaitingViewModel.Remaining), nameof(WaitingViewModel.RemainingDigits), nameof(WaitingViewModel.RemainingText)],
            announced.Order());
    }

    /// <summary>One language change is one redraw per screen, not one per property of <see cref="Text"/>.</summary>
    [Fact]
    public void ALanguageChangeRedrawsEachScreenExactlyOnce()
    {
        using var machine = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        // Every screen built and shown once, so none is redrawn here for another reason.
        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddHours(1)));
        var active = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);

        app.Show(Say.Status(
            "UnlockPending", Moment, endsAt: Moment.AddHours(1), unlockAt: Moment.AddMinutes(20)));
        var waiting = Assert.IsType<WaitingViewModel>(app.Shell.CurrentScreen);

        app.Show(Say.Status("Idle", Moment, layers: [Say.Layer("wfp")]));
        var setup = Assert.IsType<SetupViewModel>(app.Shell.CurrentScreen);

        using var dialog = new AddAppViewModel(setup, () => []);

        var onSetup = Watch(setup);
        var onDialog = Watch(dialog);
        var onActive = Watch(active);
        var onWaiting = Watch(waiting);
        var onTray = Watch(app.Shell.Tray);
        var onShell = Watch(app.Shell);
        var onSettings = Watch(app.Shell.Settings);

        app.Language.Choose(LanguageChoice.Russian);

        Assert.Equal(1, onSetup.Count(name => name == nameof(SetupViewModel.Presets)));
        Assert.Equal(1, onDialog.Count(name => name == nameof(AddAppViewModel.MatchKinds)));
        Assert.Equal(1, onSettings.Count(name => name == nameof(SettingsViewModel.Layers)));
        Assert.Equal(1, onActive.Count(name => name == nameof(ActiveViewModel.CoolDownNotice)));
        Assert.Equal(1, onWaiting.Count(name => name == nameof(WaitingViewModel.RemainingText)));
        Assert.Equal(1, onTray.Count(name => name == nameof(TrayViewModel.Tooltip)));
        Assert.Equal(1, onShell.Count(name => name == nameof(ShellViewModel.ServiceMessage)));
        Assert.Equal(1, onShell.Count(name => name == nameof(ShellViewModel.Sections)));
    }

    /// <summary>The words really do change; a screen redrawn once and one not redrawn at all are both quiet.</summary>
    [Fact]
    public void ALanguageChangeStillReachesTheScreenThatIsNotInFront()
    {
        using var machine = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status(
            "UnlockPending", Moment, endsAt: Moment.AddHours(1), unlockAt: Moment.AddMinutes(20)));

        var waiting = Assert.IsType<WaitingViewModel>(app.Shell.CurrentScreen);
        Assert.Equal("The block lifts in 20 minutes.", waiting.RemainingText);

        app.Show(Say.Status("Idle", Moment, layers: [Say.Layer("wfp")]));
        Assert.IsType<SetupViewModel>(app.Shell.CurrentScreen);
        Assert.Equal(Section.Session, app.Shell.Section);
        Assert.Equal("Firewall", Assert.Single(app.Shell.Settings.Layers).Name);

        app.Language.Choose(LanguageChoice.Russian);

        Assert.Equal("Блокировка снимется через 20 минут.", waiting.RemainingText);

        // The settings section is not in front either.
        Assert.Equal("Брандмауэр", Assert.Single(app.Shell.Settings.Layers).Name);
    }

    /// <summary>
    /// A control told its items were replaced writes back -1 and the setter refuses it, so the selection
    /// must be announced separately. The old selection is not value-equal to anything in the new list.
    /// </summary>
    [Fact]
    public void EveryListOfWordsAnnouncesItsSelectionRightAfterItself()
    {
        using var machine = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Idle", Moment));

        var screen = Assert.IsType<SetupViewModel>(app.Shell.CurrentScreen);
        using var dialog = new AddAppViewModel(screen, () => []);
        dialog.SelectedMatchKind = 1;

        var announced = Watch(dialog);
        var onSetup = Watch(screen);
        var onSettings = Watch(app.Shell.Settings);
        var onShell = Watch(app.Shell);

        app.Language.Choose(LanguageChoice.Russian);

        AnnouncedAfterIt(announced, nameof(AddAppViewModel.MatchKinds), nameof(AddAppViewModel.SelectedMatchKind));
        AnnouncedAfterIt(onSetup, nameof(SetupViewModel.Durations), nameof(SetupViewModel.SelectedDuration));

        // The language list is not replaced, so only its selection is said.
        Assert.DoesNotContain(nameof(SettingsViewModel.Languages), onSettings);
        Assert.Contains(nameof(SettingsViewModel.SelectedLanguage), onSettings);
        AnnouncedAfterIt(onShell, nameof(ShellViewModel.Sections), nameof(ShellViewModel.SelectedSectionIndex));

        // The selection itself survived.
        Assert.Equal(1, dialog.SelectedMatchKind);
    }

    /// <summary>The -1 a list control writes back during replacement is refused; the selection announcement restores it.</summary>
    [Fact]
    public void ASelectionOfNothingIsRefusedRatherThanKept()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Idle", Moment));

        var screen = Assert.IsType<SetupViewModel>(app.Shell.CurrentScreen);

        using var dialog = new AddAppViewModel(screen, () => []);

        dialog.SelectedMatchKind = 1;
        dialog.SelectedMatchKind = -1;
        Assert.Equal(1, dialog.SelectedMatchKind);

        app.Shell.Settings.SelectedLanguage = -1;
        Assert.Equal(0, app.Shell.Settings.SelectedLanguage);
    }

    /// <summary>
    /// A binding passes on only a value that differs from the last one it read, so a selection is said
    /// as none first. Without that the sidebar and duration chips show nothing highlighted after a language change.
    /// </summary>
    [Fact]
    public void AReplacedSidebarSaysItsSectionAsNoneAndThenAsWhatItIs()
    {
        using var machine = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status("Idle", Moment));
        app.Shell.SelectedSectionIndex = 2;
        var read = Read(app.Shell, () => app.Shell.SelectedSectionIndex, nameof(ShellViewModel.Sections), nameof(ShellViewModel.SelectedSectionIndex));

        app.Language.Choose(LanguageChoice.Russian);

        Assert.Equal(
            [(nameof(ShellViewModel.Sections), -1), (nameof(ShellViewModel.SelectedSectionIndex), -1), (nameof(ShellViewModel.SelectedSectionIndex), 2)],
            read);
        Assert.Equal(2, app.Shell.SelectedSectionIndex);
        Assert.Equal(Section.Apps, app.Shell.Section);
    }

    [Fact]
    public void ASidebarReplacedByASessionKeepsItsSection()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status("Idle", Moment));
        app.Shell.SelectedSectionIndex = 1;
        var read = Read(app.Shell, () => app.Shell.SelectedSectionIndex, nameof(ShellViewModel.Sections), nameof(ShellViewModel.SelectedSectionIndex));

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddHours(1)));

        Assert.Equal(
            [(nameof(ShellViewModel.Sections), -1), (nameof(ShellViewModel.SelectedSectionIndex), -1), (nameof(ShellViewModel.SelectedSectionIndex), 1)],
            read);
    }

    [Fact]
    public void ReplacedDurationsSayTheirChipAsNoneAndThenAsWhatItIs()
    {
        using var machine = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status("Idle", Moment));
        var screen = Assert.IsType<SetupViewModel>(app.Shell.CurrentScreen);
        screen.SelectedDuration = 2;
        var minutes = screen.Minutes;
        var read = Read(screen, () => screen.SelectedDuration, nameof(SetupViewModel.Durations), nameof(SetupViewModel.SelectedDuration));

        app.Language.Choose(LanguageChoice.Russian);

        Assert.Equal(
            [(nameof(SetupViewModel.Durations), -1), (nameof(SetupViewModel.SelectedDuration), -1), (nameof(SetupViewModel.SelectedDuration), 2)],
            read);
        Assert.Equal(2, screen.SelectedDuration);
        Assert.Equal(minutes, screen.Minutes);
        Assert.False(screen.IsCustomDuration);
    }

    /// <summary>The language list is not replaced: a box handed new items while writing its selection puts the old one back.</summary>
    [Fact]
    public void ALanguageChangeKeepsTheLanguageList()
    {
        using var machine = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status("Idle", Moment));
        var settings = app.Shell.Settings;
        var languages = settings.Languages;
        var onSettings = Watch(settings);

        settings.SelectedLanguage = LanguageChoice.IndexOf(LanguageChoice.Russian);

        Assert.Same(languages, settings.Languages);
        Assert.Equal(["English", "Русский"], settings.Languages.Select(line => line.Name));
        Assert.DoesNotContain(nameof(SettingsViewModel.Languages), onSettings);
        Assert.Equal(1, onSettings.Count(name => name == nameof(SettingsViewModel.SelectedLanguage)));
        Assert.Equal(LanguageChoice.IndexOf(LanguageChoice.Russian), settings.SelectedLanguage);
    }

    [Fact]
    public void AStatusSaysNothingAboutTheLanguages()
    {
        using var machine = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status("Idle", Moment));
        app.Shell.Settings.SelectedLanguage = LanguageChoice.IndexOf(LanguageChoice.Russian);
        var languages = app.Shell.Settings.Languages;
        var onSettings = Watch(app.Shell.Settings);

        app.Show(Say.Status("Idle", Moment.AddSeconds(15), layers: [Say.Layer("wfp")]));

        Assert.Same(languages, app.Shell.Settings.Languages);
        Assert.Contains(nameof(SettingsViewModel.Layers), onSettings);
        Assert.DoesNotContain(nameof(SettingsViewModel.Languages), onSettings);
        Assert.DoesNotContain(nameof(SettingsViewModel.SelectedLanguage), onSettings);
    }

    // What a selection reads as at each announcement of the list or of itself.
    private static List<(string Name, int Selected)> Read(
        INotifyPropertyChanged source, Func<int> selected, params string[] names)
    {
        var read = new List<(string, int)>();
        source.PropertyChanged += (_, e) =>
        {
            if (names.Contains(e.PropertyName))
            {
                read.Add((e.PropertyName!, selected()));
            }
        };

        return read;
    }

    private static void AnnouncedAfterIt(List<string> announced, string list, string selection)
    {
        var items = announced.IndexOf(list);
        var chosen = announced.IndexOf(selection);

        Assert.True(items >= 0, $"{list} was never announced.");
        Assert.True(
            chosen > items,
            $"{selection} has to be announced after {list}; it was announced at {chosen} and the list at {items}.");
    }

    private static List<string> Watch(INotifyPropertyChanged source)
    {
        var names = new List<string>();
        source.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);

        return names;
    }
}
