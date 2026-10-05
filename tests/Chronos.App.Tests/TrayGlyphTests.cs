using Chronos.App.Services;
using Chronos.App.ViewModels;

namespace Chronos.App.Tests;

/// <summary>The icon differs between a state with a block in force and one without; which picture is the view's business.</summary>
public sealed class TrayGlyphTests
{
    private static readonly DateTimeOffset Moment = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Glyph_DiffersBetweenASessionAndNoSession()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(47)));
        var blocking = app.Shell.Tray.Glyph;

        app.Show(Say.Status("Idle", Moment));
        var idle = app.Shell.Tray.Glyph;

        Assert.Equal(TrayGlyph.Blocking, blocking);
        Assert.Equal(TrayGlyph.Idle, idle);
        Assert.NotEqual(blocking, idle);
    }

    /// <summary>Waiting for the unlock is still a block; a quiet icon would say the opposite.</summary>
    [Fact]
    public void Glyph_WhileWaitingForTheUnlock_StillShowsABlock()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("UnlockPending", Moment, endsAt: Moment.AddHours(3), unlockAt: Moment.AddMinutes(20)));

        Assert.Equal(TrayGlyph.Blocking, app.Shell.Tray.Glyph);
    }

    [Fact]
    public void Glyph_WhileTheSessionIsFinishing_StillShowsABlock()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Ended", Moment, endsAt: Moment));

        Assert.Equal(TrayGlyph.Blocking, app.Shell.Tray.Glyph);
    }

    /// <summary>With no service neither icon may be claimed: three situations, three pictures.</summary>
    [Fact]
    public void Glyph_WithNoService_IsNeitherOfTheTwo()
    {
        using var app = new AppUnderTest(Moment);

        app.Link.Publish(ServiceSnapshot.Unavailable);

        Assert.Equal(TrayGlyph.Unknown, app.Shell.Tray.Glyph);
    }

    [Fact]
    public void Glyph_BeforeTheFirstAnswer_IsNeitherOfTheTwo()
    {
        using var app = new AppUnderTest(Moment);

        Assert.Equal(TrayGlyph.Unknown, app.Shell.Tray.Glyph);
    }

    /// <summary>An unknown state word is not no session; reading it as one would show the not-blocking icon during a session.</summary>
    [Fact]
    public void Glyph_ForAStateWordThisBuildDoesNotKnow_IsNeitherOfTheTwo()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("SomethingLater", Moment));

        Assert.Equal(TrayGlyph.Unknown, app.Shell.Tray.Glyph);
    }

    [Fact]
    public void Glyph_SaysThatItChangedWhenTheStateDoes()
    {
        using var app = new AppUnderTest(Moment);
        var announced = new List<string>();

        app.Show(Say.Status("Idle", Moment));
        app.Shell.Tray.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddMinutes(47)));

        Assert.Contains(nameof(TrayViewModel.Glyph), announced);
    }
}
