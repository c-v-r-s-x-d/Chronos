using System.Threading.Channels;
using Chronos.Ipc;
using Chronos.Service.Dns;
using Chronos.Service.Ipc;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Tests;

/// <summary>
/// An attempt the resolver refused becomes a <see cref="IpcEventKind.SiteBlocked"/> event.
/// The announcer runs on the resolver's answer path, so every test also checks it never throws and never waits.
/// </summary>
public sealed class SiteBlockAnnouncerTests
{
    private const string Domain = "reddit.com";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly EventBus _bus = new();
    private readonly CapturingLogger<SiteBlockAnnouncer> _log = new();
    private readonly List<Action> _scheduled = [];
    private readonly StatusPayload _status = TestStatus.Blank();
    private int _statusReads;

    [Fact]
    public void ARefusedSiteIsPublishedWithItsDomainAndTheStatus()
    {
        using var _ = _bus.Subscribe(out var events);

        Inline().Announce(Domain);

        Assert.True(events.TryRead(out var published));
        Assert.Equal(IpcEventKind.SiteBlocked, published.Kind);
        Assert.Equal(Domain, published.Domain);
        Assert.Same(_status, published.Status);
    }

    [Fact]
    public void WithNobodyListeningNothingIsScheduledAndTheStatusIsNotBuilt()
    {
        Deferred().Announce(Domain);

        Assert.Empty(_scheduled);
        Assert.Equal(0, _statusReads);
    }

    [Fact]
    public void TheStatusIsBuiltOffTheAnswerPath()
    {
        using var _ = _bus.Subscribe(out var events);

        Deferred().Announce(Domain);

        // Building the status takes the session gate; the resolver's thread must not.
        Assert.Equal(0, _statusReads);
        Assert.False(events.TryRead(out var _));

        RunScheduled();

        Assert.Equal(1, _statusReads);
        Assert.True(events.TryRead(out var published));
        Assert.Equal(Domain, published.Domain);
    }

    [Fact]
    public async Task AStatusHeldUpBehindTheSessionGateDoesNotHoldUpTheAnswer()
    {
        using var _ = _bus.Subscribe(out var events);
        using var gate = new ManualResetEventSlim();
        var announcer = new SiteBlockAnnouncer(
            _bus,
            () =>
            {
                // Longer than the test waits for the answer, so an answer that waited cannot pass.
                gate.Wait(Patience * 3);
                return _status;
            },
            _log);

        var answered = Task.Run(() => announcer.Announce(Domain));

        var settled = await Task.WhenAny(answered, Task.Delay(Patience));
        Assert.True(ReferenceEquals(settled, answered), "The answer path waited for the status.");

        gate.Set();

        var published = await Next(events);
        Assert.Equal(Domain, published.Domain);
    }

    [Fact]
    public void AStatusThatThrowsIsSwallowedAndTheDomainIsWrittenOnlyAtDebug()
    {
        using var _ = _bus.Subscribe(out var events);
        var announcer = new SiteBlockAnnouncer(
            _bus, () => throw new ObjectDisposedException("dispatcher"), _log, work => work());

        announcer.Announce(Domain);

        Assert.False(events.TryRead(out var _));
        Assert.NotEmpty(_log.Entries);
        Assert.All(_log.Entries, entry => Assert.Equal(LogLevel.Debug, entry.Level));
    }

    [Fact]
    public void ASchedulerThatRefusesIsSwallowedAndWrittenOnlyAtDebug()
    {
        using var _ = _bus.Subscribe(out var events);
        var announcer = new SiteBlockAnnouncer(
            _bus, Status, _log, _ => throw new NotSupportedException("no threads today"));

        announcer.Announce(Domain);

        Assert.NotEmpty(_log.Entries);
        Assert.All(_log.Entries, entry => Assert.Equal(LogLevel.Debug, entry.Level));
    }

    [Fact]
    public void ARefusedScheduleGivesItsPlaceBack()
    {
        using var _ = _bus.Subscribe(out var events);
        var refuse = true;
        var announcer = new SiteBlockAnnouncer(
            _bus,
            Status,
            _log,
            work =>
            {
                if (refuse)
                {
                    throw new NotSupportedException("no threads today");
                }

                work();
            });

        for (var attempt = 0; attempt <= SiteBlockAnnouncer.MaxPending; attempt++)
        {
            announcer.Announce($"site-{attempt}.example");
        }

        refuse = false;
        announcer.Announce(Domain);

        Assert.True(events.TryRead(out var published));
        Assert.Equal(Domain, published.Domain);
    }

    [Fact]
    public void NoMoreThanMaxPendingAnnouncementsWaitAtOnce()
    {
        using var _ = _bus.Subscribe(out var events);
        var announcer = Deferred();

        for (var attempt = 0; attempt < SiteBlockAnnouncer.MaxPending + 5; attempt++)
        {
            announcer.Announce($"site-{attempt}.example");
        }

        Assert.Equal(SiteBlockAnnouncer.MaxPending, _scheduled.Count);
    }

    [Fact]
    public void AnAnnouncementThatHasRunGivesItsPlaceBack()
    {
        using var _ = _bus.Subscribe(out var events);
        var announcer = Deferred();

        for (var attempt = 0; attempt < SiteBlockAnnouncer.MaxPending; attempt++)
        {
            announcer.Announce($"site-{attempt}.example");
        }

        RunScheduled();
        announcer.Announce(Domain);

        Assert.Single(_scheduled);
    }

    [Fact]
    public void ADroppedAnnouncementTakesNoPlaceAndIsWrittenOnlyAtDebug()
    {
        using var _ = _bus.Subscribe(out var events);
        var announcer = Deferred();

        // More dropped than there are places: a drop that kept its place would leave none free.
        for (var attempt = 0; attempt <= 2 * SiteBlockAnnouncer.MaxPending; attempt++)
        {
            announcer.Announce($"site-{attempt}.example");
        }

        RunScheduled();
        announcer.Announce(Domain);

        Assert.Single(_scheduled);
        Assert.NotEmpty(_log.Entries);
        Assert.All(_log.Entries, entry => Assert.Equal(LogLevel.Debug, entry.Level));
    }

    [Fact]
    public void AnAnnouncementWhoseStatusThrewGivesItsPlaceBack()
    {
        using var _ = _bus.Subscribe(out var events);
        var announcer = new SiteBlockAnnouncer(
            _bus, () => throw new InvalidOperationException("gone"), _log, _scheduled.Add);

        for (var attempt = 0; attempt < SiteBlockAnnouncer.MaxPending; attempt++)
        {
            announcer.Announce($"site-{attempt}.example");
        }

        RunScheduled();
        announcer.Announce(Domain);

        Assert.Single(_scheduled);
    }

    [Fact]
    public void NothingIsWrittenAboveDebugWhenAllGoesWell()
    {
        using var _ = _bus.Subscribe(out var events);

        Inline().Announce(Domain);

        Assert.All(_log.Entries, entry => Assert.Equal(LogLevel.Debug, entry.Level));
    }

    private static async Task<IpcEvent> Next(ChannelReader<IpcEvent> events)
    {
        using var timeout = new CancellationTokenSource(Patience);

        return await events.ReadAsync(timeout.Token);
    }

    private StatusPayload Status()
    {
        _statusReads++;
        return _status;
    }

    private SiteBlockAnnouncer Inline() => new(_bus, Status, _log, work => work());

    private SiteBlockAnnouncer Deferred() => new(_bus, Status, _log, _scheduled.Add);

    private void RunScheduled()
    {
        var work = _scheduled.ToArray();
        _scheduled.Clear();

        foreach (var one in work)
        {
            one();
        }
    }
}
