using Chronos.App.Resources;
using Chronos.App.Services;

namespace Chronos.App.Tests;

/// <summary>Quitting during a session does not lift the block; the warning is about what it does cost: the block screens and notices.</summary>
[Collection(CultureBound.Name)]
public sealed class ExitWarningTests
{
    private static readonly DateTimeOffset Moment = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RequestingExitDuringASession_WarnsAndDoesNotQuit()
    {
        var quits = 0;
        using var app = new AppUnderTest(Moment, () => quits++);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(47)));
        app.Shell.Exit.RequestCommand.Execute(null);

        Assert.True(app.Shell.Exit.IsWarningShowing);
        Assert.Equal(0, quits);
        Assert.False(app.Shell.Exit.IsQuitting);
    }

    [Fact]
    public void ConfirmingTheWarning_Quits()
    {
        var quits = 0;
        using var app = new AppUnderTest(Moment, () => quits++);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(47)));
        app.Shell.Exit.RequestCommand.Execute(null);
        app.Shell.Exit.ConfirmCommand.Execute(null);

        Assert.Equal(1, quits);
        Assert.True(app.Shell.Exit.IsQuitting);
    }

    [Fact]
    public void DismissingTheWarning_KeepsTheProcessAndPutsTheWarningAway()
    {
        var quits = 0;
        using var app = new AppUnderTest(Moment, () => quits++);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(47)));
        app.Shell.Exit.RequestCommand.Execute(null);
        app.Shell.Exit.CancelCommand.Execute(null);

        Assert.False(app.Shell.Exit.IsWarningShowing);
        Assert.False(app.Shell.Exit.IsQuitting);
        Assert.Equal(0, quits);
    }

    /// <summary>Confirming is only a way out of an asked warning; a stray command must not quit.</summary>
    [Fact]
    public void ConfirmingWithNoWarningShowing_DoesNothing()
    {
        var quits = 0;
        using var app = new AppUnderTest(Moment, () => quits++);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(47)));
        app.Shell.Exit.ConfirmCommand.Execute(null);

        Assert.Equal(0, quits);
        Assert.False(app.Shell.Exit.IsQuitting);
    }

    /// <summary>Waiting for the unlock is a session with a block in force, so quitting is warned about the same way.</summary>
    [Fact]
    public void RequestingExitWhileWaitingForTheUnlock_Warns()
    {
        using var app = new AppUnderTest(Moment, () => { });

        app.Show(Say.Status("UnlockPending", Moment, endsAt: Moment.AddHours(3), unlockAt: Moment.AddMinutes(20)));
        app.Shell.Exit.RequestCommand.Execute(null);

        Assert.True(app.Shell.Exit.IsWarningShowing);
    }

    /// <summary>With no session there is nothing to warn about.</summary>
    [Fact]
    public void RequestingExitWithNoSession_QuitsWithoutAsking()
    {
        var quits = 0;
        using var app = new AppUnderTest(Moment, () => quits++);

        app.Show(Say.Status("Idle", Moment));
        app.Shell.Exit.RequestCommand.Execute(null);

        Assert.False(app.Shell.Exit.IsWarningShowing);
        Assert.Equal(1, quits);
        Assert.True(app.Shell.Exit.IsQuitting);
    }

    /// <summary>With no service the interface has not been told there is a session, so it must not warn as if there were.</summary>
    [Fact]
    public void RequestingExitWithNoService_QuitsWithoutAsking()
    {
        var quits = 0;
        using var app = new AppUnderTest(Moment, () => quits++);

        app.Link.Publish(ServiceSnapshot.Unavailable);
        app.Shell.Exit.RequestCommand.Execute(null);

        Assert.False(app.Shell.Exit.IsWarningShowing);
        Assert.Equal(1, quits);
    }

    /// <summary>The window cancels its own closing while this is false, so the process outlives the window.</summary>
    [Fact]
    public void NothingIsQuittingUntilTheExitIsTaken()
    {
        using var app = new AppUnderTest(Moment, () => { });

        Assert.False(app.Shell.Exit.IsQuitting);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(47)));
        Assert.False(app.Shell.Exit.IsQuitting);

        app.Shell.Exit.RequestCommand.Execute(null);
        Assert.False(app.Shell.Exit.IsQuitting);
    }

    [Fact]
    public void AskingTwice_QuitsOnce()
    {
        var quits = 0;
        using var app = new AppUnderTest(Moment, () => quits++);

        app.Show(Say.Status("Idle", Moment));
        app.Shell.Exit.RequestCommand.Execute(null);
        app.Shell.Exit.RequestCommand.Execute(null);

        Assert.Equal(1, quits);
    }

    /// <summary>Both halves are said: the block survives, the screens and notices do not.</summary>
    [Fact]
    public void TheWarningSaysWhatSurvivesTheExitAndWhatDoesNot()
    {
        using var language = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment, () => { });

        Assert.Equal(Strings.ExitWarning, app.Shell.Exit.Text.ExitWarning);
        Assert.Contains("does not lift the block", Strings.ExitWarning, StringComparison.Ordinal);
        Assert.Contains("no block screens", Strings.ExitWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWarningIsShownAndPutAwayLoudlyEnoughForAWindowToNotice()
    {
        using var app = new AppUnderTest(Moment, () => { });
        var announced = new List<string>();

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(47)));
        app.Shell.Exit.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        app.Shell.Exit.RequestCommand.Execute(null);
        Assert.Contains(nameof(app.Shell.Exit.IsWarningShowing), announced);

        announced.Clear();
        app.Shell.Exit.CancelCommand.Execute(null);
        Assert.Contains(nameof(app.Shell.Exit.IsWarningShowing), announced);
    }
}
