using System.ComponentModel;
using Chronos.App.ViewModels;

namespace Chronos.App.Tests;

/// <summary>The countdown runs from EndsAt on the machine's ticks, with the clock difference measured against the service's Now. Every clock is injected.</summary>
[Collection(CultureBound.Name)]
public sealed class CountdownTests
{
    private static readonly DateTimeOffset Moment = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Remaining_CountsDownBetweenEventsRatherThanFreezing()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(50)));
        var screen = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);

        Assert.Equal(TimeSpan.FromMinutes(50), screen.Remaining);

        // Nothing arrives from the service; ninety seconds pass here all the same.
        app.Clock.Advance(TimeSpan.FromSeconds(90));
        app.Ticker.Tick();

        Assert.Equal(TimeSpan.FromMinutes(50) - TimeSpan.FromSeconds(90), screen.Remaining);
    }

    /// <summary>The service's clock decides: ten minutes apart, the session has twenty left by its Now but would look like thirty by the local clock.</summary>
    [Fact]
    public void Remaining_UsesTheServicesClockRatherThanThisMachines()
    {
        using var app = new AppUnderTest(Moment);

        var serviceNow = Moment.AddMinutes(10);
        app.Show(Say.Status("Active", serviceNow, endsAt: serviceNow.AddMinutes(20)));

        var screen = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);

        Assert.Equal(TimeSpan.FromMinutes(20), screen.Remaining);
        Assert.NotEqual(TimeSpan.FromMinutes(30), screen.Remaining);

        // The offset survives the ticks: measured once against the status, not guessed again from the local clock.
        app.Clock.Advance(TimeSpan.FromMinutes(5));
        app.Ticker.Tick();

        Assert.Equal(TimeSpan.FromMinutes(15), screen.Remaining);
    }

    [Fact]
    public void Waiting_CountsDownToTheMomentTheBlockLifts()
    {
        using var app = new AppUnderTest(Moment);

        var serviceNow = Moment.AddMinutes(-10);
        app.Show(Say.Status(
            "UnlockPending",
            serviceNow,
            endsAt: serviceNow.AddHours(3),
            unlockAt: serviceNow.AddSeconds(30)));

        var screen = Assert.IsType<WaitingViewModel>(app.Shell.CurrentScreen);

        Assert.Equal(TimeSpan.FromSeconds(30), screen.Remaining);

        app.Clock.Advance(TimeSpan.FromSeconds(20));
        app.Ticker.Tick();

        Assert.Equal(TimeSpan.FromSeconds(10), screen.Remaining);
    }

    /// <summary>A countdown past its end reads zero, not negative; the service decides when the state changes.</summary>
    [Fact]
    public void Remaining_StopsAtZeroRatherThanRunningBackwards()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddSeconds(5)));
        var screen = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);

        app.Clock.Advance(TimeSpan.FromMinutes(1));
        app.Ticker.Tick();

        Assert.Equal(TimeSpan.Zero, screen.Remaining);
    }

    [Fact]
    public void Remaining_SaysThatItChangedSoTheWindowRedraws()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(50)));
        var screen = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);
        var announced = Watch(screen);

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        app.Ticker.Tick();

        Assert.Contains(nameof(ActiveViewModel.Remaining), announced);
        Assert.Contains(nameof(ActiveViewModel.RemainingText), announced);
    }

    /// <summary>English is pinned because this machine is Russian; a Russian-only screen would pass a Russian assertion.</summary>
    [Theory]
    [InlineData(47, "47 minutes")]
    [InlineData(1, "1 minute")]
    [InlineData(21, "21 minutes")]
    public void Remaining_IsSaidInEnglishWordsThatAgreeWithTheNumber(int minutes, string expected)
    {
        using var restore = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(minutes)));

        Assert.Contains(expected, Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen).RemainingText);
    }

    [Theory]
    [InlineData(47, "47 минут")]
    [InlineData(1, "1 минута")]
    [InlineData(21, "21 минута")]
    [InlineData(3, "3 минуты")]
    public void Remaining_IsSaidInRussianWordsThatAgreeWithTheNumber(int minutes, string expected)
    {
        using var restore = new UiCulture("ru-RU", formatting: "en-GB");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(minutes)));

        Assert.Contains(expected, Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen).RemainingText);
    }

    /// <summary>Under a minute the countdown is in seconds; the waiting screen would otherwise say "0 minutes".</summary>
    [Fact]
    public void Remaining_UnderAMinuteIsCountedInSeconds()
    {
        using var restore = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("UnlockPending", Moment, endsAt: Moment.AddHours(1), unlockAt: Moment.AddSeconds(30)));

        Assert.Contains("30 seconds", Assert.IsType<WaitingViewModel>(app.Shell.CurrentScreen).RemainingText);
    }

    [Fact]
    public void Remaining_OverAnHourIsCountedInHoursAndMinutes()
    {
        using var restore = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(125)));

        var said = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen).RemainingText;

        Assert.Contains("2 hours", said);
        Assert.Contains("5 minutes", said);
    }

    /// <summary>A sentence no markup can name, since the number comes from the clock.</summary>
    [Fact]
    public void Remaining_FollowsALanguageChangeWithoutBeingRebuilt()
    {
        using var restore = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(50)));
        var screen = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);
        var announced = Watch(screen);

        var english = screen.RemainingText;
        app.Language.Use("ru-RU");

        Assert.NotEqual(english, screen.RemainingText);
        Assert.Contains(nameof(ActiveViewModel.RemainingText), announced);
    }

    private static List<string> Watch(INotifyPropertyChanged screen)
    {
        var names = new List<string>();
        screen.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);

        return names;
    }
}
