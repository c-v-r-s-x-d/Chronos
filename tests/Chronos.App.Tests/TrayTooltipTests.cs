using System.ComponentModel;
using Chronos.App.Resources;
using Chronos.App.ViewModels;

namespace Chronos.App.Tests;

/// <summary>The icon differs between blocking and not, and the tooltip carries the state and remaining time. Read off a view model, since the suite has no windowing system.</summary>
[Collection(CultureBound.Name)]
public sealed class TrayTooltipTests
{
    private static readonly DateTimeOffset Moment = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Windows keeps 127 characters of a tooltip and silently drops the rest.</summary>
    private const int WindowsTooltipLimit = 127;

    [Fact]
    public void Tooltip_ForASessionWith47MinutesLeft_SaysItIsRunningAndHowLong()
    {
        using var language = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(47)));

        var running = app.Shell.Tray.Tooltip;

        Assert.Contains("47 minutes", running, StringComparison.Ordinal);
        Assert.Contains(Strings.AppTitle, running, StringComparison.Ordinal);

        // "no session is running" contains "session is running", so the sentences are compared, not searched.
        app.Show(Say.Status("Idle", Moment));

        Assert.NotEqual(app.Shell.Tray.Tooltip, running);
        Assert.Contains(Strings.TrayActiveFormat.Split("{0}", StringSplitOptions.None)[0], running, StringComparison.Ordinal);
    }

    [Fact]
    public void Tooltip_ForASessionWith47MinutesLeft_UsesTheRussianFormOfTheNoun()
    {
        using var language = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(47)));

        Assert.Contains("47 минут", app.Shell.Tray.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("47 минуты", app.Shell.Tray.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("47 минута", app.Shell.Tray.Tooltip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "1 minute")]
    [InlineData(21, "21 minutes")]
    [InlineData(47, "47 minutes")]
    public void Tooltip_CarriesTheRemainingTimeInEnglish(int minutes, string expected)
    {
        using var language = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(minutes)));

        Assert.Contains(expected, app.Shell.Tray.Tooltip, StringComparison.Ordinal);
    }

    /// <summary>1 and 21 take the first Russian form, 47 the third; a rule on the last digit alone puts 21 wrong.</summary>
    [Theory]
    [InlineData(1, "1 минута")]
    [InlineData(21, "21 минута")]
    [InlineData(47, "47 минут")]
    public void Tooltip_CarriesTheRemainingTimeInRussian(int minutes, string expected)
    {
        using var language = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(minutes)));

        Assert.Contains(expected, app.Shell.Tray.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Tooltip_UnderAMinute_CountsInSeconds()
    {
        using var language = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddSeconds(30)));

        Assert.Contains("30 seconds", app.Shell.Tray.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Tooltip_WithNoSession_SaysSoAndCarriesNoTime()
    {
        using var language = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Idle", Moment));

        var tooltip = app.Shell.Tray.Tooltip;

        Assert.Contains(Strings.TrayIdle, tooltip, StringComparison.Ordinal);

        // No remaining time, because there is none.
        Assert.DoesNotContain(tooltip, char.IsDigit);
    }

    /// <summary>The waiting state counts to the moment the block lifts, a different field from the session end.</summary>
    [Fact]
    public void Tooltip_WhileWaitingForTheUnlock_CountsToTheUnlockRatherThanToTheSessionsEnd()
    {
        using var language = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status(
            "UnlockPending",
            Moment,
            endsAt: Moment.AddHours(3),
            unlockAt: Moment.AddMinutes(20)));

        var tooltip = app.Shell.Tray.Tooltip;

        Assert.Contains("20 minutes", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("3 hours", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Tooltip_WhileTheSessionIsFinishing_SaysSoRatherThanCountingZero()
    {
        using var language = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Ended", Moment, endsAt: Moment));

        var tooltip = app.Shell.Tray.Tooltip;

        Assert.Contains(Strings.SessionFinishing, tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("0 seconds", tooltip, StringComparison.Ordinal);
    }

    /// <summary>With no service there is no state to report, and the tooltip says that instead of guessing.</summary>
    [Fact]
    public void Tooltip_WithNoService_SaysTheStateIsNotKnownRatherThanGuessing()
    {
        using var language = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Link.Publish(Chronos.App.Services.ServiceSnapshot.Unavailable);

        var tooltip = app.Shell.Tray.Tooltip;

        Assert.Contains(Strings.TrayUnknown, tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.TrayIdle, tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Tooltip_BeforeTheFirstAnswer_SaysTheServiceIsBeingLookedFor()
    {
        using var language = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        Assert.Contains(Strings.TrayConnecting, app.Shell.Tray.Tooltip, StringComparison.Ordinal);
    }

    /// <summary>The tooltip countdown keeps time between statuses; with the window hidden it is the only thing still counting.</summary>
    [Fact]
    public void Tooltip_CountsDownBetweenStatusesRatherThanFreezing()
    {
        using var language = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(48)));
        Assert.Contains("48 minutes", app.Shell.Tray.Tooltip, StringComparison.Ordinal);

        var announced = new List<string>();
        ((INotifyPropertyChanged)app.Shell.Tray).PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        app.Clock.Advance(TimeSpan.FromMinutes(1));
        app.Ticker.Tick();

        Assert.Contains("47 minutes", app.Shell.Tray.Tooltip, StringComparison.Ordinal);

        // The beat has to reach the icon, not only the property.
        Assert.Contains(nameof(TrayViewModel.Tooltip), announced);
    }

    [Fact]
    public void Tooltip_FollowsTheLanguageAndSaysThatItChanged()
    {
        using var language = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(47)));

        var announced = new List<string>();
        ((INotifyPropertyChanged)app.Shell.Tray).PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        var english = app.Shell.Tray.Tooltip;
        app.Language.Use("ru-RU");

        Assert.NotEqual(english, app.Shell.Tray.Tooltip);
        Assert.Contains("47 минут", app.Shell.Tray.Tooltip, StringComparison.Ordinal);
        Assert.Contains(nameof(TrayViewModel.Tooltip), announced);
    }

    /// <summary>Every tooltip in both languages fits what Windows shows; overlong text is cut off silently.</summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void Tooltip_FitsInWhatTheNotificationAreaWillShow(string culture)
    {
        using var language = new UiCulture(culture);

        foreach (var tooltip in EveryTooltip())
        {
            Assert.True(
                tooltip.Length <= WindowsTooltipLimit,
                $"{culture}: \"{tooltip}\" is {tooltip.Length} characters, over the {WindowsTooltipLimit} Windows keeps.");
        }
    }

    private static IEnumerable<string> EveryTooltip()
    {
        var states = new[]
        {
            Say.Status("Idle", Moment),
            Say.Status("Active", Moment, endsAt: Moment.AddMinutes(444)),
            Say.Status("Ended", Moment, endsAt: Moment),
            Say.Status("UnlockPending", Moment, endsAt: Moment.AddHours(9), unlockAt: Moment.AddMinutes(444)),
        };

        using var app = new AppUnderTest(Moment);

        yield return app.Shell.Tray.Tooltip;

        app.Link.Publish(Chronos.App.Services.ServiceSnapshot.Unavailable);
        yield return app.Shell.Tray.Tooltip;

        foreach (var status in states)
        {
            app.Show(status);
            yield return app.Shell.Tray.Tooltip;
        }
    }
}
