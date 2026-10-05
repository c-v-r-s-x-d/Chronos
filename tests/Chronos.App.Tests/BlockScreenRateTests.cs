using Chronos.App.Blocking;
using Chronos.App.Services;
using Chronos.Ipc;

namespace Chronos.App.Tests;

/// <summary>The rate limit driven through the whole path: link, limit, screen. It runs on a clock the test moves, not wall time.</summary>
public sealed class BlockScreenRateTests
{
    private static readonly DateTimeOffset Machine = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TwentyRestartsOfOneApplicationInAMinuteGiveOneScreen()
    {
        using var watch = new Watching();

        for (var restart = 0; restart < 20; restart++)
        {
            watch.Block("steam.exe");
            watch.Clock.Advance(TimeSpan.FromSeconds(3));
        }

        var only = Assert.Single(watch.Shown);
        Assert.Equal("steam.exe", only.Target);
    }

    /// <summary>Two applications are two events for the user, so a global limit would hide the second behind the first.</summary>
    [Fact]
    public void ABlockOfASecondApplicationIsNotHiddenBehindTheFirst()
    {
        using var watch = new Watching();

        for (var restart = 0; restart < 20; restart++)
        {
            watch.Block("steam.exe");
            watch.Clock.Advance(TimeSpan.FromSeconds(3));
        }

        watch.Block("discord.exe");

        Assert.Equal(["steam.exe", "discord.exe"], watch.Shown.Select(block => block.Target));
    }

    [Fact]
    public void TwoDifferentApplicationsAtTheSameMomentEachGetAScreen()
    {
        using var watch = new Watching();

        watch.Block("steam.exe");
        watch.Block("discord.exe");

        Assert.Equal(2, watch.Shown.Count);
    }

    [Fact]
    public void TheSameApplicationGetsAnotherScreenOnceTheWindowHasElapsed()
    {
        using var watch = new Watching();

        watch.Block("steam.exe");
        watch.Clock.Advance(BlockScreenRate.Window + TimeSpan.FromSeconds(1));
        watch.Block("steam.exe");

        Assert.Equal(2, watch.Shown.Count);
    }

    /// <summary>A limit expiring early would let an application restarting every few minutes through.</summary>
    [Fact]
    public void TheSameApplicationIsStillSilencedAMomentBeforeTheWindowIsUp()
    {
        using var watch = new Watching();

        watch.Block("steam.exe");
        watch.Clock.Advance(BlockScreenRate.Window - TimeSpan.FromSeconds(1));
        watch.Block("steam.exe");

        Assert.Single(watch.Shown);
    }

    /// <summary>One target whatever the service capitalised; Windows treats the names as one file.</summary>
    [Fact]
    public void TheSameApplicationSpeltDifferentlyIsStillTheSameTarget()
    {
        using var watch = new Watching();

        watch.Block("steam.exe");
        watch.Block("Steam.exe");

        Assert.Single(watch.Shown);
    }

    /// <summary>The limit remembers per target only; a hundred applications blocked once each are a hundred screens.</summary>
    [Fact]
    public void EveryTargetsFirstBlockIsShown()
    {
        using var watch = new Watching();

        for (var index = 0; index < 100; index++)
        {
            watch.Block($"app-{index}.exe");
        }

        Assert.Equal(100, watch.Shown.Count);
    }

    [Fact]
    public void AnEventWithNoNameShowsNothing()
    {
        using var watch = new Watching();

        watch.Link.PublishBlock(new AppBlock(string.Empty, Status()));

        Assert.Empty(watch.Shown);
    }

    private static StatusPayload Status() =>
        Say.Status("Active", Machine, endsAt: Machine + TimeSpan.FromMinutes(42));

    private sealed class Watching : IDisposable
    {
        public Watching()
        {
            Notices = new BlockNotices(Link, new BlockScreenRate(Clock), _shown.Add);
        }

        private readonly List<BlockEvent> _shown = [];

        public FakeClock Clock { get; } = new(Machine);

        public RecordingLink Link { get; } = new();

        public BlockNotices Notices { get; }

        public IReadOnlyList<BlockEvent> Shown => _shown;

        public void Block(string appName) => Link.PublishBlock(new AppBlock(appName, Status()));

        public void Dispose() => Notices.Dispose();
    }
}
