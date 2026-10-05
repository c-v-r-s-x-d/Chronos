using Chronos.App.Localization;
using Chronos.App.Resources;
using Chronos.App.Services;
using Chronos.App.Time;
using Chronos.App.ViewModels;
using Chronos.Ipc;

namespace Chronos.App.Tests;

/// <summary>A language change applies without a restart. These tests drive the control a user would use, against the real service.</summary>
[Collection(CultureBound.Name)]
public sealed class LanguageChoiceTests : IAsyncLifetime
{
    private readonly ServiceHarness _service = new();

    private readonly RecordedWaits _waits = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _service.DisposeAsync().AsTask();

    /// <summary>Choose Russian: words change at once, the service holds the choice, and a fresh interface starts in Russian.</summary>
    [Fact]
    public async Task ChoosingALanguageChangesTheScreenAtOnceAndIsStillThereNextTime()
    {
        _service.Start();
        await using var link = new ServiceLink(_service.PipeName, _waits.NoWaitAsync);
        link.Start();

        var idle = await LinkWait.ForAsync(
            link, static snapshot => snapshot.Status is not null, "reached the service");

        using var machine = new UiCulture("en-US");

        var language = new LanguageSwitch();
        var text = new Text(language);
        using var setup = new SetupViewModel(link, text, new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));
        setup.Show(idle.Status!);
        await setup.Loading;

        using var screen = new SettingsViewModel(link, text);
        screen.Show(idle.Status!);

        // Nothing chosen yet: an English machine gets English.
        Assert.Equal(LanguageChoice.English, language.Choice);
        Assert.Equal("Start session", text.StartSession);

        screen.SelectedLanguage = LanguageChoice.IndexOf(LanguageChoice.Russian);
        await screen.Saving;

        // No restart happened in between.
        Assert.Equal("Начать сессию", text.StartSession);
        Assert.Equal("Настройки", text.SectionSettings);
        Assert.Equal(LanguageChoice.Russian, language.Choice);

        // Asked for again rather than read off the answer, so this is what a later reader would find.
        var stored = await link.SendAsync(
            new IpcRequest { Command = "GetConfig" }, CancellationToken.None);

        Assert.Equal(LanguageChoice.Russian, stored!.Config!.Language);

        // A fresh interface on a machine still in English comes up in Russian only if it reads the choice back from the configuration.
        using (new UiCulture("en-US"))
        {
            var again = new LanguageSwitch();
            var words = new Text(again);
            using var restarted = new SetupViewModel(link, words, new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));
            restarted.Show(idle.Status!);
            await restarted.Loading;

            using var settings = new SettingsViewModel(link, words);

            Assert.Equal(LanguageChoice.Russian, again.Choice);
            Assert.Equal(LanguageChoice.IndexOf(LanguageChoice.Russian), settings.SelectedLanguage);

            // Read in the language the fresh interface arrived at, not off this thread, which this test pinned to English.
            Assert.Equal(
                "Начать сессию",
                Strings.ResourceManager.GetString(nameof(Strings.StartSession), again.Current));
        }
    }

    /// <summary>Two choices, each naming itself.</summary>
    [Fact]
    public void TheChoicesAreTheTwoLanguages()
    {
        using var machine = new UiCulture("en-US");

        var text = new Text(new LanguageSwitch());
        using var screen = new SettingsViewModel(new RecordingLink(), text);

        Assert.Equal([LanguageChoice.English, LanguageChoice.Russian], screen.Languages.Select(line => line.Choice));
        Assert.Equal("English", screen.Languages[0].Name);
        Assert.Equal("Русский", screen.Languages[1].Name);
    }

    /// <summary>With no language chosen, or one the interface doesn't have, a Russian Windows gets Russian and any other English.</summary>
    [Theory]
    [InlineData("ru-RU", "system", "ru")]
    [InlineData("ru-KZ", null, "ru")]
    [InlineData("en-US", "system", "en")]
    [InlineData("de-DE", "system", "en")]
    [InlineData("ru-RU", "de", "ru")]
    [InlineData("de-DE", "fr", "en")]
    [InlineData("en-US", "ru", "ru")]
    [InlineData("ru-RU", "en", "en")]
    [InlineData("en-US", "ru-KZ", "ru")]
    public void AnUnchosenLanguageFollowsTheMachine(string machine, string? stored, string expected)
    {
        using var culture = new UiCulture(machine);
        var language = new LanguageSwitch();

        language.Choose(stored);

        Assert.Equal(expected, language.Choice);
        Assert.Equal(expected, language.Current.TwoLetterISOLanguageName);
    }

    [Theory]
    [InlineData("ru-RU", "ru")]
    [InlineData("de-DE", "en")]
    public void BeforeTheConfigurationArrivesThePickerShowsTheMachinesLanguage(string machine, string expected)
    {
        using var culture = new UiCulture(machine);

        Assert.Equal(expected, new LanguageSwitch().Choice);
    }

    /// <summary>The choice is persisted by asking the service to keep it; the interface has no configuration file.</summary>
    [Fact]
    public async Task TheChoiceIsSentToTheServiceRatherThanWrittenAnywhere()
    {
        using var machine = new UiCulture("en-US");

        var link = new RecordingLink();
        using var screen = new SettingsViewModel(link, new Text(new LanguageSwitch()));

        screen.SelectedLanguage = LanguageChoice.IndexOf(LanguageChoice.Russian);
        await screen.Saving;

        var sent = Assert.Single(link.Sent, request => request.Command == "UpdateSettings");

        Assert.Equal(LanguageChoice.Russian, sent.Settings!.Language);

        // Every other field is left alone, so two clients changing two settings do not undo each other.
        Assert.Null(sent.Settings.CoolDownMinutes);
        Assert.Null(sent.Settings.DefaultSessionMinutes);
        Assert.Null(sent.Settings.VerboseLogging);
        Assert.Null(sent.Settings.WfpEnabled);
    }

    /// <summary>Every screen and the notification area change with it, not only the screen the control is on.</summary>
    [Fact]
    public async Task EverySurfaceChangesLanguageAndNotOnlyTheOneWithTheControl()
    {
        using var machine = new UiCulture("en-US");
        using var app = new AppUnderTest(ServiceHarness.Moment);

        app.Show(Say.Status("Idle", ServiceHarness.Moment));

        var setup = Assert.IsType<SetupViewModel>(app.Shell.CurrentScreen);
        await setup.Loading;

        app.Shell.Settings.SelectedLanguage = LanguageChoice.IndexOf(LanguageChoice.Russian);
        await app.Shell.Settings.Saving;

        Assert.Equal("Служба Chronos недоступна.".Split('.')[0], app.Text.ServiceUnavailableHeading.Split('.')[0]);
        Assert.Equal("сессия не идёт", app.Shell.Tray.Tooltip.Split(": ")[1]);

        // The screen not in front of anybody changes too.
        app.Show(Say.Status(
            "UnlockPending",
            ServiceHarness.Moment,
            endsAt: ServiceHarness.Moment.AddHours(1),
            unlockAt: ServiceHarness.Moment.AddMinutes(30)));

        var waiting = Assert.IsType<WaitingViewModel>(app.Shell.CurrentScreen);

        Assert.Equal("Блокировка снимется через 30 минут.", waiting.RemainingText);
    }
}
