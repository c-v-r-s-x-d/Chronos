using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.Time;
using Chronos.App.ViewModels;
using Chronos.Ipc;

namespace Chronos.App.Tests;

/// <summary>How much of the sand is down, and the time a session ends.</summary>
[Collection(CultureBound.Name)]
public sealed class ProgressTests
{
    private static readonly DateTimeOffset Six = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(60, 0.5)]
    [InlineData(120, 1.0)]
    [InlineData(180, 1.0)]    // past the end
    [InlineData(-30, 0.0)]    // before the start
    public void TheShareIsTheTimeGoneOverTheWholeLength(int minutes, double share) =>
        Assert.Equal(share, Elapsed.Share(Six, Six.AddHours(2), Six.AddMinutes(minutes)), 6);

    [Fact]
    public void WithoutAStartTheShareIsNothing() =>
        Assert.Equal(0, Elapsed.Share(null, Six.AddHours(1), Six.AddMinutes(30)));

    [Fact]
    public void WithoutAnEndTheShareIsNothing() =>
        Assert.Equal(0, Elapsed.Share(Six, null, Six.AddMinutes(30)));

    [Fact]
    public void AnEndNotAfterTheStartIsNothing()
    {
        Assert.Equal(0, Elapsed.Share(Six, Six, Six.AddMinutes(1)));
        Assert.Equal(0, Elapsed.Share(Six, Six.AddMinutes(-5), Six.AddMinutes(1)));
    }

    [Fact]
    public void HalfASessionIsHalfTheSand()
    {
        using var app = new AppUnderTest(Six.AddHours(1));
        app.Show(Say.Status(EngineState.Active, Six.AddHours(1), startedAt: Six, endsAt: Six.AddHours(2)));

        Assert.Equal(0.5, ((ActiveViewModel)app.Shell.CurrentScreen!).Progress, 3);
    }

    [Fact]
    public void ASessionWithoutAStartHasNoSandDown()
    {
        using var app = new AppUnderTest(Six.AddHours(1));
        app.Show(Say.Status(EngineState.Active, Six.AddHours(1), endsAt: Six.AddHours(2)));

        Assert.Equal(0, ((ActiveViewModel)app.Shell.CurrentScreen!).Progress);
    }

    [Fact]
    public void AnEndBeforeNowStaysInsideTheGlass()
    {
        using var app = new AppUnderTest(Six.AddHours(3));
        app.Show(Say.Status(EngineState.Active, Six.AddHours(3), startedAt: Six, endsAt: Six.AddHours(1)));
        var active = (ActiveViewModel)app.Shell.CurrentScreen!;

        Assert.Equal(1, active.Progress);
        Assert.Equal(TimeSpan.Zero, active.Remaining);
    }

    [Fact]
    public void AServiceClockMinutesOutDoesNotMoveTheSand()
    {
        // The machine is seven minutes behind the service; the share is the service's own.
        using var app = new AppUnderTest(Six.AddMinutes(53));
        app.Show(Say.Status(EngineState.Active, Six.AddMinutes(60), startedAt: Six, endsAt: Six.AddHours(2)));

        Assert.Equal(0.5, ((ActiveViewModel)app.Shell.CurrentScreen!).Progress, 3);
    }

    [Fact]
    public void ExtendingPutsSandBackOnTop()
    {
        using var app = new AppUnderTest(Six.AddMinutes(30));
        app.Show(Say.Status(EngineState.Active, Six.AddMinutes(30), startedAt: Six, endsAt: Six.AddHours(1)));

        app.Show(Say.Status(EngineState.Active, Six.AddMinutes(30), startedAt: Six, endsAt: Six.AddHours(2)));

        Assert.Equal(0.25, ((ActiveViewModel)app.Shell.CurrentScreen!).Progress, 3);
    }

    [Fact]
    public void AFinishingSessionIsAllSandBelow()
    {
        using var app = new AppUnderTest(Six);
        app.Show(Say.Status(EngineState.Ended, Six, startedAt: Six, endsAt: Six.AddHours(1)));

        Assert.Equal(1, ((ActiveViewModel)app.Shell.CurrentScreen!).Progress);
    }

    [Fact]
    public void TheSandMovesWithTheClock()
    {
        using var app = new AppUnderTest(Six);
        app.Show(Say.Status(EngineState.Active, Six, startedAt: Six, endsAt: Six.AddHours(1)));

        app.Clock.Advance(TimeSpan.FromMinutes(15));
        app.Ticker.Tick();

        Assert.Equal(0.25, ((ActiveViewModel)app.Shell.CurrentScreen!).Progress, 3);
    }

    [Fact]
    public void TheWaitIsMeasuredAgainstTheCoolDown()
    {
        var lifts = Six.AddMinutes(10);
        using var app = new AppUnderTest(Six);
        app.Show(Say.Status(EngineState.UnlockPending, Six, endsAt: Six.AddHours(1), unlockAt: lifts, coolDownMinutes: 20));

        // 20 minutes of wait, 10 left: half the sand.
        Assert.Equal(0.5, ((WaitingViewModel)app.Shell.CurrentScreen!).Progress, 3);
    }

    [Fact]
    public void AWaitWithoutALiftMomentHasNoSandDown()
    {
        using var app = new AppUnderTest(Six);
        app.Show(Say.Status(EngineState.UnlockPending, Six, endsAt: Six.AddHours(1), coolDownMinutes: 20));

        Assert.Equal(0, ((WaitingViewModel)app.Shell.CurrentScreen!).Progress);
    }

    [Fact]
    public void AChipExtendsByItsLength()
    {
        using var app = new AppUnderTest(Six);
        app.Show(Say.Status(EngineState.Active, Six, startedAt: Six, endsAt: Six.AddHours(1)));
        var active = (ActiveViewModel)app.Shell.CurrentScreen!;

        active.ExtendByCommand.Execute(active.ExtendChoices[1]);

        Assert.Equal(30, app.Link.Sent.Last(r => r.Command == ServiceCommand.ExtendSession).DurationMinutes);
    }

    [Fact]
    public void TheChipsAre15And30And60()
    {
        using var app = new AppUnderTest(Six);
        app.Show(Say.Status(EngineState.Active, Six, startedAt: Six, endsAt: Six.AddHours(1)));

        var chips = ((ActiveViewModel)app.Shell.CurrentScreen!).ExtendChoices;

        Assert.Equal([15, 30, 60], chips.Select(chip => chip.Minutes));
        Assert.All(chips, chip => Assert.False(chip.IsCustom));
    }

    [Fact]
    public void TheChipsAreNamedInTheLanguageAndRewordedWithIt()
    {
        using var culture = new UiCulture("en-US");
        using var app = new AppUnderTest(Six);
        app.Show(Say.Status(EngineState.Active, Six, startedAt: Six, endsAt: Six.AddHours(1)));
        var active = (ActiveViewModel)app.Shell.CurrentScreen!;
        var announced = Watch(active);

        Assert.Equal("15 minutes", active.ExtendChoices[0].Name);

        app.Language.Use("ru-RU");

        Assert.Contains(nameof(ActiveViewModel.ExtendChoices), announced);
        Assert.Equal("15 минут", active.ExtendChoices[0].Name);
        Assert.Equal("1 час", active.ExtendChoices[2].Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ACustomLengthThatIsNotPositiveSendsNothing(int minutes)
    {
        using var app = new AppUnderTest(Six);
        app.Show(Say.Status(EngineState.Active, Six, startedAt: Six, endsAt: Six.AddHours(1)));
        var active = (ActiveViewModel)app.Shell.CurrentScreen!;
        active.ExtendMinutes = minutes;

        active.ExtendCommand.Execute(null);

        Assert.DoesNotContain(app.Link.Sent, request => request.Command == ServiceCommand.ExtendSession);
    }

    [Fact]
    public void TheCustomToggleAnnouncesItself()
    {
        using var app = new AppUnderTest(Six);
        app.Show(Say.Status(EngineState.Active, Six, startedAt: Six, endsAt: Six.AddHours(1)));
        var active = (ActiveViewModel)app.Shell.CurrentScreen!;
        var announced = Watch(active);

        active.IsCustomExtendOpen = true;

        Assert.True(active.IsCustomExtendOpen);
        Assert.Equal([nameof(ActiveViewModel.IsCustomExtendOpen)], announced);
    }

    [Fact]
    public void TheCustomBoxStaysOpenAcrossAStatus()
    {
        using var app = new AppUnderTest(Six);
        app.Show(Say.Status(EngineState.Active, Six, startedAt: Six, endsAt: Six.AddHours(1)));
        var active = (ActiveViewModel)app.Shell.CurrentScreen!;
        active.IsCustomExtendOpen = true;
        active.ExtendMinutes = 25;

        app.Show(Say.Status(EngineState.Active, Six, startedAt: Six, endsAt: Six.AddHours(1)));

        Assert.True(active.IsCustomExtendOpen);
        Assert.Equal(25, active.ExtendMinutes);
    }

    [Fact]
    public void TheEndIsSaidAsATimeOfDay()
    {
        using var culture = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Six);
        app.Show(Say.Status(EngineState.Active, Six, startedAt: Six, endsAt: Six.AddMinutes(105)));

        var until = Six.AddMinutes(105).ToLocalTime().ToString("t", CultureInfo.CurrentUICulture);
        Assert.Equal($"до {until}", ((ActiveViewModel)app.Shell.CurrentScreen!).EndsAtText);
    }

    [Fact]
    public void WithoutAnEndNothingIsSaid()
    {
        using var app = new AppUnderTest(Six);
        app.Show(Say.Status(EngineState.Active, Six, startedAt: Six));

        Assert.Equal(string.Empty, ((ActiveViewModel)app.Shell.CurrentScreen!).EndsAtText);
    }

    [Fact]
    public void TheEndIsRewordedWithTheLanguage()
    {
        using var culture = new UiCulture("en-US");
        using var app = new AppUnderTest(Six);
        app.Show(Say.Status(EngineState.Active, Six, startedAt: Six, endsAt: Six.AddMinutes(105)));
        var active = (ActiveViewModel)app.Shell.CurrentScreen!;
        var announced = Watch(active);

        Assert.StartsWith("until ", active.EndsAtText, StringComparison.Ordinal);

        app.Language.Use("ru-RU");

        Assert.Contains(nameof(ActiveViewModel.EndsAtText), announced);
        Assert.StartsWith("до ", active.EndsAtText, StringComparison.Ordinal);
    }

    [Fact]
    public void AStatusAnnouncesTheEndAndTheSand()
    {
        using var app = new AppUnderTest(Six);
        app.Show(Say.Status(EngineState.Active, Six, startedAt: Six, endsAt: Six.AddHours(1)));
        var active = (ActiveViewModel)app.Shell.CurrentScreen!;
        var announced = Watch(active);

        app.Show(Say.Status(EngineState.Active, Six, startedAt: Six, endsAt: Six.AddHours(2)));

        Assert.Contains(nameof(ActiveViewModel.EndsAtText), announced);
        Assert.Contains(nameof(ActiveViewModel.Progress), announced);
    }

    [Fact]
    public void ALanguageChangeAnnouncesTheWaitingCountdownAndNothingElse()
    {
        using var culture = new UiCulture("en-US");
        using var app = new AppUnderTest(Six);
        app.Show(Say.Status(EngineState.UnlockPending, Six, endsAt: Six.AddHours(1), unlockAt: Six.AddMinutes(10)));
        var waiting = (WaitingViewModel)app.Shell.CurrentScreen!;
        var announced = Watch(waiting);

        app.Language.Use("ru-RU");

        Assert.Equal([nameof(WaitingViewModel.RemainingText)], announced);
    }

    [Fact]
    public void NewKeysAreWordedInBothLanguages()
    {
        using var culture = new UiCulture("en-US");
        var text = new Text(new LanguageSwitch());

        Assert.Equal("until {0}", text.EndsAtFormat);
        Assert.Equal("Extend", text.ExtendHeading);
        Assert.Equal("Custom…", text.CustomExtend);
        Assert.Equal("Unlocks in", text.UnlockHeading);

        using var russian = new UiCulture("ru-RU");

        Assert.Equal("до {0}", text.EndsAtFormat);
        Assert.Equal("Продлить", text.ExtendHeading);
        Assert.Equal("Своё…", text.CustomExtend);
        Assert.Equal("Снятие через", text.UnlockHeading);
        Assert.Equal("Попросить снятие…", text.RequestUnlock);
        Assert.Equal("Продолжить сессию", text.CancelUnlock);
    }

    [Fact]
    public void TheCancelButtonIsCentredAndSizedByItsText()
    {
        var markup = File.ReadAllText(Repo.App("Views", "WaitingView.axaml"));
        var button = Regex.Match(markup, @"<Button\b[^>]*CancelCommand[^>]*>", RegexOptions.Singleline).Value;

        Assert.Contains("HorizontalAlignment=\"Center\"", button, StringComparison.Ordinal);
        Assert.DoesNotContain("Stretch", button, StringComparison.Ordinal);
        Assert.DoesNotContain("Width=", button, StringComparison.Ordinal);
    }

    [Fact]
    public void TheActivePageShowsTheGlassTheEndAndTheChips()
    {
        var markup = File.ReadAllText(Repo.App("Views", "SessionActivePage.axaml"));

        Assert.Matches(@"<c:Hourglass\b[^>]*Progress=""\{Binding Progress\}""", markup);
        Assert.Contains("{Binding RemainingCaption}", markup, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ExtendChoices}\"", markup, StringComparison.Ordinal);
        Assert.Contains("ExtendByCommand", markup, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsCustomExtendOpen}\"", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding Notice}", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWaitingViewShowsTheGlass()
    {
        var markup = File.ReadAllText(Repo.App("Views", "WaitingView.axaml"));

        Assert.Matches(@"<c:Hourglass\b[^>]*Progress=""\{Binding Progress\}""", markup);
        Assert.Contains("Text.UnlockHeading", markup, StringComparison.Ordinal);
    }

    private static List<string> Watch(INotifyPropertyChanged model)
    {
        var names = new List<string>();
        model.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);

        return names;
    }
}
