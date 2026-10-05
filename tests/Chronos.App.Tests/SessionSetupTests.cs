using System.Globalization;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.ViewModels;
using Chronos.Ipc;

namespace Chronos.App.Tests;

/// <summary>The Session section before a session starts.</summary>
[Collection(CultureBound.Name)]
public sealed class SessionSetupTests
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 30, 18, 45, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheDurationsOnOfferAreTheFourAndACustomOne()
    {
        using var app = await Setup(defaultMinutes: 60);

        Assert.Equal([30, 60, 120, 240], app.Shell.SetupScreen().Durations.Where(d => !d.IsCustom).Select(d => d.Minutes));
        Assert.True(app.Shell.SetupScreen().Durations[^1].IsCustom);
    }

    [Fact]
    public async Task TheDefaultLengthPicksItsChip()
    {
        using var app = await Setup(defaultMinutes: 120);

        Assert.Equal(2, app.Shell.SetupScreen().SelectedDuration);
        Assert.False(app.Shell.SetupScreen().IsCustomDuration);
    }

    [Fact]
    public async Task ADefaultOutsideTheChipsIsTheCustomOne()
    {
        using var app = await Setup(defaultMinutes: 45);
        var setup = app.Shell.SetupScreen();

        Assert.True(setup.IsCustomDuration);
        Assert.Equal(45, setup.Minutes);
    }

    [Fact]
    public async Task PickingAChipSetsTheLengthThatIsSent()
    {
        using var app = await Setup(defaultMinutes: 60);
        var setup = app.Shell.SetupScreen();

        setup.SelectedDuration = 3;
        setup.StartCommand.Execute(null);

        Assert.Equal(240, app.Link.Sent.Last(r => r.Command == ServiceCommand.StartSession).DurationMinutes);
    }

    [Fact]
    public async Task AChipThePersonPickedIsNotUndoneByTheNextStatus()
    {
        using var app = await Setup(defaultMinutes: 60);
        var setup = app.Shell.SetupScreen();
        setup.SelectedDuration = 0;

        app.Show(Say.Status(EngineState.Idle, Moment));
        await setup.Loading;

        Assert.Equal(0, setup.SelectedDuration);
        Assert.Equal(30, setup.Minutes);
    }

    [Fact]
    public async Task ACustomLengthTheyTypedIsNotUndoneByTheNextStatus()
    {
        using var app = await Setup(defaultMinutes: 60);
        var setup = app.Shell.SetupScreen();
        setup.SelectedDuration = 4;
        setup.Minutes = 50;

        app.Show(Say.Status(EngineState.Idle, Moment));
        await setup.Loading;

        Assert.True(setup.IsCustomDuration);
        Assert.Equal(50, setup.Minutes);
    }

    [Fact]
    public async Task TheSummarySaysWhatAndUntilWhen()
    {
        using var culture = new UiCulture("ru-RU");
        using var app = await Setup(defaultMinutes: 60, sites: 12, apps: 3);

        var until = Moment.AddHours(1).ToLocalTime().ToString("t", CultureInfo.CurrentUICulture);
        Assert.Equal($"Заблокирует 12 сайтов и 3 приложения до {until}.", app.Shell.SetupScreen().Summary);
    }

    [Fact]
    public async Task TheSummaryMovesWithTheClock()
    {
        using var app = await Setup(defaultMinutes: 60, sites: 1);
        var before = app.Shell.SetupScreen().Summary;

        app.Clock.Advance(TimeSpan.FromMinutes(5));
        app.Ticker.Tick();

        Assert.NotEqual(before, app.Shell.SetupScreen().Summary);
    }

    [Fact]
    public async Task TheSummaryMovesWithTheLength()
    {
        using var app = await Setup(defaultMinutes: 60, sites: 1);
        var setup = app.Shell.SetupScreen();
        var before = setup.Summary;

        setup.SelectedDuration = 3;

        Assert.NotEqual(before, setup.Summary);
    }

    [Fact]
    public async Task TheSummaryFollowsTheLanguage()
    {
        using var culture = new UiCulture("en-US");
        using var app = await Setup(defaultMinutes: 60, sites: 2, apps: 1);
        var setup = app.Shell.SetupScreen();
        var announced = new List<string>();
        setup.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        Assert.StartsWith("Blocks 2 sites and 1 app until", setup.Summary, StringComparison.Ordinal);
        app.Text.SwitchTo(LanguageChoice.Russian);

        Assert.StartsWith("Заблокирует 2 сайта и 1 приложение до", setup.Summary, StringComparison.Ordinal);
        Assert.Contains(nameof(SetupViewModel.Summary), announced);
    }

    [Fact]
    public async Task EmptyListsSayThereIsNothingToBlockButStillAllowAStart()
    {
        using var app = await Setup(defaultMinutes: 60);
        var setup = app.Shell.SetupScreen();

        Assert.True(setup.NothingToBlock);
        Assert.Equal(string.Empty, setup.Summary);
        Assert.True(setup.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheChipNamesFollowTheLanguage()
    {
        using var culture = new UiCulture("en-US");
        using var app = await Setup(defaultMinutes: 60);
        var english = app.Shell.SetupScreen().Durations[1].Name;

        app.Text.SwitchTo(LanguageChoice.Russian);

        Assert.Equal("1 час", app.Shell.SetupScreen().Durations[1].Name);
        Assert.NotEqual(english, app.Shell.SetupScreen().Durations[1].Name);
    }

    [Fact]
    public void ThePageLinksToBothLists()
    {
        var markup = File.ReadAllText(Repo.App("Views", "SessionSetupPage.axaml"));

        Assert.Matches(@"GoToCommand[^>]*CommandParameter=""\{x:Static\s+vm:Section\.Sites\}""", markup);
        Assert.Matches(@"GoToCommand[^>]*CommandParameter=""\{x:Static\s+vm:Section\.Apps\}""", markup);
    }

    [Fact]
    public async Task APickedChipAnnouncesTheSummary()
    {
        using var app = await Setup(defaultMinutes: 60, sites: 1);
        var setup = app.Shell.SetupScreen();
        var announced = Watch(setup);

        setup.SelectedDuration = 3;

        Assert.Contains(nameof(SetupViewModel.Summary), announced);
        Assert.Contains(nameof(SetupViewModel.Minutes), announced);
    }

    [Fact]
    public async Task ATypedLengthAnnouncesTheSummary()
    {
        using var app = await Setup(defaultMinutes: 60, sites: 1);
        var setup = app.Shell.SetupScreen();
        var announced = Watch(setup);

        setup.Minutes = 50;

        Assert.Contains(nameof(SetupViewModel.Summary), announced);
    }

    [Fact]
    public async Task AConfigurationThatArrivesAnnouncesWhatItChanged()
    {
        using var app = await Setup(defaultMinutes: 60);
        var setup = app.Shell.SetupScreen();
        var announced = Watch(setup);

        Answer(app, defaultMinutes: 45, sites: 2);
        app.Show(Say.Status(EngineState.Idle, Moment));
        await setup.Loading;

        Assert.Contains(nameof(SetupViewModel.NothingToBlock), announced);
        Assert.Contains(nameof(SetupViewModel.IsCustomDuration), announced);
        Assert.Contains(nameof(SetupViewModel.Summary), announced);
        Assert.False(setup.NothingToBlock);
        Assert.True(setup.IsCustomDuration);
    }

    [Fact]
    public async Task ALanguageChangeAnnouncesTheChipsAndThenTheSelection()
    {
        using var app = await Setup(defaultMinutes: 60);
        var setup = app.Shell.SetupScreen();
        var announced = Watch(setup);

        app.Text.SwitchTo(LanguageChoice.Russian);

        var list = announced.IndexOf(nameof(SetupViewModel.Durations));
        Assert.True(list >= 0, "Durations was never announced.");
        Assert.True(announced.IndexOf(nameof(SetupViewModel.SelectedDuration)) > list, "The selection must follow the list.");
        app.Text.SwitchTo(LanguageChoice.English);
    }

    [Fact]
    public async Task AThirtyMinuteDefaultIsTheFirstChipAndNotTheCustomOne()
    {
        using var app = await Setup(defaultMinutes: 30);
        var setup = app.Shell.SetupScreen();

        Assert.Equal(0, setup.SelectedDuration);
        Assert.False(setup.IsCustomDuration);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-5, 5)]
    [InlineData(4, 5)]
    [InlineData(5, 5)]
    [InlineData(1440, 1440)]
    [InlineData(1441, 1440)]
    [InlineData(100000, 1440)]
    public async Task TheLengthStaysInsideWhatTheBoxAllows(int typed, int kept)
    {
        using var app = await Setup(defaultMinutes: 60, sites: 1);
        var setup = app.Shell.SetupScreen();

        setup.Minutes = typed;

        Assert.Equal(kept, setup.Minutes);
        Assert.NotEmpty(setup.Summary);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-5, 5)]
    [InlineData(4, 5)]
    [InlineData(5, 5)]
    [InlineData(1440, 1440)]
    [InlineData(1441, 1440)]
    [InlineData(100000, 1440)]
    public async Task ADefaultOutsideTheBoxIsClamped(int configured, int kept)
    {
        using var app = await Setup(defaultMinutes: configured, sites: 1);
        var setup = app.Shell.SetupScreen();

        Assert.Equal(kept, setup.Minutes);
        Assert.NotEmpty(setup.Summary);
    }

    [Theory]
    [InlineData("ru-RU", 12, 0, "Заблокирует 12 сайтов до")]
    [InlineData("ru-RU", 0, 3, "Заблокирует 3 приложения до")]
    [InlineData("en-US", 1, 0, "Blocks 1 site until")]
    [InlineData("en-US", 0, 2, "Blocks 2 apps until")]
    public async Task AnEmptyListIsLeftOutOfTheSummary(string language, int sites, int apps, string start)
    {
        using var culture = new UiCulture(language);
        using var app = await Setup(defaultMinutes: 60, sites, apps);

        var summary = app.Shell.SetupScreen().Summary;

        Assert.StartsWith(start, summary, StringComparison.Ordinal);
        Assert.DoesNotContain(" 0 ", summary, StringComparison.Ordinal);
    }

    private static List<string> Watch(System.ComponentModel.INotifyPropertyChanged source)
    {
        var names = new List<string>();
        source.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);

        return names;
    }

    private static void Answer(AppUnderTest app, int defaultMinutes, int sites = 0, int apps = 0)
    {
        // The configuration's language is "system" so that showing it leaves the culture a test
        // set alone; "en" would switch every Russian test back to English on the first status.
        var config = Say.Config(
            [.. Enumerable.Range(0, sites).Select(i => $"site{i}.example")],
            [.. Enumerable.Range(0, apps).Select(i => $"app{i}.exe")],
            defaultMinutes) with { Language = "system" };

        app.Link.Answer = request =>
            request.Command == ServiceCommand.GetConfig ? IpcResponse.Ok(Say.Status(EngineState.Idle, Moment), config) : IpcResponse.Ok();
    }

    // Idle, config with the given lists and default; answers GetConfig.
    private static async Task<AppUnderTest> Setup(int defaultMinutes, int sites = 0, int apps = 0)
    {
        var app = new AppUnderTest(Moment);

        Answer(app, defaultMinutes, sites, apps);

        app.Show(Say.Status(EngineState.Idle, Moment));
        await app.Shell.SetupScreen().Loading;

        return app;
    }
}
