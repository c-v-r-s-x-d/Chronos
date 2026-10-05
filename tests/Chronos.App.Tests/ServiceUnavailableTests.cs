using Chronos.App.Localization;
using Chronos.App.Resources;
using Chronos.App.Services;
using Chronos.Ipc;

namespace Chronos.App.Tests;

public sealed class ServiceUnavailableTests
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheWindowCountsDownToTheNextAttempt()
    {
        using var culture = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Moment);
        app.Link.Publish(ServiceSnapshot.Unavailable);

        app.Link.PublishRetry(TimeSpan.FromSeconds(8));
        app.Clock.Advance(TimeSpan.FromSeconds(3));
        app.Ticker.Tick();

        Assert.Equal("Следующая попытка через 5 секунд.", app.Shell.RetryText);
    }

    [Fact]
    public void AnAttemptThatIsDueSaysItIsConnecting()
    {
        using var culture = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Moment);
        app.Link.Publish(ServiceSnapshot.Unavailable);
        app.Link.PublishRetry(TimeSpan.FromSeconds(1));

        app.Clock.Advance(TimeSpan.FromSeconds(2));
        app.Ticker.Tick();

        Assert.Equal("Подключаемся…", app.Shell.RetryText);
    }

    [Fact]
    public void NoCountdownIsShownWhileTheServiceIsThere()
    {
        using var app = new AppUnderTest(Moment);
        app.Link.PublishRetry(TimeSpan.FromSeconds(1));
        app.Show(Say.Status(EngineState.Idle, Moment));

        Assert.Equal(string.Empty, app.Shell.RetryText);
    }

    [Fact]
    public void TheCountdownFollowsTheLanguage()
    {
        using var culture = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Moment);
        app.Link.Publish(ServiceSnapshot.Unavailable);
        app.Link.PublishRetry(TimeSpan.FromSeconds(5));

        app.Text.SwitchTo(LanguageChoice.English);

        Assert.Equal("Next attempt in 5 seconds.", app.Shell.RetryText);
    }

    [Fact]
    public void TheRecoveryCommandIsNamedWordForWordInBothLanguages()
    {
        foreach (var culture in new[] { "en-US", "ru-RU" })
        {
            using var _ = new UiCulture(culture);
            Assert.Contains("chronos recover", Strings.ServiceRecoverHint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheWindowShowsTheCountdownAndTheHint()
    {
        var markup = File.ReadAllText(Repo.App("MainWindow.axaml"));

        Assert.Matches(@"\{Binding\s+RetryText\}", markup);
        Assert.Matches(@"\{Binding\s+Text\.ServiceRecoverHint\}", markup);
        Assert.Matches(@"\{Binding\s+Text\.ServiceUnavailableMeaning\}", markup);
    }

    [Fact]
    public void TheMeaningSaysABlockInForceKeepsHolding()
    {
        using (new UiCulture("ru-RU"))
        {
            Assert.Contains("блокировк", Strings.ServiceUnavailableMeaning, StringComparison.Ordinal);
        }

        using (new UiCulture("en-US"))
        {
            Assert.Contains("block", Strings.ServiceUnavailableMeaning, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OneSecondLeftIsInTheAccusative()
    {
        using var culture = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Moment);
        app.Link.Publish(ServiceSnapshot.Unavailable);

        app.Link.PublishRetry(TimeSpan.FromSeconds(1));

        Assert.Equal("Следующая попытка через 1 секунду.", app.Shell.RetryText);
    }

    private static List<string> Announced(AppUnderTest app)
    {
        var names = new List<string>();
        app.Shell.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);
        return names;
    }

    [Fact]
    public void ARetryIsAnnounced()
    {
        using var app = new AppUnderTest(Moment);
        app.Link.Publish(ServiceSnapshot.Unavailable);
        var names = Announced(app);

        app.Link.PublishRetry(TimeSpan.FromSeconds(5));

        Assert.Contains("RetryText", names);
    }

    [Fact]
    public void ATickWhileUnavailableAnnouncesTheCountdown()
    {
        using var app = new AppUnderTest(Moment);
        app.Link.Publish(ServiceSnapshot.Unavailable);
        app.Link.PublishRetry(TimeSpan.FromSeconds(5));
        var names = Announced(app);

        app.Ticker.Tick();

        Assert.Contains("RetryText", names);
    }

    [Fact]
    public void ATickWhileAvailableDoesNotAnnounceTheCountdown()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment));
        var names = Announced(app);

        app.Ticker.Tick();

        Assert.DoesNotContain("RetryText", names);
    }

    [Fact]
    public void ALanguageSwitchAnnouncesTheCountdown()
    {
        using var culture = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Moment);
        app.Link.Publish(ServiceSnapshot.Unavailable);
        app.Link.PublishRetry(TimeSpan.FromSeconds(5));
        var names = Announced(app);

        app.Text.SwitchTo(LanguageChoice.English);

        Assert.Contains("RetryText", names);
    }
}
