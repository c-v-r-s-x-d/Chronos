using Chronos.App.Blocking;
using Chronos.App.Services;
using Chronos.Ipc;

namespace Chronos.App.Tests;

/// <summary>Where an event becomes a window: a refused site uses the same screen as a closed application, and only while the DNS layer, the one that sees an attempt, is known to be working.</summary>
public sealed class BlockNoticesTests
{
    private static readonly DateTimeOffset Machine = new(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ARefusedSiteIsShownWhenTheDnsLayerIsWorking()
    {
        using var watch = new Watching();

        watch.Site("reddit.com", Dns(available: true));

        var only = Assert.IsType<SiteBlock>(Assert.Single(watch.Shown));
        Assert.Equal("reddit.com", only.Domain);
    }

    [Fact]
    public void NoSiteNoticeWhileTheDnsLayerIsUnavailable()
    {
        using var watch = new Watching();

        watch.Site("reddit.com", Say.Layer("wfp"), Dns(available: false));

        Assert.Empty(watch.Shown);
    }

    [Fact]
    public void NoSiteNoticeWhileTheDnsLayerHasNotReported()
    {
        using var watch = new Watching();

        // Unknown is not working, whatever the other layers say.
        watch.Site("reddit.com", Say.Layer("wfp"), Say.Layer("hosts"));

        Assert.Empty(watch.Shown);
    }

    [Fact]
    public void ASiteRefusedForWantOfTheLayerDoesNotSpendItsTurn()
    {
        using var watch = new Watching();

        watch.Site("reddit.com", Dns(available: false));
        watch.Site("reddit.com", Dns(available: true));

        Assert.Single(watch.Shown);
    }

    [Fact]
    public void OneSiteTriedTwentyTimesInAMinuteGivesOneNotice()
    {
        using var watch = new Watching();

        for (var attempt = 0; attempt < 20; attempt++)
        {
            watch.Site("reddit.com", Dns(available: true));
            watch.Clock.Advance(TimeSpan.FromSeconds(3));
        }

        Assert.Single(watch.Shown);
    }

    [Fact]
    public void TheSameSiteIsShownAgainOnceTheWindowHasElapsed()
    {
        using var watch = new Watching();

        watch.Site("reddit.com", Dns(available: true));
        watch.Clock.Advance(BlockScreenRate.Window);
        watch.Site("reddit.com", Dns(available: true));

        Assert.Equal(2, watch.Shown.Count);
    }

    [Fact]
    public void TwoSitesEachGetANotice()
    {
        using var watch = new Watching();

        watch.Site("reddit.com", Dns(available: true));
        watch.Site("youtube.com", Dns(available: true));

        Assert.Equal(["reddit.com", "youtube.com"], watch.Shown.Select(block => block.Target));
    }

    [Fact]
    public void ASiteNoticeWithNoDomainShowsNothing()
    {
        using var watch = new Watching();

        watch.Site(" ", Dns(available: true));

        Assert.Empty(watch.Shown);
    }

    [Fact]
    public void AClosedApplicationIsShownWhateverTheDnsLayerSays()
    {
        using var watch = new Watching();

        watch.Link.PublishBlock(new AppBlock("steam.exe", Status(Dns(available: false))));

        var only = Assert.IsType<AppBlock>(Assert.Single(watch.Shown));
        Assert.Equal("steam.exe", only.AppName);
    }

    [Fact]
    public void AnApplicationAndASiteWithTheSameNameEachGetANotice()
    {
        using var watch = new Watching();

        watch.Link.PublishBlock(new AppBlock("chess.com", Status(Dns(available: true))));
        watch.Site("chess.com", Dns(available: true));

        Assert.Equal(2, watch.Shown.Count);
    }

    [Fact]
    public void ABlockWhoseStatusHasNoLayersListShowsNoNotice()
    {
        using var watch = new Watching();
        var unusable = Say.Status("Active", Machine) with { Layers = null! };

        watch.Link.PublishBlock(new AppBlock("steam.exe", unusable));
        watch.Link.PublishBlock(new SiteBlock("reddit.com", unusable));

        Assert.Empty(watch.Shown);
    }

    [Fact]
    public void ABlockWhoseStatusHoldsANullLayerShowsNoNotice()
    {
        using var watch = new Watching();
        var unusable = Say.Status("Active", Machine) with { Layers = [null!] };

        watch.Link.PublishBlock(new AppBlock("steam.exe", unusable));

        Assert.Empty(watch.Shown);
    }

    [Fact]
    public void ABlockWhoseStatusIsWholeStillShowsItsNotice()
    {
        using var watch = new Watching();

        watch.Link.PublishBlock(new AppBlock("steam.exe", Status()));

        Assert.Single(watch.Shown);
    }

    [Fact]
    public void TheLayerThatSeesSitesIsNamedOnceOnTheWire()
    {
        Assert.Equal("dns", LayerNames.Dns);
    }

    private static LayerStatus Dns(bool available) =>
        Say.Layer("dns", available, available ? null : "dns.port-busy");

    private static StatusPayload Status(params LayerStatus[] layers) =>
        Say.Status("Active", Machine, endsAt: Machine + TimeSpan.FromMinutes(42), layers: layers);

    private sealed class Watching : IDisposable
    {
        private readonly List<BlockEvent> _shown = [];

        public Watching()
        {
            Notices = new BlockNotices(Link, new BlockScreenRate(Clock), _shown.Add);
        }

        public FakeClock Clock { get; } = new(Machine);

        public RecordingLink Link { get; } = new();

        public BlockNotices Notices { get; }

        public IReadOnlyList<BlockEvent> Shown => _shown;

        public void Site(string domain, params LayerStatus[] layers) =>
            Link.PublishBlock(new SiteBlock(domain, Status(layers)));

        public void Dispose() => Notices.Dispose();
    }
}
