using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Chronos.App.Apps;
using Chronos.App.Localization;
using Chronos.App.Resources;
using Chronos.App.Services;
using Chronos.App.ViewModels;
using Chronos.Ipc;

namespace Chronos.App.Tests;

/// <summary>The "Add application" dialog.</summary>
[Collection(CultureBound.Name)]
public sealed class AddAppDialogTests
{
    private static readonly DateTimeOffset Moment = new(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);

    private static readonly RunningApp Steam = new("steam.exe", @"C:\s\steam.exe");

    private static readonly RunningApp Discord = new("discord.exe", @"C:\d\discord.exe");

    [Fact]
    public void NothingChosenMeansNothingToAdd()
    {
        using var app = Idle();
        var dialog = Open(app);

        Assert.False(dialog.CanAdd);
    }

    [Fact]
    public void AShortcutShowsWhereItLeadsBeforeAnythingIsAdded()
    {
        using var app = Idle();
        var dialog = Open(app);
        dialog.TabIndex = (int)AddAppTab.Shortcut;

        dialog.ChooseShortcut(Fixtures.ShortcutTo(Fixtures.Notepad));

        Assert.Equal(Fixtures.Notepad, dialog.ShortcutTarget);
        Assert.True(dialog.CanAdd);
        Assert.DoesNotContain(app.Link.Sent, r => r.Command == ServiceCommand.AddAppRule);
    }

    [Fact]
    public void AShortcutThatLeadsNowhereCannotBeAdded()
    {
        using var app = Idle();
        var dialog = Open(app);
        dialog.TabIndex = (int)AddAppTab.Shortcut;

        dialog.ChooseShortcut(Fixtures.ShortcutTo(@"C:\nowhere\gone.exe"));

        Assert.False(dialog.CanAdd);
        Assert.NotEmpty(dialog.ShortcutRefusal);
    }

    [Fact]
    public void OnTheNameTabOnlyMatchingByNameIsOffered()
    {
        using var app = Idle();
        var dialog = Open(app);
        dialog.SelectedMatchKind = IndexOf(AppMatch.FullPath);

        dialog.TabIndex = (int)AddAppTab.Name;

        Assert.False(dialog.CanChooseFullPath);
        Assert.Equal(IndexOf(AppMatch.FileName), dialog.SelectedMatchKind);
    }

    [Fact]
    public async Task ARefusalKeepsTheDialogOpenAndSaysWhy()
    {
        using var app = Idle();
        var dialog = Open(app);
        var closed = false;
        dialog.Done += (_, _) => closed = true;
        dialog.TabIndex = (int)AddAppTab.Name;
        dialog.Name = "csrss.exe";

        await dialog.AddCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.True(dialog.Rules.HasNotice);
    }

    [Fact]
    public async Task ARefusalFromTheServiceKeepsTheDialogOpenToo()
    {
        using var culture = new UiCulture("en-US");
        using var app = Idle();
        var dialog = Open(app);
        var closed = false;
        dialog.Done += (_, _) => closed = true;
        dialog.TabIndex = (int)AddAppTab.Name;
        dialog.Name = "steam.exe";

        await dialog.AddCommand.ExecuteAsync(null);

        Assert.Single(app.Link.Sent, r => r.Command == ServiceCommand.AddAppRule);
        Assert.False(closed);
        Assert.Equal(Strings.NoticeRulesAppProtected, dialog.Rules.Notice);
        Assert.Equal("steam.exe", dialog.Name);
    }

    [Fact]
    public async Task ASuccessClosesTheDialog()
    {
        using var app = Idle(acceptEverything: true);
        var dialog = Open(app);
        var closed = false;
        dialog.Done += (_, _) => closed = true;
        dialog.TabIndex = (int)AddAppTab.Name;
        dialog.Name = "steam.exe";

        await dialog.AddCommand.ExecuteAsync(null);

        Assert.True(closed);
    }

    [Fact]
    public async Task ALostServiceKeepsTheDialogOpen()
    {
        using var app = Idle();
        app.Link.Answer = _ => null;
        var dialog = Open(app);
        var closed = false;
        dialog.Done += (_, _) => closed = true;
        dialog.TabIndex = (int)AddAppTab.Name;
        dialog.Name = "steam.exe";

        await dialog.AddCommand.ExecuteAsync(null);

        Assert.False(closed);
    }

    [Fact]
    public void AStatusDoesNotResetWhatIsChosen()
    {
        using var app = Idle();
        var dialog = Open(app);
        dialog.TabIndex = (int)AddAppTab.Executable;
        dialog.ChooseExecutable(Fixtures.Notepad);

        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddHours(1)));

        Assert.Equal((int)AddAppTab.Executable, dialog.TabIndex);
        Assert.Equal(Fixtures.Notepad, dialog.ChosenExecutable);
    }

    /// <summary>Awaited, not fired: the list is read off the interface thread.</summary>
    [Fact]
    public async Task TheRunningListCanBeNarrowed()
    {
        using var app = Idle();
        var dialog = new AddAppViewModel(Rules(app), () =>
            [new RunningApp("steam.exe", @"C:\s\steam.exe"), new RunningApp("discord.exe", @"C:\d\discord.exe")]);
        await dialog.LoadRunningCommand.ExecuteAsync(null);

        dialog.RunningFilter = "disc";

        Assert.Equal(["discord.exe"], dialog.VisibleRunning.Select(a => a.Name));
    }

    [Fact]
    public async Task AStatusLeavesEveryTabAsItWas()
    {
        using var app = Idle();
        var dialog = await Running(app, Steam, Discord);
        dialog.RunningFilter = "s";
        dialog.SelectedRunning = Steam;
        dialog.ChooseExecutable(Fixtures.Notepad);
        dialog.ChooseShortcut(Fixtures.ShortcutTo(Fixtures.Notepad));
        dialog.SelectedMatchKind = IndexOf(AppMatch.FullPath);
        dialog.Tab = AddAppTab.Shortcut;
        var announced = Watch(dialog);

        app.Show(Say.Status(EngineState.Idle, Moment.AddSeconds(15)));
        app.Show(Say.Status(EngineState.Active, Moment.AddSeconds(30), endsAt: Moment.AddHours(1)));

        Assert.Empty(announced);
        Assert.Equal(AddAppTab.Shortcut, dialog.Tab);
        Assert.Equal("s", dialog.RunningFilter);
        Assert.Equal(Steam, dialog.SelectedRunning);
        Assert.Equal(Fixtures.Notepad, dialog.ChosenExecutable);
        Assert.Equal(Fixtures.Notepad, dialog.ShortcutTarget);
        Assert.Equal(IndexOf(AppMatch.FullPath), dialog.SelectedMatchKind);
        Assert.True(dialog.CanAdd);
    }

    [Fact]
    public async Task AnApplicationCanBeAddedDuringASession()
    {
        using var app = Idle(acceptEverything: true);
        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddHours(1)));
        var dialog = Open(app);
        var closed = false;
        dialog.Done += (_, _) => closed = true;
        dialog.Tab = AddAppTab.Name;
        dialog.Name = "steam.exe";

        await dialog.AddCommand.ExecuteAsync(null);

        Assert.Single(app.Link.Sent, r => r.Command == ServiceCommand.AddAppRule);
        Assert.True(closed);
    }

    [Theory]
    [InlineData(AddAppTab.Running)]
    [InlineData(AddAppTab.Executable)]
    [InlineData(AddAppTab.Shortcut)]
    [InlineData(AddAppTab.Name)]
    public void TheButtonIsOffUntilSomethingIsChosenOnTheTabInFront(AddAppTab tab)
    {
        using var app = Idle();
        var dialog = Open(app);
        dialog.Tab = tab;

        Assert.False(dialog.CanAdd);
        Assert.False(dialog.AddCommand.CanExecute(null));

        var announced = Watch(dialog);
        var asked = 0;
        dialog.AddCommand.CanExecuteChanged += (_, _) => asked++;

        Choose(dialog, tab);

        Assert.True(dialog.CanAdd);
        Assert.True(dialog.AddCommand.CanExecute(null));
        Assert.Contains(nameof(AddAppViewModel.CanAdd), announced);
        Assert.True(asked > 0, "The command never said that it could now run.");
    }

    [Theory]
    [InlineData(AddAppTab.Running)]
    [InlineData(AddAppTab.Executable)]
    [InlineData(AddAppTab.Shortcut)]
    [InlineData(AddAppTab.Name)]
    public void AChoiceOnOneTabDoesNotCountOnTheOthers(AddAppTab tab)
    {
        using var app = Idle();
        var dialog = Open(app);
        dialog.Tab = tab;
        Choose(dialog, tab);

        foreach (var other in Enum.GetValues<AddAppTab>().Where(other => other != tab))
        {
            var announced = Watch(dialog);
            var asked = 0;
            dialog.AddCommand.CanExecuteChanged += (_, _) => asked++;

            dialog.Tab = other;

            Assert.False(dialog.CanAdd, $"{tab} counted on {other}.");
            Assert.False(dialog.AddCommand.CanExecute(null));
            Assert.Contains(nameof(AddAppViewModel.CanAdd), announced);
            Assert.True(asked > 0, "The command never said that it could no longer run.");

            dialog.Tab = tab;
            Assert.True(dialog.CanAdd);
        }
    }

    [Fact]
    public void ANameOfSpacesIsNotAName()
    {
        using var app = Idle();
        var dialog = Open(app);
        dialog.Tab = AddAppTab.Name;
        dialog.Name = "steam.exe";
        var announced = Watch(dialog);

        dialog.Name = "   ";

        Assert.False(dialog.CanAdd);
        Assert.False(dialog.AddCommand.CanExecute(null));
        Assert.Equal([nameof(AddAppViewModel.Name), nameof(AddAppViewModel.CanAdd)], announced);
    }

    [Fact]
    public async Task AddingWithNothingChosenSendsNothing()
    {
        using var app = Idle(acceptEverything: true);
        var closed = false;

        foreach (var tab in Enum.GetValues<AddAppTab>())
        {
            var dialog = Open(app);
            dialog.Done += (_, _) => closed = true;
            dialog.Tab = tab;

            await dialog.AddCommand.ExecuteAsync(null);
        }

        Assert.DoesNotContain(app.Link.Sent, r => r.Command == ServiceCommand.AddAppRule);
        Assert.False(closed);
    }

    [Fact]
    public async Task ANameIsAddedByNameWithoutTheSpacesAroundIt()
    {
        using var app = Idle(acceptEverything: true);
        var dialog = Open(app);
        dialog.Tab = AddAppTab.Name;
        dialog.Name = "  steam.exe ";

        await dialog.AddCommand.ExecuteAsync(null);

        var rule = Assert.Single(app.Link.Sent, r => r.Command == ServiceCommand.AddAppRule).App;
        Assert.Equal(new AppRuleMessage(AppMatch.FileName, "steam.exe"), rule);
    }

    [Fact]
    public void ChangingTheTabSaysSo()
    {
        using var app = Idle();
        var dialog = Open(app);
        var announced = Watch(dialog);

        dialog.TabIndex = (int)AddAppTab.Name;

        Assert.Equal(AddAppTab.Name, dialog.Tab);
        Assert.Contains(nameof(AddAppViewModel.Tab), announced);
        Assert.Contains(nameof(AddAppViewModel.TabIndex), announced);
        Assert.Contains(nameof(AddAppViewModel.CanChooseFullPath), announced);
        Assert.Contains(nameof(AddAppViewModel.CanAdd), announced);

        // Nothing was chosen by path, so the kind had no reason to move.
        Assert.DoesNotContain(nameof(AddAppViewModel.SelectedMatchKind), announced);

        announced.Clear();
        dialog.Tab = AddAppTab.Running;

        Assert.Equal((int)AddAppTab.Running, dialog.TabIndex);
        Assert.True(dialog.CanChooseFullPath);
        Assert.Contains(nameof(AddAppViewModel.TabIndex), announced);
        Assert.Contains(nameof(AddAppViewModel.CanChooseFullPath), announced);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void ATabThatIsNotOneOfTheFourIsIgnored(int index)
    {
        using var app = Idle();
        var dialog = Open(app);
        dialog.Tab = AddAppTab.Shortcut;
        var announced = Watch(dialog);

        dialog.TabIndex = index;

        Assert.Equal(AddAppTab.Shortcut, dialog.Tab);
        Assert.Empty(announced);
    }

    [Fact]
    public void MovingToTheNameTabAnnouncesTheKindItFellBackTo()
    {
        using var app = Idle();
        var dialog = Open(app);
        dialog.SelectedMatchKind = IndexOf(AppMatch.FullPath);
        var announced = Watch(dialog);

        dialog.Tab = AddAppTab.Name;

        Assert.Contains(nameof(AddAppViewModel.SelectedMatchKind), announced);
        Assert.Contains(nameof(AddAppViewModel.MatchKindExplanation), announced);
        Assert.Contains(nameof(AddAppViewModel.CanChooseFullPath), announced);
        Assert.Equal(AppMatchKinds.Explanation(AppMatch.FileName), dialog.MatchKindExplanation);
    }

    [Fact]
    public void OnTheNameTabTheFullPathCannotBeChosen()
    {
        using var app = Idle();
        var dialog = Open(app);
        dialog.Tab = AddAppTab.Name;
        var announced = Watch(dialog);

        dialog.SelectedMatchKind = IndexOf(AppMatch.FullPath);

        Assert.Equal(IndexOf(AppMatch.FileName), dialog.SelectedMatchKind);
        Assert.Empty(announced);
    }

    [Fact]
    public void ChoosingAKindAnnouncesItAndItsExplanation()
    {
        using var app = Idle();
        var dialog = Open(app);
        var announced = Watch(dialog);

        dialog.SelectedMatchKind = IndexOf(AppMatch.FullPath);

        Assert.Equal(
            [nameof(AddAppViewModel.SelectedMatchKind), nameof(AddAppViewModel.MatchKindExplanation)],
            announced);
    }

    [Fact]
    public void ChoosingAProgramAnnouncesIt()
    {
        using var app = Idle();
        var dialog = Open(app);
        dialog.Tab = AddAppTab.Executable;
        var announced = Watch(dialog);

        dialog.ChooseExecutable("  " + Fixtures.Notepad + " ");

        Assert.Equal(Fixtures.Notepad, dialog.ChosenExecutable);
        Assert.Equal([nameof(AddAppViewModel.ChosenExecutable), nameof(AddAppViewModel.CanAdd)], announced);
    }

    [Fact]
    public void ChoosingAShortcutAnnouncesWhereItLeads()
    {
        using var culture = new UiCulture("en-US");
        using var app = Idle();
        var dialog = Open(app);
        dialog.Tab = AddAppTab.Shortcut;
        var shortcut = Fixtures.ShortcutTo(Fixtures.Notepad);
        var announced = Watch(dialog);

        dialog.ChooseShortcut(shortcut);

        Assert.Equal(shortcut, dialog.ChosenShortcut);
        Assert.Equal(
            string.Format(CultureInfo.CurrentUICulture, Strings.ShortcutPointsTo, Fixtures.Notepad),
            dialog.ShortcutLeadsTo);
        Assert.Empty(dialog.ShortcutRefusal);
        Assert.Contains(nameof(AddAppViewModel.ChosenShortcut), announced);
        Assert.Contains(nameof(AddAppViewModel.ShortcutTarget), announced);
        Assert.Contains(nameof(AddAppViewModel.ShortcutLeadsTo), announced);
        Assert.Contains(nameof(AddAppViewModel.ShortcutRefusal), announced);
        Assert.Contains(nameof(AddAppViewModel.CanAdd), announced);
    }

    [Fact]
    public void AnotherShortcutReplacesWhatTheLastOneSaid()
    {
        using var app = Idle();
        var dialog = Open(app);
        dialog.Tab = AddAppTab.Shortcut;
        var good = Fixtures.ShortcutTo(Fixtures.Notepad);
        var bad = Fixtures.ShortcutTo(@"C:\nowhere\gone.exe");

        dialog.ChooseShortcut(good);
        dialog.ChooseShortcut(bad);

        Assert.Null(dialog.ShortcutTarget);
        Assert.Empty(dialog.ShortcutLeadsTo);
        Assert.Equal(Shortcut.Refusal(ShortcutOutcome.TargetMissing), dialog.ShortcutRefusal);
        Assert.False(dialog.CanAdd);

        dialog.ChooseShortcut(good);

        Assert.Equal(Fixtures.Notepad, dialog.ShortcutTarget);
        Assert.Empty(dialog.ShortcutRefusal);
        Assert.True(dialog.CanAdd);
    }

    [Fact]
    public async Task LoadingWhatIsRunningAnnouncesTheLists()
    {
        using var app = Idle();
        var dialog = new AddAppViewModel(Rules(app), () => [Steam, Discord]);
        var announced = Watch(dialog);

        await dialog.LoadRunningCommand.ExecuteAsync(null);

        Assert.Equal([Steam, Discord], dialog.RunningApps);
        Assert.Equal([Steam, Discord], dialog.VisibleRunning);
        Assert.Contains(nameof(AddAppViewModel.RunningApps), announced);
        Assert.Contains(nameof(AddAppViewModel.AnyRunningListed), announced);
        AnnouncedAfterIt(announced, nameof(AddAppViewModel.VisibleRunning), nameof(AddAppViewModel.SelectedRunning));
    }

    [Fact]
    public async Task NarrowingAnnouncesTheListAndThenItsSelection()
    {
        using var app = Idle();
        var dialog = await Running(app, Steam, Discord);
        dialog.SelectedRunning = Discord;
        var announced = Watch(dialog);

        dialog.RunningFilter = "DISC";

        Assert.Equal([Discord], dialog.VisibleRunning);
        Assert.Equal(Discord, dialog.SelectedRunning);
        Assert.Contains(nameof(AddAppViewModel.RunningFilter), announced);
        AnnouncedAfterIt(announced, nameof(AddAppViewModel.VisibleRunning), nameof(AddAppViewModel.SelectedRunning));
    }

    [Fact]
    public async Task TheRunningListIsNarrowedByAnyPartOfTheName()
    {
        using var app = Idle();
        var dialog = await Running(app, Steam, Discord);

        dialog.RunningFilter = "cor";

        Assert.Equal([Discord], dialog.VisibleRunning);
    }

    [Fact]
    public async Task TheRunningListIsNotNarrowedByThePath()
    {
        using var app = Idle();
        var dialog = await Running(
            app, new RunningApp("steam.exe", @"C:\Games\Valve\steam.exe"), new RunningApp("discord.exe", @"C:\Games\discord.exe"));

        dialog.RunningFilter = "Valve";

        Assert.Empty(dialog.VisibleRunning);

        dialog.RunningFilter = "Games";

        Assert.Empty(dialog.VisibleRunning);
    }

    [Fact]
    public async Task ARunningProgramMatchedByPathNamesItsFullPath()
    {
        using var app = Idle(acceptEverything: true);
        var dialog = await Running(app, Steam, Discord);
        dialog.SelectedMatchKind = IndexOf(AppMatch.FullPath);
        dialog.SelectedRunning = Steam;

        await dialog.AddCommand.ExecuteAsync(null);

        var rule = Assert.Single(app.Link.Sent, r => r.Command == ServiceCommand.AddAppRule).App;
        Assert.Equal(new AppRuleMessage(AppMatch.FullPath, Steam.Path), rule);
    }

    [Fact]
    public async Task ASelectionNarrowedOutOfTheListIsLetGo()
    {
        using var app = Idle();
        var dialog = await Running(app, Steam, Discord);
        dialog.SelectedRunning = Steam;
        var announced = Watch(dialog);
        var asked = 0;
        dialog.AddCommand.CanExecuteChanged += (_, _) => asked++;

        dialog.RunningFilter = "disc";

        Assert.Null(dialog.SelectedRunning);
        Assert.False(dialog.CanAdd);
        Assert.Contains(nameof(AddAppViewModel.CanAdd), announced);
        Assert.True(asked > 0);
    }

    /// <summary>What a list control writes back while its items are replaced is refused, like -1.</summary>
    [Fact]
    public async Task ASelectionOfNothingIsRefusedRatherThanKept()
    {
        using var app = Idle();
        var dialog = await Running(app, Steam, Discord);
        dialog.SelectedRunning = Steam;
        var announced = Watch(dialog);

        dialog.SelectedRunning = null;

        Assert.Equal(Steam, dialog.SelectedRunning);
        Assert.Empty(announced);
    }

    [Fact]
    public async Task PickingARunningProgramAnnouncesIt()
    {
        using var app = Idle();
        var dialog = await Running(app, Steam, Discord);
        var announced = Watch(dialog);

        dialog.SelectedRunning = Discord;

        Assert.Equal([nameof(AddAppViewModel.SelectedRunning), nameof(AddAppViewModel.CanAdd)], announced);
    }

    [Fact]
    public void ALanguageChangeRewordsTheDialogAtOnce()
    {
        using var culture = new UiCulture("en-US");
        using var app = Idle();
        var dialog = Open(app);
        dialog.Tab = AddAppTab.Shortcut;
        dialog.ChooseShortcut(Fixtures.ShortcutTo(Fixtures.Notepad));
        var leadsTo = dialog.ShortcutLeadsTo;
        var kind = dialog.MatchKinds[1];
        dialog.SelectedMatchKind = 1;
        var announced = Watch(dialog);

        app.Language.Choose(LanguageChoice.Russian);

        Assert.Equal($"Ведёт к: {Fixtures.Notepad}", dialog.ShortcutLeadsTo);
        Assert.NotEqual(leadsTo, dialog.ShortcutLeadsTo);
        Assert.NotEqual(kind.Name, dialog.MatchKinds[1].Name);
        Assert.Equal(1, dialog.SelectedMatchKind);
        Assert.Equal(1, announced.Count(name => name == nameof(AddAppViewModel.MatchKinds)));
        AnnouncedAfterIt(announced, nameof(AddAppViewModel.MatchKinds), nameof(AddAppViewModel.SelectedMatchKind));
        Assert.Contains(nameof(AddAppViewModel.MatchKindExplanation), announced);
        Assert.Contains(nameof(AddAppViewModel.ShortcutLeadsTo), announced);
    }

    [Fact]
    public void ALanguageChangeRewordsAShortcutRefusalAndSaysSo()
    {
        using var culture = new UiCulture("en-US");
        using var app = Idle();
        var dialog = Open(app);
        dialog.Tab = AddAppTab.Shortcut;
        dialog.ChooseShortcut(Fixtures.ShortcutTo(@"C:\nowhere\gone.exe"));
        var english = dialog.ShortcutRefusal;
        var announced = Watch(dialog);

        app.Language.Choose(LanguageChoice.Russian);

        Assert.NotEqual(english, dialog.ShortcutRefusal);
        Assert.Equal(Shortcut.Refusal(ShortcutOutcome.TargetMissing), dialog.ShortcutRefusal);
        Assert.Contains(nameof(AddAppViewModel.ShortcutRefusal), announced);
    }

    [Fact]
    public async Task ALanguageChangeRewordsTheRefusalInTheDialog()
    {
        using var culture = new UiCulture("en-US");
        using var app = Idle();
        var dialog = Open(app);
        dialog.Tab = AddAppTab.Name;
        dialog.Name = "csrss.exe";
        await dialog.AddCommand.ExecuteAsync(null);
        var english = dialog.Rules.Notice;
        var announced = Watch(dialog.Rules);

        app.Language.Choose(LanguageChoice.Russian);

        Assert.Contains("csrss.exe", dialog.Rules.Notice, StringComparison.Ordinal);
        Assert.NotEqual(english, dialog.Rules.Notice);
        Assert.Contains(nameof(RuleScreenViewModel.Notice), announced);
    }

    [Fact]
    public void AClosedDialogStopsListeningToTheLanguage()
    {
        using var culture = new UiCulture("en-US");
        using var app = Idle();
        var dialog = Open(app);
        var announced = Watch(dialog);

        dialog.Dispose();
        app.Language.Choose(LanguageChoice.Russian);

        Assert.Empty(announced);
    }

    /// <summary>The control drops its selection with the old items; a binding passes on only changed values, so the selection is said as none first.</summary>
    [Fact]
    public void ALanguageChangeSaysTheKindAsNoneAndThenAsWhatItIs()
    {
        using var culture = new UiCulture("en-US");
        using var app = Idle();
        var dialog = Open(app);
        dialog.SelectedMatchKind = 1;
        var read = new List<(string Name, int Kind)>();
        dialog.PropertyChanged += (_, e) => read.Add((e.PropertyName ?? string.Empty, dialog.SelectedMatchKind));

        app.Language.Choose(LanguageChoice.Russian);

        Assert.Equal(
            [(nameof(AddAppViewModel.MatchKinds), -1), (nameof(AddAppViewModel.SelectedMatchKind), -1), (nameof(AddAppViewModel.SelectedMatchKind), 1)],
            read.Take(3));
        Assert.Equal(1, dialog.SelectedMatchKind);
    }

    [Fact]
    public async Task NarrowingSaysThePickAsNoneAndThenAsWhatItIs()
    {
        using var app = Idle();
        var dialog = await Running(app, Steam, Discord);
        dialog.SelectedRunning = Discord;
        var read = new List<(string Name, RunningApp? Picked)>();
        dialog.PropertyChanged += (_, e) => read.Add((e.PropertyName ?? string.Empty, dialog.SelectedRunning));

        dialog.RunningFilter = "disc";

        Assert.Equal(
            [
                (nameof(AddAppViewModel.RunningFilter), Discord),
                (nameof(AddAppViewModel.VisibleRunning), null),
                (nameof(AddAppViewModel.SelectedRunning), null),
                (nameof(AddAppViewModel.SelectedRunning), Discord),
                (nameof(AddAppViewModel.CanAdd), Discord),
            ],
            read);
        Assert.True(dialog.CanAdd);
    }

    [Fact]
    public async Task ThePickStaysAddableWhileItsListIsBeingReplaced()
    {
        using var app = Idle();
        var dialog = await Running(app, Steam, Discord);
        dialog.SelectedRunning = Discord;
        var addable = new List<bool>();
        dialog.PropertyChanged += (_, _) => addable.Add(dialog.CanAdd);

        dialog.RunningFilter = "disc";

        Assert.DoesNotContain(false, addable);
    }

    /// <summary>The dialog shows the screen's notice as a refusal, so it opens without the page's own notice.</summary>
    [Fact]
    public async Task TheDialogOpensWithoutWhatThePageLastSaid()
    {
        using var app = Idle(acceptEverything: true);
        var rules = Rules(app);
        await rules.ApplyPresetCommand.ExecuteAsync(rules.Presets[0].Preset);
        Assert.True(rules.HasNotice);
        var announced = Watch(rules);

        var dialog = Open(app);

        Assert.False(dialog.Rules.HasNotice);
        Assert.Empty(dialog.Rules.Notice);
        Assert.Contains(nameof(RuleScreenViewModel.Notice), announced);
        Assert.Contains(nameof(RuleScreenViewModel.HasNotice), announced);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARefusalDoesNotOutliveTheChoiceItWasAbout(bool fromTheService)
    {
        using var app = Idle();
        var dialog = Open(app);
        dialog.Tab = AddAppTab.Name;
        dialog.Name = fromTheService ? "steam.exe" : "csrss.exe";
        await dialog.AddCommand.ExecuteAsync(null);
        Assert.True(dialog.Rules.HasNotice);
        var announced = Watch(dialog.Rules);

        dialog.Name = "game.exe";

        Assert.False(dialog.Rules.HasNotice);
        Assert.Contains(nameof(RuleScreenViewModel.Notice), announced);
        Assert.Contains(nameof(RuleScreenViewModel.HasNotice), announced);
    }

    [Fact]
    public void ChoosingWithNoNoticeUpSaysNothingOnTheScreen()
    {
        using var app = Idle();
        var announced = Watch(Rules(app));

        var dialog = Open(app);
        dialog.Tab = AddAppTab.Name;
        dialog.Name = "steam.exe";
        dialog.Rules.ForgetNotice();

        Assert.Empty(announced);
    }

    /// <summary>The end of a path is the program, so long paths are wrapped before they are cut and cannot widen the dialog.</summary>
    [Fact]
    public void AChosenPathIsWrappedBeforeItIsCut()
    {
        var markup = File.ReadAllText(Repo.App("Views", "AddAppDialog.axaml"));

        foreach (var bound in new[] { "ChosenExecutable", "ShortcutLeadsTo" })
        {
            Assert.Matches(
                @"<TextBlock[^>]*Text=""\{Binding\s+" + bound + @"\}""[^>]*TextTrimming=""CharacterEllipsis""[^>]*TextWrapping=""Wrap""[^>]*MaxLines=""4""",
                markup);
        }
    }

    [Fact]
    public void TheDialogIsAModalWindowOfTheSizeTheDesignGives()
    {
        var markup = File.ReadAllText(Repo.App("Views", "AddAppDialog.axaml"));
        var window = Regex.Match(markup, @"<Window\b[^>]*>", RegexOptions.Singleline).Value;

        Assert.Contains(@"Width=""560""", window);
        Assert.Contains(@"Height=""520""", window);
        Assert.Contains(@"CanResize=""False""", window);
        Assert.Contains(@"WindowStartupLocation=""CenterOwner""", window);
        Assert.Contains(@"ShowInTaskbar=""False""", window);
        Assert.Contains(@"Title=""{Binding Rules.Text.AddAppTitle}""", window);
        Assert.Matches(@"<TabControl[^>]*SelectedIndex=""\{Binding\s+TabIndex\b", markup);
    }

    [Fact]
    public void TheDialogShowsTheRefusalAndHasBothButtons()
    {
        var markup = File.ReadAllText(Repo.App("Views", "AddAppDialog.axaml"));

        Assert.Matches(
            @"<TextBlock[^>]*Foreground=""\{DynamicResource Chronos\.Error\}""[^>]*Text=""\{Binding\s+Rules\.Notice\}""[^>]*IsVisible=""\{Binding\s+Rules\.HasNotice\}""",
            markup);
        Assert.Matches(
            @"<TextBlock[^>]*Foreground=""\{DynamicResource Chronos\.Error\}""[^>]*Text=""\{Binding\s+ShortcutRefusal\}""",
            markup);
        Assert.Matches(@"Text=""\{Binding\s+ShortcutLeadsTo\}""", markup);
        Assert.Matches(@"<Button[^>]*Content=""\{Binding\s+Rules\.Text\.Cancel\}""[^>]*IsCancel=""True""", markup);
        Assert.Matches(
            @"<Button[^>]*Classes=""primary""[^>]*Content=""\{Binding\s+Rules\.Text\.AddApp\}""[^>]*IsDefault=""True""[^>]*Command=""\{Binding\s+AddCommand\}""",
            markup);

        // The second kind is turned off on the name tab, not taken away.
        Assert.Matches(@"IsEnabled""\s+Value=""\{Binding[^""]*\bCanChooseFullPath\}""", markup);
    }

    [Fact]
    public void LongNamesAndPathsAreCutRatherThanWideningTheDialog()
    {
        var markup = File.ReadAllText(Repo.App("Views", "AddAppDialog.axaml"));

        foreach (var bound in new[] { "Name", "Path", "ChosenExecutable", "ChosenShortcut", "ShortcutLeadsTo" })
        {
            Assert.Matches(
                @"<TextBlock[^>]*Text=""\{Binding\s+" + bound + @"\}""[^>]*TextTrimming=""CharacterEllipsis""",
                markup);
        }

        Assert.Matches(
            @"<ListBox[^>]*ItemsSource=""\{Binding\s+VisibleRunning\}""[^>]*ScrollViewer\.HorizontalScrollBarVisibility=""Disabled""",
            markup);
        Assert.Matches(
            @"<ListBox[^>]*ItemsSource=""\{Binding\s+MatchKinds\}""[^>]*ScrollViewer\.HorizontalScrollBarVisibility=""Disabled""",
            markup);
    }

    [Fact]
    public void TheAppsPageOpensTheDialogAndAddsNothingItself()
    {
        var markup = File.ReadAllText(Repo.App("Views", "AppsPage.axaml"));
        var code = File.ReadAllText(Repo.App("Views", "AppsPage.axaml.cs"));

        Assert.Matches(@"<Button[^>]*Classes=""primary""[^>]*Content=""\{Binding\s+Rules\.Text\.AddApp\}""", markup);
        Assert.Single(Regex.Matches(markup, @"Classes=""primary"""));

        foreach (var gone in new[] { "MatchKinds", "RunningApps", "NewAppName", "OnChooseExecutable", "OnChooseShortcut", "ListRunning" })
        {
            Assert.DoesNotContain(gone, markup, StringComparison.Ordinal);
        }

        Assert.Contains("new AddAppViewModel(section.Rules)", code, StringComparison.Ordinal);
        Assert.Contains("new AddAppDialog", code, StringComparison.Ordinal);
        Assert.Contains("ShowDialog(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDialogClosesWhenTheViewModelIsDone()
    {
        var code = File.ReadAllText(Repo.App("Views", "AddAppDialog.axaml.cs"));

        Assert.Matches(@"\.Done\s*\+=", code);
        Assert.Contains("Close()", code, StringComparison.Ordinal);
    }

    /// <summary>An idle product. Unless told to accept everything, the service refuses every app rule the interface lets through.</summary>
    private static AppUnderTest Idle(bool acceptEverything = false)
    {
        var app = new AppUnderTest(Moment);

        if (!acceptEverything)
        {
            app.Link.Answer = request => request.Command == ServiceCommand.AddAppRule
                ? IpcResponse.Fail(IpcCodes.RulesAppProtected)
                : IpcResponse.Ok();
        }

        app.Show(Say.Status(EngineState.Idle, Moment));

        return app;
    }

    private static RuleScreenViewModel Rules(AppUnderTest app) =>
        Assert.IsAssignableFrom<RuleScreenViewModel>(app.Shell.CurrentScreen);

    private static AddAppViewModel Open(AppUnderTest app) => new(Rules(app), () => []);

    private static async Task<AddAppViewModel> Running(AppUnderTest app, params RunningApp[] running)
    {
        var dialog = new AddAppViewModel(Rules(app), () => running);
        await dialog.LoadRunningCommand.ExecuteAsync(null);

        return dialog;
    }

    private static void Choose(AddAppViewModel dialog, AddAppTab tab)
    {
        switch (tab)
        {
            case AddAppTab.Running:
                dialog.SelectedRunning = Steam;
                break;
            case AddAppTab.Executable:
                dialog.ChooseExecutable(Fixtures.Notepad);
                break;
            case AddAppTab.Shortcut:
                dialog.ChooseShortcut(Fixtures.ShortcutTo(Fixtures.Notepad));
                break;
            default:
                dialog.Name = "steam.exe";
                break;
        }
    }

    private static int IndexOf(string kind)
    {
        var at = AppMatchKinds.Kinds.ToList().IndexOf(kind);
        Assert.True(at >= 0, $"{kind} is not one of the match kinds.");

        return at;
    }

    private static void AnnouncedAfterIt(List<string> announced, string list, string selection)
    {
        var items = announced.IndexOf(list);
        var chosen = announced.LastIndexOf(selection);

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
