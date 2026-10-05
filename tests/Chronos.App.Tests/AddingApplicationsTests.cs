using Chronos.App.Time;
using System.Text.RegularExpressions;
using Chronos.App.Apps;
using Chronos.App.Localization;
using Chronos.App.ViewModels;
using Chronos.Ipc;

namespace Chronos.App.Tests;

/// <summary>The ways to add a program in the dialog, and the match kind explained. A rule for a <c>.lnk</c> blocks nothing, since the shell reads the shortcut.</summary>
[Collection(CultureBound.Name)]
public sealed class AddingApplicationsTests : IDisposable
{
    private static readonly DateTimeOffset Moment = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    // Built before a test names its culture, so the tests that change it put it into English first.
    private readonly LanguageSwitch _language = new();

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "chronos-adding-" + Guid.NewGuid().ToString("N"));

    // The screens and dialogs a test opened, let go of with the test.
    private readonly List<IDisposable> _opened = [];

    public AddingApplicationsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        foreach (var opened in _opened)
        {
            opened.Dispose();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // The machine's business, not this test's.
        }
    }

    // The shortcut is named nothing like its target, so a rule from the shortcut cannot pass for one from the program.
    [Fact]
    public async Task AShortcutMakesARuleForTheProgramAndNotForTheShortcut()
    {
        var program = Make("game.exe");
        var shortcut = Link("Play the Game.lnk", program);

        var link = new RecordingLink();
        var dialog = Open(link, AddAppTab.Shortcut);

        dialog.ChooseShortcut(shortcut);
        await dialog.AddCommand.ExecuteAsync(null);

        var rule = Assert.Single(link.Sent, request => request.Command == "AddAppRule").App;
        Assert.NotNull(rule);
        Assert.Equal("game.exe", rule!.Value);
        Assert.DoesNotContain(".lnk", rule.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(dialog.Rules.Notice);
        Assert.Empty(dialog.ShortcutRefusal);
    }

    [Fact]
    public async Task AShortcutMatchedByPathNamesTheProgramsPath()
    {
        var program = Make("game.exe");
        var shortcut = Link("Play the Game.lnk", program);

        var link = new RecordingLink();
        var dialog = Open(link, AddAppTab.Shortcut);
        dialog.SelectedMatchKind = IndexOf("FullPath");

        dialog.ChooseShortcut(shortcut);
        await dialog.AddCommand.ExecuteAsync(null);

        var rule = Assert.Single(link.Sent, request => request.Command == "AddAppRule").App;
        Assert.Equal("FullPath", rule!.MatchKind);
        Assert.Equal(program, rule.Value, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AShortcutWhoseProgramIsGoneMakesNoRuleAndSaysWhy()
    {
        var program = Make("gone.exe");
        var shortcut = Link("Gone.lnk", program);
        File.Delete(program);

        var link = new RecordingLink();
        var dialog = Open(link, AddAppTab.Shortcut);

        dialog.ChooseShortcut(shortcut);
        await dialog.AddCommand.ExecuteAsync(null);

        Assert.DoesNotContain(link.Sent, request => request.Command == "AddAppRule");
        Assert.Equal(Shortcut.Refusal(ShortcutOutcome.TargetMissing), dialog.ShortcutRefusal);
        Assert.NotEmpty(dialog.ShortcutRefusal);
    }

    [Fact]
    public async Task AFileThatIsNotAShortcutMakesNoRuleAndSaysWhy()
    {
        var link = new RecordingLink();
        var dialog = Open(link, AddAppTab.Shortcut);

        dialog.ChooseShortcut(Make("notes.txt"));
        await dialog.AddCommand.ExecuteAsync(null);

        Assert.DoesNotContain(link.Sent, request => request.Command == "AddAppRule");
        Assert.Equal(Shortcut.Refusal(ShortcutOutcome.NotAShortcut), dialog.ShortcutRefusal);
    }

    [Fact]
    public async Task AShortcutRefusalIsRewrittenWhenTheLanguageChanges()
    {
        using var restore = new UiCulture("en-US");
        _language.Use("en-US");

        var link = new RecordingLink();
        var dialog = Open(link, AddAppTab.Shortcut);

        dialog.ChooseShortcut(Make("notes.txt"));
        await dialog.AddCommand.ExecuteAsync(null);

        var english = dialog.ShortcutRefusal;
        _language.Use("ru-RU");

        Assert.NotEqual(english, dialog.ShortcutRefusal);
        Assert.False(string.IsNullOrWhiteSpace(dialog.ShortcutRefusal));
    }

    [Fact]
    public async Task PickingNothingChangesNothing()
    {
        var link = new RecordingLink();
        var dialog = Open(link, AddAppTab.Shortcut);

        dialog.ChooseShortcut(null!);
        await dialog.AddCommand.ExecuteAsync(null);

        dialog.Tab = AddAppTab.Executable;
        dialog.ChooseExecutable(null!);
        await dialog.AddCommand.ExecuteAsync(null);
        dialog.ChooseExecutable("   ");
        await dialog.AddCommand.ExecuteAsync(null);

        Assert.DoesNotContain(link.Sent, request => request.Command == "AddAppRule");
        Assert.Empty(dialog.Rules.Notice);
        Assert.Empty(dialog.ShortcutRefusal);
    }

    [Fact]
    public async Task AProgramPickedFromDiskBecomesARuleForItsName()
    {
        var link = new RecordingLink();
        var dialog = Open(link, AddAppTab.Executable);

        dialog.ChooseExecutable(@"D:\Games\Steam\steamapps\game.exe");
        await dialog.AddCommand.ExecuteAsync(null);

        var rule = Assert.Single(link.Sent, request => request.Command == "AddAppRule").App;
        Assert.Equal("FileName", rule!.MatchKind);
        Assert.Equal("game.exe", rule.Value);
    }

    [Fact]
    public async Task AProgramPickedFromDiskCanBeMatchedByItsFullPathInstead()
    {
        var link = new RecordingLink();
        var dialog = Open(link, AddAppTab.Executable);
        dialog.SelectedMatchKind = IndexOf("FullPath");

        dialog.ChooseExecutable(@"D:\Games\Steam\steamapps\game.exe");
        await dialog.AddCommand.ExecuteAsync(null);

        var rule = Assert.Single(link.Sent, request => request.Command == "AddAppRule").App;
        Assert.Equal("FullPath", rule!.MatchKind);
        Assert.Equal(@"D:\Games\Steam\steamapps\game.exe", rule.Value);
    }

    [Fact]
    public async Task AProgramPickedFromWhatIsRunningBecomesARule()
    {
        var link = new RecordingLink();
        var dialog = Open(link, AddAppTab.Running);

        dialog.SelectedRunning = new RunningApp("game.exe", @"D:\Games\game.exe");
        await dialog.AddCommand.ExecuteAsync(null);

        var rule = Assert.Single(link.Sent, request => request.Command == "AddAppRule").App;
        Assert.Equal("game.exe", rule!.Value);
    }

    /// <summary>The running list is filled when asked for, not on every status.</summary>
    [Fact]
    public async Task WhatIsRunningIsListedWhenItIsAskedForAndNotBefore()
    {
        var link = new RecordingLink();
        var dialog = Open(link, AddAppTab.Running);

        Assert.Empty(dialog.RunningApps);
        Assert.False(dialog.AnyRunningListed);

        await dialog.LoadRunningCommand.ExecuteAsync(null);

        Assert.NotEmpty(dialog.RunningApps);
        Assert.True(dialog.AnyRunningListed);
    }

    /// <summary>The protected-program check is one place, so all three ways refuse it.</summary>
    [Fact]
    public async Task AProtectedProgramIsRefusedWhicheverOfTheThreeWaysItArrivesBy()
    {
        var protectedProgram = Make("explorer.exe");
        var shortcut = Link("File Explorer.lnk", protectedProgram);

        var link = new RecordingLink();
        var dialog = Open(link, AddAppTab.Running);

        dialog.SelectedRunning = new RunningApp("explorer.exe", @"C:\Windows\explorer.exe");
        await dialog.AddCommand.ExecuteAsync(null);
        Assert.True(dialog.Rules.HasNotice);

        dialog.Tab = AddAppTab.Executable;
        dialog.ChooseExecutable(@"C:\Windows\explorer.exe");
        await dialog.AddCommand.ExecuteAsync(null);
        Assert.True(dialog.Rules.HasNotice);

        dialog.Tab = AddAppTab.Shortcut;
        dialog.ChooseShortcut(shortcut);
        await dialog.AddCommand.ExecuteAsync(null);
        Assert.True(dialog.Rules.HasNotice);

        // Not one of the three reached the service.
        Assert.DoesNotContain(link.Sent, request => request.Command == "AddAppRule");
    }

    [Fact]
    public async Task AProtectedProgramIsRefusedByPathToo()
    {
        var link = new RecordingLink();
        var dialog = Open(link, AddAppTab.Executable);
        dialog.SelectedMatchKind = IndexOf("FullPath");

        dialog.ChooseExecutable(@"C:\Windows\explorer.exe");
        await dialog.AddCommand.ExecuteAsync(null);

        Assert.DoesNotContain(link.Sent, request => request.Command == "AddAppRule");
        Assert.True(dialog.Rules.HasNotice);
    }

    /// <summary>Both kinds are offered, named, and explained.</summary>
    [Fact]
    public void BothMatchKindsAreOfferedWithAnExplanationOfEach()
    {
        var link = new RecordingLink();
        var dialog = Open(link);

        Assert.Equal(2, dialog.MatchKinds.Count);
        Assert.Equal(["FileName", "FullPath"], dialog.MatchKinds.Select(kind => kind.Kind));

        foreach (var kind in dialog.MatchKinds)
        {
            Assert.False(string.IsNullOrWhiteSpace(kind.Name), $"{kind.Kind} has no name.");
            Assert.False(string.IsNullOrWhiteSpace(kind.Explanation), $"{kind.Kind} has no explanation.");

            // An explanation that is the label again explains nothing.
            Assert.NotEqual(kind.Name, kind.Explanation);
        }

        Assert.NotEqual(
            dialog.MatchKinds[0].Explanation,
            dialog.MatchKinds[1].Explanation,
            StringComparer.Ordinal);
    }

    [Fact]
    public void TheExplanationOnTheScreenFollowsTheChoice()
    {
        var link = new RecordingLink();
        var dialog = Open(link);

        Assert.Equal(dialog.MatchKinds[0].Explanation, dialog.MatchKindExplanation);

        var first = dialog.MatchKindExplanation;
        dialog.SelectedMatchKind = 1;

        Assert.Equal(dialog.MatchKinds[1].Explanation, dialog.MatchKindExplanation);

        // A really different sentence, not two readings of the same empty one.
        Assert.NotEqual(first, dialog.MatchKindExplanation);
    }

    [Fact]
    public void TheKindOfferedFirstIsTheOneThatSurvivesAReinstall()
    {
        var link = new RecordingLink();
        var dialog = Open(link);

        Assert.Equal(0, dialog.SelectedMatchKind);
        Assert.Equal("FileName", dialog.MatchKinds[dialog.SelectedMatchKind].Kind);
    }

    [Fact]
    public void ChangingTheLanguageRewritesTheKindsAndTheirExplanations()
    {
        using var restore = new UiCulture("en-US");
        _language.Use("en-US");

        var link = new RecordingLink();
        var dialog = Open(link);

        var english = dialog.MatchKinds[0];
        var explained = dialog.MatchKindExplanation;

        _language.Use("ru-RU");

        Assert.NotEqual(english.Name, dialog.MatchKinds[0].Name);
        Assert.NotEqual(english.Explanation, dialog.MatchKinds[0].Explanation);
        Assert.NotEqual(explained, dialog.MatchKindExplanation);
    }

    /// <summary>A selection outside the list, which a list control reports while it has none, leaves the choice alone.</summary>
    [Fact]
    public async Task ASelectionThatIsNotOneOfTheKindsIsIgnored()
    {
        var link = new RecordingLink();
        var dialog = Open(link, AddAppTab.Executable);

        dialog.SelectedMatchKind = -1;
        dialog.SelectedMatchKind = 7;

        dialog.ChooseExecutable(@"D:\Games\game.exe");
        await dialog.AddCommand.ExecuteAsync(null);

        var rule = Assert.Single(link.Sent, request => request.Command == "AddAppRule").App;
        Assert.Equal("FileName", rule!.MatchKind);
    }

    /// <summary>One tab per way, with the explanation beside each kind. Read off the markup.</summary>
    [Fact]
    public void TheScreenOffersAllThreeWaysAndShowsTheExplanation()
    {
        var markup = File.ReadAllText(Path.Combine(Markup(), "Views", "AddAppDialog.axaml"));
        var code = File.ReadAllText(Path.Combine(Markup(), "Views", "AddAppDialog.axaml.cs"));

        Assert.Equal(4, Regex.Matches(markup, @"<TabItem\b").Count);

        foreach (var tab in new[] { "TabRunning", "TabExecutable", "TabShortcut", "TabName" })
        {
            Assert.Matches(new Regex(@"<TabItem\s+Header=""\{Binding\s+Rules\.Text\." + tab + @"\}"""), markup);
        }

        // Both kinds, each with its sentence under its name.
        Assert.Matches(new Regex(@"ItemsSource=""\{Binding\s+MatchKinds\}"""), markup);
        Assert.Matches(new Regex(@"SelectedIndex=""\{Binding\s+SelectedMatchKind\b"), markup);
        Assert.Matches(new Regex(@"Text=""\{Binding\s+Name\}"""), markup);
        Assert.Matches(new Regex(@"Text=""\{Binding\s+Explanation\}"""), markup);

        // The running list is picked from, not added from line by line.
        Assert.Matches(new Regex(@"ItemsSource=""\{Binding\s+VisibleRunning\}"""), markup);
        Assert.Matches(new Regex(@"SelectedItem=""\{Binding\s+SelectedRunning\b"), markup);
        Assert.Contains("LoadRunningCommand", code, StringComparison.Ordinal);

        // The two that open a picker are reached from the code behind; the view model may not know a control exists.
        Assert.Contains("ChooseExecutable(", code, StringComparison.Ordinal);
        Assert.Contains("ChooseShortcut(", code, StringComparison.Ordinal);
    }

    private static int IndexOf(string kind)
    {
        var at = AppMatchKinds.Kinds.ToList().IndexOf(kind);
        Assert.True(at >= 0, $"{kind} is not one of the match kinds.");

        return at;
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

    private AddAppViewModel Open(RecordingLink link, AddAppTab tab = AddAppTab.Running)
    {
        var screen = new SetupViewModel(link, new Text(_language), new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));
        screen.Show(Say.Status("Idle", Moment));

        var dialog = new AddAppViewModel(screen) { Tab = tab };

        _opened.Add(dialog);
        _opened.Add(screen);

        return dialog;
    }

    private string Make(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "a file, for the purposes of this test");

        return path;
    }

    private string Link(string name, string target) =>
        Fixtures.ShortcutTo(target, Path.Combine(_directory, name));
}
