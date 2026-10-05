using System.Text.RegularExpressions;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.ViewModels;
using Chronos.Ipc;

namespace Chronos.App.Tests;

/// <summary>
/// What a screen reader says for an entry built from a record: its name, never the record's
/// <c>ToString()</c>. Read off the markup, as the other markup tests are.
/// </summary>
[Collection(CultureBound.Name)]
public sealed class AutomationNameTests
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);

    private static string Markup(params string[] parts) => File.ReadAllText(Repo.App(parts));

    // The ListBox with this class, up to its end tag.
    private static string List(string markup, string classes)
    {
        var match = Regex.Match(markup, $@"<ListBox Classes=""{classes}"".*?</ListBox>", RegexOptions.Singleline);
        Assert.True(match.Success, $"No ListBox.{classes}.");

        return match.Value;
    }

    private static void NamesItsItems(string list, string type)
    {
        Assert.Matches(
            $@"<Style Selector=""ListBoxItem"" x:DataType=""{Regex.Escape(type)}"">\s*<Setter Property=""AutomationProperties\.Name"" Value=""\{{Binding Name\}}"" />",
            list);
    }

    [Fact]
    public void TheSidebarEntriesAreReadAsTheSectionName() =>
        NamesItsItems(List(Markup("MainWindow.axaml"), "nav"), "vm:SectionLine");

    [Fact]
    public void TheDurationChipsAreReadAsTheLength() =>
        NamesItsItems(List(Markup("Views", "SessionSetupPage.axaml"), "chips"), "vm:DurationLine");

    [Fact]
    public void TheMatchKindsAreReadAsTheKind() =>
        NamesItsItems(List(Markup("Views", "AddAppDialog.axaml"), "kinds"), "apps:MatchKindLine");

    [Fact]
    public void TheRunningProgramsAreReadAsTheirName() =>
        NamesItsItems(List(Markup("Views", "AddAppDialog.axaml"), "running"), "apps:RunningApp");

    [Fact]
    public void TheProtectionSummaryIsReadAsTheSummary()
    {
        Assert.Matches(
            @"<Button DockPanel\.Dock=""Bottom""\s+Classes=""summary""[^>]*AutomationProperties\.Name=""\{Binding Settings\.ProtectionSummary\}""",
            Markup("MainWindow.axaml"));
    }

    [Theory]
    [InlineData("SitesPage.axaml")]
    [InlineData("AppsPage.axaml")]
    public void ARemoveButtonIsReadAsWhatItRemoves(string page)
    {
        Assert.Matches(
            @"<Button Classes=""remove""[^>]*AutomationProperties\.Name=""\{Binding RemoveName\}""",
            Markup("Views", page));
    }

    [Theory]
    [InlineData("en-US", "Remove example.com", "Remove game.exe")]
    [InlineData("ru-RU", "Убрать example.com", "Убрать game.exe")]
    public void ALineNamesItsRemovalWithTheRule(string language, string site, string app)
    {
        using var culture = new UiCulture(language);
        using var under = Lists();

        var rules = (RuleScreenViewModel)under.Shell.CurrentScreen!;

        Assert.Equal(site, rules.SiteLines.Single().RemoveName);
        Assert.Equal(app, rules.AppLines.Single().RemoveName);
    }

    [Fact]
    public void ALanguageChangeRenamesTheRemovals()
    {
        using var culture = new UiCulture("en-US");
        using var under = Lists();
        var rules = (RuleScreenViewModel)under.Shell.CurrentScreen!;

        under.Language.Use("ru-RU");

        Assert.Equal("Убрать example.com", rules.SiteLines.Single().RemoveName);
        Assert.Equal("Убрать game.exe", rules.AppLines.Single().RemoveName);
    }

    private static AppUnderTest Lists()
    {
        var app = new AppUnderTest(Moment);
        var config = Say.Config(["example.com"], ["game.exe"]) with { Language = "system" };
        app.Link.Answer = _ => IpcResponse.Ok(Say.Status(EngineState.Idle, Moment), config);
        app.Show(Say.Status(EngineState.Idle, Moment));

        return app;
    }
}
