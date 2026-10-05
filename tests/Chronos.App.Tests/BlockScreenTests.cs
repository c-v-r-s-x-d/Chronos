using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Chronos.App.Blocking;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.ViewModels;

namespace Chronos.App.Tests;

/// <summary>The block window names what was closed and how much session is left. It is not full-screen; that is read off the markup.</summary>
[Collection(CultureBound.Name)]
public sealed class BlockScreenTests
{
    private static readonly DateTimeOffset Machine = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Service = Machine + TimeSpan.FromMinutes(7);

    [Fact]
    public void TheScreenNamesTheApplicationThatWasBlocked()
    {
        using var restore = new UiCulture("en-US");
        using var screen = new Screen("steam.exe", TimeSpan.FromMinutes(42));

        Assert.Contains("steam.exe", screen.Model.Heading, StringComparison.Ordinal);
    }

    /// <summary>The remaining time is arithmetic on the event's own status. The link holds a different session on purpose.</summary>
    [Fact]
    public void TheRemainingTimeComesFromTheEventsOwnStatusAndNothingIsAsked()
    {
        using var restore = new UiCulture("en-US");
        using var screen = new Screen("steam.exe", TimeSpan.FromMinutes(42), linkSays: TimeSpan.FromMinutes(7));

        Assert.Contains("42 minutes", screen.Model.RemainingText, StringComparison.Ordinal);
        // Seven, not two: "42 minutes" contains "2 minutes".
        Assert.DoesNotContain("7 minutes", screen.Model.RemainingText, StringComparison.Ordinal);

        // Nothing was asked of the service to draw this screen.
        Assert.Empty(screen.Link.Sent);
    }

    /// <summary>The offset between the two clocks is not part of the answer; both times come from the service.</summary>
    [Fact]
    public void AServiceClockThatIsMinutesOutDoesNotChangeTheRemainingTime()
    {
        using var restore = new UiCulture("en-US");
        using var screen = new Screen("steam.exe", TimeSpan.FromMinutes(42));

        Assert.Contains("42 minutes", screen.Model.RemainingText, StringComparison.Ordinal);
    }

    /// <summary>It counts down and notifies; a property read alone would pass with no notification.</summary>
    [Fact]
    public void TheScreenCountsDownAndSaysThatItChanged()
    {
        using var restore = new UiCulture("en-US");
        using var screen = new Screen("steam.exe", TimeSpan.FromMinutes(42));
        var announced = Watch(screen.Model);

        screen.Clock.Advance(TimeSpan.FromMinutes(10));
        screen.Ticker.Tick();

        Assert.Contains("32 minutes", screen.Model.RemainingText, StringComparison.Ordinal);
        Assert.Contains(nameof(BlockViewModel.RemainingText), announced);
    }

    [Fact]
    public void ASessionThatHasRunOutCountsDownToZeroRatherThanPastIt()
    {
        using var screen = new Screen("steam.exe", TimeSpan.FromMinutes(1));

        screen.Clock.Advance(TimeSpan.FromMinutes(10));
        screen.Ticker.Tick();

        Assert.Equal(TimeSpan.Zero, screen.Model.Remaining);
    }

    /// <summary>Both sentences are built here, so nothing redraws them unless this object says they changed.</summary>
    [Fact]
    public void ChangingTheLanguageRedrawsWhatTheScreenSays()
    {
        using var restore = new UiCulture("en-US");
        using var screen = new Screen("steam.exe", TimeSpan.FromMinutes(42));
        var announced = Watch(screen.Model);

        var english = screen.Model.RemainingText;
        screen.Language.Use("ru-RU");

        Assert.NotEqual(english, screen.Model.RemainingText);
        Assert.Contains(nameof(BlockViewModel.RemainingText), announced);
        Assert.Contains(nameof(BlockViewModel.Heading), announced);
    }

    [Fact]
    public void TheWindowShowsTheHourglassAndTheEnd()
    {
        var markup = File.ReadAllText(Repo.App("Views", "BlockScreen.axaml"));

        Assert.Matches(@"<c:Hourglass\b[^>]*Progress=""{Binding Progress}""", markup);
        Assert.Contains("{Binding EndsAtText}", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSandIsTheShareOfTheSessionTheEventSays()
    {
        using var screen = new Screen("steam.exe", TimeSpan.FromMinutes(42), ran: TimeSpan.FromMinutes(18));

        Assert.Equal(0.3, screen.Model.Progress, 3);

        screen.Clock.Advance(TimeSpan.FromMinutes(12));

        Assert.Equal(0.5, screen.Model.Progress, 3);
    }

    [Fact]
    public void WithoutAStartThereIsNoSandDownAndPastTheEndItIsAllDown()
    {
        using var none = new Screen("steam.exe", TimeSpan.FromMinutes(42));
        using var over = new Screen("steam.exe", TimeSpan.FromMinutes(1), ran: TimeSpan.FromMinutes(59));

        over.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(0, none.Model.Progress);
        Assert.Equal(1, over.Model.Progress);
    }

    [Fact]
    public void ATickAnnouncesTheClockReadsAndNothingElse()
    {
        using var screen = new Screen("steam.exe", TimeSpan.FromMinutes(42), ran: TimeSpan.FromMinutes(18));
        var announced = Watch(screen.Model);

        screen.Ticker.Tick();

        Assert.Equal(
            [nameof(BlockViewModel.Progress), nameof(BlockViewModel.Remaining), nameof(BlockViewModel.RemainingText)],
            announced.Order());
    }

    [Fact]
    public void TheEndIsSaidAsATimeOfDayAndRewordedWithTheLanguage()
    {
        using var restore = new UiCulture("en-US");
        using var screen = new Screen("steam.exe", TimeSpan.FromMinutes(42));
        var announced = Watch(screen.Model);
        var time = (Service + TimeSpan.FromMinutes(42)).ToLocalTime().ToString("t", CultureInfo.CurrentUICulture);

        Assert.Equal($"until {time}", screen.Model.EndsAtText);

        screen.Language.Use("ru-RU");

        Assert.Contains(nameof(BlockViewModel.EndsAtText), announced);
        Assert.StartsWith("до ", screen.Model.EndsAtText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheApplicationsNameSurvivesTheChangeOfLanguage()
    {
        using var restore = new UiCulture("en-US");
        using var screen = new Screen("steam.exe", TimeSpan.FromMinutes(42));

        screen.Language.Use("ru-RU");

        Assert.Contains("steam.exe", screen.Model.Heading, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScreenNamesTheSiteThatWasRefused()
    {
        using var restore = new UiCulture("en-US");
        using var screen = new Screen("reddit.com", TimeSpan.FromMinutes(42), site: true);

        Assert.Equal(
            string.Format(CultureInfo.InvariantCulture, screen.Text.BlockedSiteFormat, "reddit.com"),
            screen.Model.Heading);
        Assert.NotEqual(screen.Text.BlockedAppFormat, screen.Text.BlockedSiteFormat);
    }

    [Fact]
    public void ARefusedSiteCountsDownFromItsOwnEvent()
    {
        using var restore = new UiCulture("en-US");
        using var screen = new Screen("reddit.com", TimeSpan.FromMinutes(42), linkSays: TimeSpan.FromMinutes(7), site: true);

        Assert.Contains("42 minutes", screen.Model.RemainingText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSiteIsNamedInRussianToo()
    {
        using var restore = new UiCulture("en-US");
        using var screen = new Screen("reddit.com", TimeSpan.FromMinutes(42), site: true);
        var english = screen.Model.Heading;

        screen.Language.Use("ru-RU");

        Assert.NotEqual(english, screen.Model.Heading);
        Assert.Contains("reddit.com", screen.Model.Heading, StringComparison.Ordinal);
        Assert.Matches("[А-Яа-яЁё]", screen.Model.Heading);
    }

    /// <summary>A domain can run to 253 characters; the heading gives way before the countdown and button are pushed out.</summary>
    [Fact]
    public void ALongNameCannotPushTheRestOfTheWindowOutOfSight()
    {
        var markup = File.ReadAllText(Repo.App("Views", "BlockScreen.axaml"));
        var heading = Regex.Match(markup, @"<TextBlock\s+Text=""\{Binding Heading\}""[^>]*>").Value;

        Assert.Matches(@"MaxLines=""\d+""", heading);
        Assert.Matches(@"TextTrimming=""\w+Ellipsis""", heading);
    }

    /// <summary>A window with no size of its own and state FullScreen or Maximized is ruled out.</summary>
    [Fact]
    public void TheBlockWindowIsNotFullScreen()
    {
        var markup = File.ReadAllText(Repo.App("Views", "BlockScreen.axaml"));

        Assert.Null(Attribute(markup, "WindowState"));

        var width = Attribute(markup, "Width");
        var height = Attribute(markup, "Height");

        Assert.True(int.TryParse(width, out var across), $"The window has no width of its own: {width ?? "none"}.");
        Assert.True(int.TryParse(height, out var down), $"The window has no height of its own: {height ?? "none"}.");

        // Small enough that what is behind it stays visible.
        Assert.InRange(across, 1, 720);
        Assert.InRange(down, 1, 480);
    }

    /// <summary>Proves the reader above can fail, so a null for the real file means the attribute is absent.</summary>
    [Fact]
    public void TheReaderWouldNoticeAWindowHeldOpenAcrossTheScreen()
    {
        const string Pinned = "<Window WindowState=\"FullScreen\" Width=\"1920\" Height=\"1080\" />";

        Assert.Equal("FullScreen", Attribute(Pinned, "WindowState"));
        Assert.Equal("1920", Attribute(Pinned, "Width"));
        Assert.Null(Attribute("<Window Title=\"x\" />", "WindowState"));
    }

    /// <summary>The wiring lives where Avalonia does and needs a windowing system to build, so it is read off the file.</summary>
    [Fact]
    public void TheApplicationOpensABlockWindowWhenTheLinkReportsOne()
    {
        var wiring = File.ReadAllText(Repo.App("App.axaml.cs"));

        Assert.Contains(nameof(BlockNotices), wiring, StringComparison.Ordinal);
        Assert.Contains(nameof(BlockScreenRate), wiring, StringComparison.Ordinal);
        Assert.Contains("BlockScreen", wiring, StringComparison.Ordinal);
    }

    /// <summary>Proves the reader looks at the real file; "contains" checks pass vacuously on an empty string.</summary>
    [Fact]
    public void TheWiringReaderIsLookingAtARealFile()
    {
        var wiring = File.ReadAllText(Repo.App("App.axaml.cs"));

        Assert.Contains(nameof(ShellViewModel), wiring, StringComparison.Ordinal);

        // And can say no.
        Assert.DoesNotContain("NoSuchThingIsWiredUp", wiring, StringComparison.Ordinal);
    }

    private static string? Attribute(string markup, string name)
    {
        var match = Regex.Match(markup, name + @"\s*=\s*""(?<value>[^""]*)""");

        return match.Success ? match.Groups["value"].Value : null;
    }

    private static List<string> Watch(BlockViewModel model)
    {
        var names = new List<string>();
        ((INotifyPropertyChanged)model).PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);

        return names;
    }

    /// <summary>One block delivered down the link, through the limit, into a screen built from the event alone.</summary>
    private sealed class Screen : IDisposable
    {
        private readonly BlockNotices _notices;

        public Screen(
            string appName, TimeSpan left, TimeSpan? linkSays = null, bool site = false, TimeSpan? ran = null)
        {
            Text = new Text(Language);

            BlockViewModel? built = null;
            _notices = new BlockNotices(
                Link,
                new BlockScreenRate(Clock),
                block => built = new BlockViewModel(block, Text, Clock, Ticker));

            if (linkSays is { } other)
            {
                Link.Publish(ServiceSnapshot.Available(
                    Say.Status("Active", Service, endsAt: Service + other)));
            }

            // A site comes with the DNS layer working, or it would never reach a screen.
            var status = Say.Status(
                "Active",
                Service,
                endsAt: Service + left,
                layers: site ? [Say.Layer("dns")] : null,
                startedAt: ran is { } gone ? Service - gone : null);

            Link.PublishBlock(site ? new SiteBlock(appName, status) : new AppBlock(appName, status));

            Assert.True(built is not null, "The block never reached a screen.");
            Model = built!;
        }

        public RecordingLink Link { get; } = new();

        public FakeClock Clock { get; } = new(Machine);

        public FakeTicker Ticker { get; } = new();

        public LanguageSwitch Language { get; } = new();

        public Text Text { get; }

        public BlockViewModel Model { get; }

        public void Dispose()
        {
            Model.Dispose();
            _notices.Dispose();
        }
    }
}
