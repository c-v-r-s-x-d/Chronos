using System.ComponentModel;
using System.Globalization;
using Chronos.App;
using Chronos.App.Apps;
using Chronos.App.Localization;
using Chronos.App.Resources;
using Chronos.App.Services;
using Chronos.App.ViewModels;
using Chronos.Ipc;

namespace Chronos.App.Tests;

/// <summary>The two list sections, their search and their lines, and the presets.</summary>
[Collection(CultureBound.Name)]
public sealed class ListSectionTests
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task APresetAddsOnlyWhatIsMissingAndSaysHowMany()
    {
        using var culture = new UiCulture("ru-RU");
        var social = Presets.All.Single(p => p.Id == Presets.Social);
        using var app = Lists(sites: social.Domains.Take(3));
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;

        await rules.ApplyPresetCommand.ExecuteAsync(rules.Presets.Single(p => p.Preset.Id == Presets.Social).Preset);

        var added = app.Link.Sent.Where(r => r.Command == ServiceCommand.AddSiteRule).Select(r => r.Site!.Domain).ToArray();
        Assert.Equal(social.Domains.Skip(3), added);
        Assert.Equal("Добавлено: 7. Уже были: 3.", rules.Notice);
    }

    [Fact]
    public async Task APresetWhoseDomainsAreAllThereAddsNothingAndSaysSo()
    {
        using var culture = new UiCulture("en-US");
        var social = Presets.All.Single(p => p.Id == Presets.Social);
        using var app = Lists(sites: social.Domains.Select(d => d.ToUpperInvariant()));
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;

        await rules.ApplyPresetCommand.ExecuteAsync(social);

        Assert.DoesNotContain(app.Link.Sent, r => r.Command == ServiceCommand.AddSiteRule);
        Assert.Equal($"Added: 0. Already there: {social.Domains.Count}.", rules.Notice);
    }

    [Fact]
    public async Task APresetDuringASessionIsStillAdded()
    {
        using var app = Lists(state: EngineState.Active);
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;

        await rules.ApplyPresetCommand.ExecuteAsync(Presets.All[0]);

        Assert.Contains(app.Link.Sent, r => r.Command == ServiceCommand.AddSiteRule);
    }

    [Fact]
    public async Task APresetThatLosesTheServiceCountsOnlyWhatWasAccepted()
    {
        using var culture = new UiCulture("en-US");
        using var app = Lists();
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;
        var calls = 0;
        var have = new List<string>();
        app.Link.Answer = r => r.Command == ServiceCommand.AddSiteRule && ++calls > 2 ? null : Accepted(r, have);

        await rules.ApplyPresetCommand.ExecuteAsync(Presets.All.Single(p => p.Id == Presets.Video));

        Assert.Equal(string.Format(CultureInfo.CurrentUICulture, Strings.PresetOutcomeFormat, 2, 0), rules.Notice);
    }

    [Fact]
    public async Task APresetStoppedByARefusalShowsTheServicesWordsAndNoCount()
    {
        using var culture = new UiCulture("en-US");
        using var app = Lists();
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;
        var calls = 0;
        var have = new List<string>();
        app.Link.Answer = r => r.Command == ServiceCommand.AddSiteRule && ++calls > 1
            ? IpcResponse.Fail(IpcCodes.SessionEmptyBlockList)
            : Accepted(r, have);

        await rules.ApplyPresetCommand.ExecuteAsync(Presets.All.Single(p => p.Id == Presets.Video));

        Assert.Equal(ServiceNotice.Text(IpcCodes.SessionEmptyBlockList, []), rules.Notice);
        Assert.Equal(2, app.Link.Sent.Count(r => r.Command == ServiceCommand.AddSiteRule));
    }

    [Fact]
    public async Task ThePresetOutcomeIsAnnouncedAndFollowsTheLanguage()
    {
        using var culture = new UiCulture("en-US");
        using var app = Lists();
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;
        await rules.ApplyPresetCommand.ExecuteAsync(Presets.All[0]);
        var english = rules.Notice;
        var announced = Watch(rules);

        app.Text.SwitchTo(LanguageChoice.Russian);

        Assert.NotEqual(english, rules.Notice);
        Assert.Contains(nameof(RuleScreenViewModel.Notice), announced);
        Assert.Matches(@"^Добавлено: \d+\. Уже были: \d+\.$", rules.Notice);
    }

    [Fact]
    public async Task ThePresetOutcomeAnnouncesTheNoticeAndThatThereIsOne()
    {
        using var app = Lists();
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;
        var announced = Watch(rules);

        await rules.ApplyPresetCommand.ExecuteAsync(Presets.All[0]);

        Assert.Contains(nameof(RuleScreenViewModel.Notice), announced);
        Assert.Contains(nameof(RuleScreenViewModel.HasNotice), announced);
        Assert.True(rules.HasNotice);
    }

    [Fact]
    public async Task AnotherCommandAfterAPresetRemovesItsOutcome()
    {
        using var app = Lists();
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;
        await rules.ApplyPresetCommand.ExecuteAsync(Presets.All[0]);

        rules.NewSiteDomain = "extra.example";
        await rules.AddSiteCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, rules.Notice);
    }

    [Fact]
    public void ThePresetsAreOnTheScreenInBothLanguagesAndAnnouncedOnAChange()
    {
        using var culture = new UiCulture("en-US");
        using var app = Lists();
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;
        var first = rules.Presets[0].Name;
        var announced = Watch(rules);

        app.Text.SwitchTo(LanguageChoice.Russian);

        Assert.NotEqual(first, rules.Presets[0].Name);
        Assert.Contains(nameof(RuleScreenViewModel.Presets), announced);
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(9, true)]
    public void SearchAppearsOnlyForALongList(int count, bool shows)
    {
        using var app = Lists(
            sites: Enumerable.Range(0, count).Select(i => $"s{i}.example"),
            apps: Enumerable.Range(0, count).Select(i => $"a{i}.exe"));
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;

        Assert.Equal(shows, rules.ShowsSiteSearch);
        Assert.Equal(shows, rules.ShowsAppSearch);
    }

    [Fact]
    public void TheSearchAndTheLinesAreAnnouncedWhenTheListsArrive()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment));
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;
        var announced = Watch(rules);
        app.Link.Answer = _ => IpcResponse.Ok(
            Say.Status(EngineState.Idle, Moment),
            Config(
                sites: Enumerable.Range(0, 9).Select(i => $"s{i}.example").ToList(),
                apps: Enumerable.Range(0, 9).Select(i => $"a{i}.exe").ToList()));

        app.Show(Say.Status(EngineState.Idle, Moment));

        foreach (var name in new[]
        {
            nameof(RuleScreenViewModel.SiteLines),
            nameof(RuleScreenViewModel.AppLines),
            nameof(RuleScreenViewModel.ShowsSiteSearch),
            nameof(RuleScreenViewModel.ShowsAppSearch),
        })
        {
            Assert.Contains(name, announced);
        }
    }

    [Fact]
    public void TheFilterNarrowsTheLinesAndSurvivesAStatus()
    {
        using var app = Lists(sites: ["reddit.com", "vk.com", "youtube.com"]);
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;
        var announced = Watch(rules);

        rules.SiteFilter = "TUB";
        app.Show(Say.Status(EngineState.Idle, Moment));

        Assert.Equal("TUB", rules.SiteFilter);
        Assert.Equal(["youtube.com"], rules.SiteLines.Select(l => l.Rule.Domain));
        Assert.Contains(nameof(RuleScreenViewModel.SiteFilter), announced);
        Assert.Contains(nameof(RuleScreenViewModel.SiteLines), announced);
    }

    [Fact]
    public void TheAppFilterNarrowsTheAppLinesAndSurvivesAStatus()
    {
        using var app = Lists(apps: ["steam.exe", "discord.exe"]);
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;
        var announced = Watch(rules);

        rules.AppFilter = "dis";
        app.Show(Say.Status(EngineState.Idle, Moment));

        Assert.Equal("dis", rules.AppFilter);
        Assert.Equal(["discord.exe"], rules.AppLines.Select(l => l.Rule.Value));
        Assert.Contains(nameof(RuleScreenViewModel.AppFilter), announced);
        Assert.Contains(nameof(RuleScreenViewModel.AppLines), announced);
    }

    [Fact]
    public void ALineSaysTheFirstLetterOrDigitOfTheName()
    {
        using var app = Lists(sites: ["1password.com", "-x.example"], apps: ["steam.exe"]);
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;

        Assert.Equal(["1", "X"], rules.SiteLines.Select(l => l.Letter));
        Assert.Equal("S", rules.AppLines.Single().Letter);
    }

    [Fact]
    public void EachAppLineSaysHowItMatches()
    {
        using var culture = new UiCulture("ru-RU");
        using var app = Lists(apps: ["steam.exe"]);
        var line = ((RuleScreenViewModel)app.Shell.CurrentScreen!).AppLines.Single();

        Assert.Equal(AppMatchKinds.Name(AppMatch.FileName), line.Kind);
        Assert.Equal("S", line.Letter);
    }

    [Fact]
    public void TheKindOnEachLineFollowsTheLanguage()
    {
        using var culture = new UiCulture("en-US");
        using var app = Lists(apps: ["steam.exe"]);
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;
        var english = rules.AppLines.Single().Kind;
        var announced = Watch(rules);

        app.Text.SwitchTo(LanguageChoice.Russian);

        Assert.NotEqual(english, rules.AppLines.Single().Kind);
        Assert.Contains(nameof(RuleScreenViewModel.AppLines), announced);
        Assert.Contains(nameof(RuleScreenViewModel.SiteLines), announced);
    }

    [Fact]
    public void ALongDomainIsCutRatherThanWideningTheWindow()
    {
        var markup = File.ReadAllText(Repo.App("Views", "SitesPage.axaml"));

        Assert.Matches(@"<TextBlock[^>]*Text=""\{Binding\s+Rule\.Domain\}""[^>]*TextTrimming=""CharacterEllipsis""", markup);
    }

    [Fact]
    public void ALongAppNameIsCutToo()
    {
        var markup = File.ReadAllText(Repo.App("Views", "AppsPage.axaml"));

        Assert.Matches(@"<TextBlock[^>]*Text=""\{Binding\s+Rule\.Value\}""[^>]*TextTrimming=""CharacterEllipsis""", markup);
    }

    [Theory]
    [InlineData("SitesPage.axaml", "Site")]
    [InlineData("AppsPage.axaml", "App")]
    public void ThePageOffersSearchLinesAndTheNotice(string page, string kind)
    {
        var markup = File.ReadAllText(Repo.App("Views", page));

        Assert.Matches(@"IsVisible=""\{Binding\s+Rules\.Shows" + kind + @"Search\}""", markup);
        Assert.Matches(@"Text=""\{Binding\s+Rules\." + kind + @"Filter", markup);
        Assert.Matches(@"ItemsSource=""\{Binding\s+Rules\." + kind + @"Lines\}""", markup);
        Assert.Matches(@"CommandParameter=""\{Binding\s+Rule\}""", markup);
        Assert.Matches(@"\{Binding\s+Rules\.Notice\}", markup);
        Assert.Matches(@"\{Binding\s+Rules\.Text\.SearchPlaceholder\}", markup);
    }

    [Fact]
    public void ThePresetsAreChipsOnTheSitesPageOnly()
    {
        Assert.Contains("Rules.ApplyPresetCommand", File.ReadAllText(Repo.App("Views", "SitesPage.axaml")));
        Assert.DoesNotContain("ApplyPresetCommand", File.ReadAllText(Repo.App("Views", "AppsPage.axaml")));
    }

    [Fact]
    public void TheKindIsWrittenInTheMutedClassOnTheAppsPage()
    {
        var markup = File.ReadAllText(Repo.App("Views", "AppsPage.axaml"));

        Assert.Matches(@"<TextBlock[^>]*Classes=""muted""[^>]*Text=""\{Binding\s+Kind\}""", markup);
        Assert.Contains(@"Classes=""primary""", markup);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATypedFilterKeepsItsSearchBoxWhenTheListShrinks(bool sites)
    {
        using var app = Lists(
            sites: Enumerable.Range(0, 9).Select(i => $"s{i}.example"),
            apps: Enumerable.Range(0, 9).Select(i => $"a{i}.exe"));
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;
        var announced = Watch(rules);
        var name = sites ? nameof(RuleScreenViewModel.ShowsSiteSearch) : nameof(RuleScreenViewModel.ShowsAppSearch);
        bool Shows() => sites ? rules.ShowsSiteSearch : rules.ShowsAppSearch;

        if (sites)
        {
            rules.SiteFilter = "s1";
        }
        else
        {
            rules.AppFilter = "a1";
        }

        Assert.Contains(name, announced);
        app.Link.Answer = _ => IpcResponse.Ok(
            Say.Status(EngineState.Idle, Moment),
            Config(
                Enumerable.Range(0, 8).Select(i => $"s{i}.example").ToList(),
                Enumerable.Range(0, 8).Select(i => $"a{i}.exe").ToList()));
        app.Show(Say.Status(EngineState.Idle, Moment));

        Assert.True(Shows());

        announced.Clear();
        if (sites)
        {
            rules.SiteFilter = string.Empty;
        }
        else
        {
            rules.AppFilter = string.Empty;
        }

        Assert.False(Shows());
        Assert.Contains(name, announced);
    }

    [Fact]
    public async Task TheNoticeIsAnnouncedAfterTheOutcomeExistsAndTheLastReadIsTheOutcome()
    {
        using var culture = new UiCulture("en-US");
        using var app = Lists();
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;
        var read = new List<string>();
        rules.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RuleScreenViewModel.Notice))
            {
                read.Add(rules.Notice);
            }
        };

        await rules.ApplyPresetCommand.ExecuteAsync(Presets.All[0]);

        Assert.Equal(
            string.Format(CultureInfo.CurrentUICulture, Strings.PresetOutcomeFormat, Presets.All[0].Domains.Count, 0),
            read[^1]);
    }

    [Fact]
    public async Task APresetThatSendsNothingClearsAStaleRefusal()
    {
        using var culture = new UiCulture("ru-RU");
        var social = Presets.All.Single(p => p.Id == Presets.Social);
        using var app = Lists(sites: social.Domains);
        var rules = (RuleScreenViewModel)app.Shell.CurrentScreen!;
        var good = app.Link.Answer;
        app.Link.Answer = _ => IpcResponse.Fail(IpcCodes.SessionEmptyBlockList);
        rules.NewSiteDomain = "x.example";
        await rules.AddSiteCommand.ExecuteAsync(null);
        Assert.NotEmpty(rules.Notice);
        app.Link.Answer = good;

        await rules.ApplyPresetCommand.ExecuteAsync(social);

        Assert.Equal($"Добавлено: 0. Уже были: {social.Domains.Count}.", rules.Notice);
    }

    // Idle (or given state) with the configuration answering these lists.
    private static AppUnderTest Lists(IEnumerable<string>? sites = null, IEnumerable<string>? apps = null, string state = EngineState.Idle)
    {
        var app = new AppUnderTest(Moment);
        var have = sites?.ToList() ?? [];
        var kept = apps?.ToList() ?? [];

        app.Link.Answer = request => request.Command == ServiceCommand.AddSiteRule
            ? Accepted(request, have, kept, state)
            : IpcResponse.Ok(Status(state), Config(have, kept));

        app.Show(Status(state));

        return app;
    }

    // Follows the machine: a configuration that named a language would switch the one the test chose.
    private static ConfigPayload Config(IEnumerable<string>? sites, IEnumerable<string>? apps) =>
        Say.Config(sites?.ToList(), apps?.ToList()) with { Language = "system" };

    private static StatusPayload Status(string state) => state == EngineState.Idle
        ? Say.Status(state, Moment)
        : Say.Status(state, Moment, endsAt: Moment.AddMinutes(50), startedAt: Moment);

    // Accepted, with the configuration now holding the domain: Take refreshes the list from it.
    private static IpcResponse Accepted(
        IpcRequest request, List<string> sites, List<string>? apps = null, string state = EngineState.Idle)
    {
        sites.Add(request.Site!.Domain);

        return IpcResponse.Ok(Status(state), Config(sites, apps));
    }

    private static List<string> Watch(INotifyPropertyChanged source)
    {
        var names = new List<string>();
        source.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);

        return names;
    }
}
