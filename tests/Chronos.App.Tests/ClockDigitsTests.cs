using System.ComponentModel;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.ViewModels;

namespace Chronos.App.Tests;

/// <summary>The two timers as clock digits, and the caption under the session's.</summary>
[Collection(CultureBound.Name)]
public sealed class ClockDigitsTests
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(4360, "1:12:40")]
    [InlineData(247, "4:07")]
    [InlineData(49, "0:49")]
    [InlineData(0, "0:00")]
    [InlineData(-30, "0:00")]
    [InlineData(60, "1:00")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3601, "1:00:01")]
    [InlineData(90000, "25:00:00")]
    public void ASpanIsWrittenAsHoursFromOneHourUpAndAsMinutesBelow(int seconds, string digits)
    {
        Assert.Equal(digits, Duration.Digits(TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>Up, as the sentences are: the digits do not sit on zero while time is left.</summary>
    [Theory]
    [InlineData(200, "0:01")]
    [InlineData(48200, "0:49")]
    [InlineData(3599200, "1:00:00")]
    public void APartOfASecondIsRoundedUp(int milliseconds, string digits)
    {
        Assert.Equal(digits, Duration.Digits(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void TheDigitsAreTheSameInEveryLanguage()
    {
        using var culture = new UiCulture("ru-RU");

        Assert.Equal("1:12:40", Duration.Digits(new TimeSpan(1, 12, 40)));
    }

    [Fact]
    public void TheActiveScreenShowsWhatIsLeftAsDigits()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddSeconds(4360)));
        var screen = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);

        Assert.Equal("1:12:40", screen.RemainingDigits);

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        app.Ticker.Tick();

        Assert.Equal("1:12:39", screen.RemainingDigits);
    }

    [Fact]
    public void TheWaitingScreenShowsWhatIsLeftAsDigits()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.UnlockPending, Moment, endsAt: Moment.AddHours(2), unlockAt: Moment.AddSeconds(49)));
        var screen = Assert.IsType<WaitingViewModel>(app.Shell.CurrentScreen);

        Assert.Equal("0:49", screen.RemainingDigits);
    }

    [Fact]
    public void ATickAnnouncesTheDigitsAndNotTheCaption()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddMinutes(50)));
        var screen = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);
        var announced = Watch(screen);

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        app.Ticker.Tick();

        Assert.Contains(nameof(ActiveViewModel.RemainingDigits), announced);
        Assert.DoesNotContain(nameof(ActiveViewModel.RemainingCaption), announced);
    }

    [Fact]
    public void ATickOnTheWaitingScreenAnnouncesTheDigits()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.UnlockPending, Moment, endsAt: Moment.AddHours(2), unlockAt: Moment.AddMinutes(5)));
        var screen = Assert.IsType<WaitingViewModel>(app.Shell.CurrentScreen);
        var announced = Watch(screen);

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        app.Ticker.Tick();

        Assert.Contains(nameof(WaitingViewModel.RemainingDigits), announced);
        Assert.Equal("4:59", screen.RemainingDigits);
    }

    [Theory]
    [InlineData("en-US", "left · ")]
    [InlineData("ru-RU", "осталось · ")]
    public void TheCaptionSaysLeftAndWhenItEnds(string language, string start)
    {
        using var culture = new UiCulture(language);
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddMinutes(50)));
        var screen = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);

        Assert.NotEmpty(screen.EndsAtText);
        Assert.Equal(start + screen.EndsAtText, screen.RemainingCaption);
    }

    [Theory]
    [InlineData("en-US", "left")]
    [InlineData("ru-RU", "осталось")]
    public void WithNoEndToNameTheCaptionIsTheOneWord(string language, string caption)
    {
        using var culture = new UiCulture(language);
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Active, Moment));
        var screen = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);

        Assert.Equal(caption, screen.RemainingCaption);
    }

    [Fact]
    public void ALanguageChangeRewritesTheCaptionAndSaysSo()
    {
        using var culture = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddMinutes(50)));
        var screen = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);
        var english = screen.RemainingCaption;
        var announced = Watch(screen);

        app.Language.Use("ru-RU");

        Assert.NotEqual(english, screen.RemainingCaption);
        Assert.StartsWith("осталось", screen.RemainingCaption, StringComparison.Ordinal);
        Assert.Contains(nameof(ActiveViewModel.RemainingCaption), announced);
    }

    [Fact]
    public void AnExtensionMovesTheCaptionAndSaysSo()
    {
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddMinutes(50)));
        var screen = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);
        var before = screen.RemainingCaption;
        var announced = Watch(screen);

        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddMinutes(65)));

        Assert.NotEqual(before, screen.RemainingCaption);
        Assert.Contains(nameof(ActiveViewModel.RemainingCaption), announced);
    }

    [Fact]
    public void TheActivePageShowsDigitsWithTheCaptionUnderThem()
    {
        var page = File.ReadAllText(Repo.App("Views", "SessionActivePage.axaml"));

        Assert.Matches(@"Classes=""timer""[^>]*Text=""\{Binding RemainingDigits\}""", page);
        Assert.Matches(@"Classes=""muted""[^>]*Text=""\{Binding RemainingCaption\}""", page);
        Assert.True(
            page.IndexOf("RemainingDigits", StringComparison.Ordinal) < page.IndexOf("RemainingCaption", StringComparison.Ordinal),
            "The caption is not under the digits.");
        Assert.DoesNotContain(@"Text=""{Binding RemainingText}""", page);
        Assert.DoesNotContain(@"Text=""{Binding EndsAtText}""", page);
    }

    [Fact]
    public void TheWaitingScreenShowsItsLabelAndTheDigitsBelowIt()
    {
        var page = File.ReadAllText(Repo.App("Views", "WaitingView.axaml"));

        Assert.Matches(@"Classes=""h1""[^>]*Text=""\{Binding Text\.UnlockHeading\}""", page);
        Assert.Matches(@"Classes=""timer""[^>]*Text=""\{Binding RemainingDigits\}""", page);
        Assert.True(
            page.IndexOf("Text.UnlockHeading", StringComparison.Ordinal) < page.IndexOf("RemainingDigits", StringComparison.Ordinal),
            "The digits are not below the label.");
        Assert.DoesNotContain(@"Text=""{Binding RemainingText}""", page);
    }

    /// <summary>The sentence stays for a screen reader: digits alone do not say what they count.</summary>
    [Theory]
    [InlineData("SessionActivePage.axaml")]
    [InlineData("WaitingView.axaml")]
    public void TheDigitsAreReadOutAsTheSentence(string file)
    {
        var page = File.ReadAllText(Repo.App("Views", file));

        Assert.Matches(@"Classes=""timer""[^>]*AutomationProperties\.Name=""\{Binding RemainingText\}""", page);
    }

    private static List<string> Watch(INotifyPropertyChanged source)
    {
        var names = new List<string>();
        source.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);

        return names;
    }
}
