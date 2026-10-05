using Chronos.App.ViewModels;
using Chronos.Ipc;

namespace Chronos.App.Tests;

/// <summary>The session section before a session: what the lists hold and the starting length. The layers are in <see cref="SettingsTests"/>.</summary>
[Collection(CultureBound.Name)]
public sealed class SetupScreenTests
{
    private static readonly DateTimeOffset Moment = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// StatusPayload.Sites and .Apps are the running session's rules and are empty without one; the
    /// configured lists arrive through GetConfig. A screen reading them from the status would be blank.
    /// </summary>
    [Fact]
    public async Task TheRulesComeFromTheConfigurationAndNotFromTheStatus()
    {
        using var app = new AppUnderTest(Moment);

        app.Link.Answer = _ => IpcResponse.Ok(
            Say.Status("Idle", Moment),
            Say.Config(sites: ["one.example", "two.example", "three.example"], apps: ["game.exe"]));

        // A status shaped like a running session's, and never the source.
        app.Show(Say.Status("Idle", Moment, sites: ["from-the-session.example"], apps: ["session.exe"]));

        var screen = await Setup(app);

        Assert.Equal(
            new[] { "one.example", "three.example", "two.example" },
            screen.Sites.Select(site => site.Domain).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "game.exe" }, screen.Apps.Select(rule => rule.Value).ToArray());

        Assert.DoesNotContain(screen.Sites, site => site.Domain == "from-the-session.example");
        Assert.DoesNotContain(screen.Apps, rule => rule.Value == "session.exe");
    }

    [Fact]
    public async Task TheScreenAsksTheServiceForTheConfiguration()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Idle", Moment));
        await Setup(app);

        Assert.Contains(app.Link.Sent, request => request.Command == "GetConfig");
    }

    /// <summary>The length in the box starts from the service's own default, not an hour made up here.</summary>
    [Fact]
    public async Task TheSessionLengthStartsFromTheServicesDefault()
    {
        using var app = new AppUnderTest(Moment);

        // Not the fallback, or the test would pass on a screen that reads nothing.
        Assert.NotEqual(SetupViewModel.DefaultMinutes, 45);

        app.Link.Answer = _ => IpcResponse.Ok(Say.Status("Idle", Moment), Say.Config(defaultMinutes: 45));
        app.Show(Say.Status("Idle", Moment));

        Assert.Equal(45, (await Setup(app)).Minutes);
    }

    /// <summary>A default is a starting point: a status arriving mid-decision must not put the box back.</summary>
    [Fact]
    public async Task ALengthThePersonChoseIsNotOverwrittenByTheNextStatus()
    {
        using var app = new AppUnderTest(Moment);

        app.Link.Answer = _ => IpcResponse.Ok(Say.Status("Idle", Moment), Say.Config(defaultMinutes: 45));
        app.Show(Say.Status("Idle", Moment));

        var screen = await Setup(app);
        screen.Minutes = 90;

        app.Show(Say.Status("Idle", Moment));
        await screen.Loading;

        Assert.Equal(90, screen.Minutes);
    }

    private static async Task<SetupViewModel> Setup(AppUnderTest app)
    {
        var screen = Assert.IsType<SetupViewModel>(app.Shell.CurrentScreen);
        await screen.Loading;

        return screen;
    }
}
