using System.ComponentModel;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.ViewModels;
using Chronos.Ipc;

namespace Chronos.App.Tests;

[Collection(CultureBound.Name)]
public sealed class ShellViewModelTests
{
    private static readonly DateTimeOffset Moment = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static StatusPayload Active() =>
        new("Active", Moment, Moment.AddMinutes(-10), Moment.AddMinutes(50), null, 30, [], [], []);

    [Fact]
    public void Shell_StartsUnknownRatherThanClaimingEitherAnswer()
    {
        using var app = new AppUnderTest(Moment);

        Assert.True(app.Shell.IsConnecting);
        Assert.False(app.Shell.IsServiceAvailable);
        Assert.Null(app.Shell.CurrentScreen);
    }

    [Fact]
    public void Shell_FollowsTheLinkWithoutTheUserDoingAnything()
    {
        using var app = new AppUnderTest(Moment);
        var changed = Watch(app.Shell);

        app.Link.Publish(ServiceSnapshot.Available(Active()));

        Assert.True(app.Shell.IsServiceAvailable);
        Assert.False(app.Shell.IsConnecting);
        Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);
        Assert.Contains(nameof(ShellViewModel.IsServiceAvailable), changed);
        Assert.Contains(nameof(ShellViewModel.CurrentScreen), changed);
    }

    [Fact]
    public void Shell_ForgetsTheStatusWhenTheServiceGoesAway()
    {
        using var app = new AppUnderTest(Moment);

        app.Link.Publish(ServiceSnapshot.Available(Active()));
        app.Link.Publish(ServiceSnapshot.Unavailable);

        Assert.False(app.Shell.IsServiceAvailable);
        Assert.False(app.Shell.IsConnecting);

        // Yesterday's session must not show while the service is not there to confirm it.
        Assert.Null(app.Shell.CurrentScreen);
    }

    [Fact]
    public void Shell_SaysSomethingDifferentInEachOfTheThreeStates()
    {
        using var app = new AppUnderTest(Moment);

        var connecting = app.Shell.ServiceMessage;

        app.Link.Publish(ServiceSnapshot.Available(Active()));
        var available = app.Shell.ServiceMessage;

        app.Link.Publish(ServiceSnapshot.Unavailable);
        var unavailable = app.Shell.ServiceMessage;

        Assert.All(
            new[] { connecting, available, unavailable },
            message => Assert.False(string.IsNullOrWhiteSpace(message)));
        Assert.Equal(3, new HashSet<string>(new[] { connecting, available, unavailable }, StringComparer.Ordinal).Count);
    }

    [Fact]
    public void Shell_MarshalsTheChangeThroughThePostItWasGiven()
    {
        var link = new RecordingLink();
        var queued = new List<Action>();
        using var shell = new ShellViewModel(link, Words(), new FakeClock(Moment), new FakeTicker(), post: queued.Add);

        link.Publish(ServiceSnapshot.Available(Active()));

        // Nothing is applied yet: the link raises on its own thread, and the view model must not touch bound properties from there.
        Assert.False(shell.IsServiceAvailable);

        Assert.Single(queued);
        queued[0]();

        Assert.True(shell.IsServiceAvailable);
    }

    [Fact]
    public void Shell_StopsListeningOnceDisposed()
    {
        var app = new AppUnderTest(Moment);

        app.Dispose();
        app.Link.Publish(ServiceSnapshot.Available(Active()));

        Assert.False(app.Shell.IsServiceAvailable);
    }

    [Fact]
    public void Shell_StopsCountingOnceDisposed()
    {
        var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(50)));
        var screen = Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen);
        var announced = Watch(screen);

        app.Dispose();
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        app.Ticker.Tick();

        Assert.Empty(announced);
    }

    /// <summary>The one message markup cannot hold, since which of three sentences it is depends on state; it must still come from the resources.</summary>
    [Fact]
    public void Shell_ShowsTheWordsTheResourcesGiveRatherThanItsOwn()
    {
        using var restore = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Moment);

        Assert.Equal(app.Text.ServiceConnecting, app.Shell.ServiceMessage);

        app.Link.Publish(ServiceSnapshot.Available(Active()));
        Assert.Equal(app.Text.ServiceAvailable, app.Shell.ServiceMessage);

        app.Link.Publish(ServiceSnapshot.Unavailable);
        Assert.Equal(app.Text.ServiceUnavailableHeading, app.Shell.ServiceMessage);
    }

    /// <summary>A sentence chosen by the view model must change with the language and say that it did.</summary>
    [Fact]
    public void Shell_FollowsALanguageChangeWithoutBeingRebuilt()
    {
        using var restore = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);
        var changed = Watch(app.Shell);

        var english = app.Shell.ServiceMessage;
        app.Language.Use("ru-RU");

        Assert.NotEqual(english, app.Shell.ServiceMessage);
        Assert.Contains(nameof(ShellViewModel.ServiceMessage), changed);
    }

    [Fact]
    public void Shell_StopsFollowingTheLanguageOnceDisposed()
    {
        using var restore = new UiCulture("en-US");
        var app = new AppUnderTest(Moment);
        var changed = Watch(app.Shell);

        app.Dispose();
        app.Language.Use("ru-RU");

        Assert.Empty(changed);
    }

    private static Text Words() => new(new LanguageSwitch());

    private static List<string> Watch(INotifyPropertyChanged observable)
    {
        var names = new List<string>();
        observable.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);

        return names;
    }
}
