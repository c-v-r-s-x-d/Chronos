using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.Time;
using Chronos.App.ViewModels;
using Chronos.Core.Sessions;

namespace Chronos.App.Tests;

/// <summary>Three screens, one per engine state. A fourth would disagree with what the service is doing.</summary>
[Collection(CultureBound.Name)]
public sealed class ScreenSwitchingTests
{
    private static readonly DateTimeOffset Moment = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Screen_FollowsTheEngineStateWithoutTheUserDoingAnything()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Idle", Moment));
        Assert.IsType<SetupViewModel>(app.Shell.CurrentScreen);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(50)));
        Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);

        app.Show(Say.Status("UnlockPending", Moment, endsAt: Moment.AddMinutes(50), unlockAt: Moment.AddMinutes(30)));
        Assert.IsType<WaitingViewModel>(app.Shell.CurrentScreen);

        app.Show(Say.Status("Idle", Moment));
        Assert.IsType<SetupViewModel>(app.Shell.CurrentScreen);
    }

    /// <summary>Ended lasts seconds and is clearing up, so it gets the active screen with a word about what is happening.</summary>
    [Fact]
    public void Screen_ForTheEndedStateIsTheActiveOneSayingTheSessionIsFinishing()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(1)));
        Assert.False(Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen).IsFinishing);

        app.Show(Say.Status("Ended", Moment, endsAt: Moment));

        var screen = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);
        Assert.True(screen.IsFinishing);
    }

    /// <summary>The service being away is not a screen; the shell's own message is shown.</summary>
    [Fact]
    public void Screen_IsNoneWhileTheServiceIsAway()
    {
        using var app = new AppUnderTest(Moment);

        Assert.Null(app.Shell.CurrentScreen);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(50)));
        Assert.NotNull(app.Shell.CurrentScreen);

        app.Link.Publish(ServiceSnapshot.Unavailable);
        Assert.Null(app.Shell.CurrentScreen);
    }

    /// <summary>The one-to-one claim, read off the engine: a fifth state in the domain fails this.</summary>
    [Fact]
    public void EveryStateTheEngineHasLandsOnOneOfTheThreeScreens()
    {
        var states = Enum.GetNames<SessionState>();
        Assert.Equal(4, states.Length);

        foreach (var state in states)
        {
            Assert.True(Screen.For(state) is not null, $"The engine state {state} lands on no screen.");
        }

        Assert.Equal(3, Enum.GetValues<ScreenKind>().Length);
    }

    /// <summary>An unknown state word gets the setup screen, which claims no remaining time.</summary>
    [Fact]
    public void AStateThisBuildDoesNotKnowLandsOnNoScreenOfItsOwn()
    {
        using var app = new AppUnderTest(Moment);

        Assert.Null(Screen.For("Hibernating"));

        app.Show(Say.Status("Hibernating", Moment));
        Assert.IsType<SetupViewModel>(app.Shell.CurrentScreen);
    }

    /// <summary>The state reaches the user as a screen, never as a wire token such as "UnlockPending".</summary>
    [Theory]
    [InlineData("Idle")]
    [InlineData("Active")]
    [InlineData("UnlockPending")]
    [InlineData("Ended")]
    public void Screen_ShowsNoWordFromTheWireToThePerson(string state)
    {
        using var restore = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status(state, Moment, endsAt: Moment.AddMinutes(50), unlockAt: Moment.AddMinutes(30)));

        foreach (var shown in Readable(app.Shell))
        {
            foreach (var word in Enum.GetNames<SessionState>())
            {
                Assert.DoesNotContain(word, shown, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>The screen sends the cancellation and returns to the active session on the next status.</summary>
    [Fact]
    public void Cancelling_ReturnsToTheActiveScreen()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("UnlockPending", Moment, endsAt: Moment.AddMinutes(50), unlockAt: Moment.AddSeconds(30)));
        var waiting = Assert.IsType<WaitingViewModel>(app.Shell.CurrentScreen);

        // What the real service does: answer, then publish the new state. Only for a command that changed
        // something: a fake that published for GetConfig would loop through the active screen's own read.
        app.Link.Service = request =>
        {
            if (request.Command == "CancelUnlock")
            {
                app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(50)));
            }
        };

        Assert.True(waiting.CancelCommand.CanExecute(null));
        waiting.CancelCommand.Execute(null);

        Assert.Equal("CancelUnlock", app.Link.Sent[0].Command);
        Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);

        // Nothing else that changes anything: the screen that took over reads the configuration.
        Assert.All(app.Link.Sent.Skip(1), request => Assert.Equal("GetConfig", request.Command));
    }

    [Fact]
    public void WhenTheWaitExpires_TheSetupScreenTakesOver()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("UnlockPending", Moment, endsAt: Moment.AddMinutes(50), unlockAt: Moment.AddSeconds(30)));
        Assert.IsType<WaitingViewModel>(app.Shell.CurrentScreen);

        // Nothing the user did: the service reached the moment and cleared the session.
        app.Show(Say.Status("Idle", Moment.AddSeconds(30)));

        Assert.IsType<SetupViewModel>(app.Shell.CurrentScreen);

        // The setup screen reads the configuration as it appears; nothing that starts, ends or changes anything went down the pipe.
        Assert.All(app.Link.Sent, request => Assert.Equal("GetConfig", request.Command));
    }

    /// <summary>The words the screens put on the wire are ones a real service accepts.</summary>
    [Fact]
    public async Task TheCommandsTheScreensSendAreOnesTheServiceAccepts()
    {
        await using var service = new ServiceHarness();
        service.Start();

        await using var link = new ServiceLink(service.PipeName);
        var text = new Text(new LanguageSwitch());

        link.Start();
        await LinkWait.ForAsync(link, snapshot => snapshot.Status is not null, "reached the service");

        var setup = new SetupViewModel(link, text, new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));
        await setup.StartCommand.ExecuteAsync(null);
        var active = await LinkWait.ForAsync(link, snapshot => snapshot.Status?.State == "Active", "saw a session start");

        var running = new ActiveViewModel(link, text, new ServiceClock(new FakeClock(Moment)));
        await running.RequestUnlockCommand.ExecuteAsync(null);
        await LinkWait.ForAsync(link, snapshot => snapshot.Status?.State == "UnlockPending", "saw the unlock request");

        var waiting = new WaitingViewModel(link, text, new ServiceClock(new FakeClock(Moment)));
        await waiting.CancelCommand.ExecuteAsync(null);
        await LinkWait.ForAsync(link, snapshot => snapshot.Status?.State == "Active", "saw the request cancelled");

        // The session the setup screen asked for has an end.
        Assert.NotNull(active.Status?.EndsAt);
    }

    private static IEnumerable<string> Readable(ShellViewModel shell)
    {
        var words = new List<string> { shell.ServiceMessage };

        if (shell.CurrentScreen is { } screen)
        {
            words.AddRange(
                screen.GetType()
                    .GetProperties()
                    .Where(property =>
                        property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0)
                    .Select(property => property.GetValue(screen) as string ?? string.Empty));
        }

        return words;
    }
}
