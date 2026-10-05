using Chronos.App.Services;
using Chronos.Ipc;

namespace Chronos.App.Tests;

public sealed class ServiceLinkTests : IAsyncLifetime
{
    private readonly ServiceHarness _service = new();
    private readonly RecordedWaits _waits = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _service.DisposeAsync().AsTask();

    private ServiceLink Link() => new(_service.PipeName, _waits.NoWaitAsync);

    [Fact]
    public async Task Connect_ReportsUnavailableWhenNothingIsListening()
    {
        await using var link = Link();

        link.Start();

        var snapshot = await LinkWait.ForAsync(
            link, static s => s.State == ServiceConnectionState.Unavailable, "reported the service missing");

        Assert.Equal(ServiceConnectionState.Unavailable, snapshot.State);
        Assert.Null(snapshot.Status);
    }

    [Fact]
    public async Task Link_SaysHowLongItWillWaitBeforeEachNewAttempt()
    {
        await using var link = Link();
        var said = new List<TimeSpan>();
        link.Retrying += (_, wait) =>
        {
            lock (said)
            {
                said.Add(wait);
            }
        };

        link.Start();

        await LinkWait.ForAsync(
            link, static s => s.State == ServiceConnectionState.Unavailable, "reported the service missing");
        SpinWait.SpinUntil(() => _waits.Asked.Count > 0, TimeSpan.FromSeconds(5));

        lock (said)
        {
            Assert.NotEmpty(said);
            Assert.Equal(_waits.Asked[0], said[0]);
        }
    }

    [Fact]
    public async Task Connect_ReportsAvailableAndCarriesTheFirstStatus()
    {
        _service.Start();

        await using var link = Link();

        link.Start();

        var snapshot = await LinkWait.ForAsync(
            link, static s => s.State == ServiceConnectionState.Available, "reached the service");

        Assert.NotNull(snapshot.Status);
        Assert.Equal("Idle", snapshot.Status.State);

        // The moment in the status is the service's own stopped clock.
        Assert.Equal(ServiceHarness.Moment, snapshot.Status.Now);
    }

    [Fact]
    public async Task Link_ReconnectsAfterTheServiceGoesAwayAndComesBack()
    {
        _service.Start();

        await using var link = Link();

        link.Start();

        await LinkWait.ForAsync(link, static s => s.State == ServiceConnectionState.Available, "reached the service");

        await _service.StopAsync();
        await LinkWait.ForAsync(
            link, static s => s.State == ServiceConnectionState.Unavailable, "noticed the service leaving");

        _service.Start();

        var recovered = await LinkWait.ForAsync(
            link, static s => s.State == ServiceConnectionState.Available, "reconnected on its own");

        Assert.NotNull(recovered.Status);
        Assert.Equal("Idle", recovered.Status.State);

        // Nobody restarted the link: it asked again by itself, after waiting.
        Assert.NotEmpty(_waits.Asked);
    }

    [Fact]
    public async Task Link_ReportsUnavailableWhileTheServiceIsDownRatherThanShowingStaleStatus()
    {
        _service.Start();

        await using var link = Link();

        var seen = new List<ServiceSnapshot>();
        var waitsBeforeTheNews = -1;
        link.Changed += (_, snapshot) =>
        {
            lock (seen)
            {
                seen.Add(snapshot);

                if (snapshot.State == ServiceConnectionState.Unavailable && waitsBeforeTheNews < 0)
                {
                    waitsBeforeTheNews = _waits.Asked.Count;
                }
            }
        };

        link.Start();

        var live = await LinkWait.ForAsync(
            link, static s => s.State == ServiceConnectionState.Available, "reached the service");
        Assert.NotNull(live.Status);

        await _service.StopAsync();

        var gone = await LinkWait.ForAsync(
            link, static s => s.State == ServiceConnectionState.Unavailable, "noticed the service leaving");

        // The status held a moment ago is gone with the service, not still on offer as if it described now.
        Assert.Null(gone.Status);
        Assert.Null(link.Snapshot.Status);

        lock (seen)
        {
            Assert.All(seen, snapshot => Assert.True(
                snapshot.State == ServiceConnectionState.Available || snapshot.Status is null,
                $"A {snapshot.State} snapshot carried a status."));

            // It said so on the broken connection itself, before waiting to retry; otherwise the finished session stays on screen for a connect timeout.
            Assert.Equal(0, waitsBeforeTheNews);
        }
    }

    [Fact]
    public async Task Command_ReachesTheServiceAndIsAnswered()
    {
        _service.Start();

        await using var link = Link();

        var response = await link.SendAsync(new IpcRequest { Command = "GetStatus" }, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Accepted);
        Assert.Equal("Idle", response.Status!.State);
    }

    [Fact]
    public async Task Command_SaysTheServiceIsGoneRatherThanThrowingAtTheCaller()
    {
        await using var link = Link();

        var response = await link.SendAsync(new IpcRequest { Command = "GetStatus" }, CancellationToken.None);

        // Not an exception: "the service is not there" is a mode the caller puts on screen.
        Assert.Null(response);

        // A command decides nothing about the connection state; the subscription owns that.
        Assert.Equal(ServiceConnectionState.Connecting, link.Snapshot.State);
        Assert.Null(link.Snapshot.Status);
    }

    [Fact]
    public async Task BeforeAnythingIsKnown_TheLinkSaysConnectingRatherThanUnavailable()
    {
        await using var link = Link();

        // Unknown and unavailable are different answers; only one says to check the service.
        Assert.Equal(ServiceConnectionState.Connecting, link.Snapshot.State);
        Assert.Null(link.Snapshot.Status);
    }

    [Fact]
    public async Task AnAppBlockedEvent_CarriesTheNameOfTheApplicationAndItsStatus()
    {
        _service.Start();

        await using var link = Link();

        var blocked = new TaskCompletionSource<BlockEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Blocked += (_, block) => blocked.TrySetResult(block);

        link.Start();

        // Subscribed before the event is published, or nobody would be listening.
        await LinkWait.ForAsync(link, static s => s.State == ServiceConnectionState.Available, "reached the service");

        var session = new StatusPayload(
            "Active",
            ServiceHarness.Moment,
            ServiceHarness.Moment - TimeSpan.FromMinutes(18),
            ServiceHarness.Moment + TimeSpan.FromMinutes(42),
            null,
            30,
            [],
            [],
            []);

        _service.Events.Publish(IpcEvent.AppBlocked(session, "steam.exe"));

        var settled = await Task.WhenAny(blocked.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(ReferenceEquals(settled, blocked.Task), "The block never reached the interface.");

        var arrived = Assert.IsType<AppBlock>(await blocked.Task);

        Assert.Equal("steam.exe", arrived.AppName);

        // The whole status comes with it, so the screen can say how long is left without asking.
        Assert.Equal("Active", arrived.Status.State);
        Assert.Equal(ServiceHarness.Moment + TimeSpan.FromMinutes(42), arrived.Status.EndsAt);
    }

    /// <summary>
    /// A status change is not a block. Three status changes go first and a block last; the stream is
    /// one ordered channel, so the block's arrival proves the earlier ones were read.
    /// </summary>
    [Fact]
    public async Task AStatusChangedEvent_IsNotReportedAsABlock()
    {
        _service.Start();

        await using var link = Link();

        var blocks = new List<BlockEvent>();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        link.Blocked += (_, block) =>
        {
            lock (blocks)
            {
                blocks.Add(block);
            }

            arrived.TrySetResult();
        };

        link.Start();
        await LinkWait.ForAsync(link, static s => s.State == ServiceConnectionState.Available, "reached the service");

        for (var minutes = 1; minutes <= 3; minutes++)
        {
            _service.Events.Publish(IpcEvent.StatusChanged(Running(minutes)));
        }

        _service.Events.Publish(IpcEvent.AppBlocked(Running(4), "steam.exe"));

        var settled = await Task.WhenAny(arrived.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(ReferenceEquals(settled, arrived.Task), "The block never reached the interface.");

        lock (blocks)
        {
            var only = Assert.IsType<AppBlock>(Assert.Single(blocks));
            Assert.Equal("steam.exe", only.AppName);
        }
    }

    /// <summary>A block's status is built on another thread and can be older than a StatusChanged that overtook it; the older one does not replace the newer.</summary>
    [Fact]
    public async Task ABlockCarryingAnOlderStatus_IsReportedButDoesNotTurnTheScreenBack()
    {
        _service.Start();

        await using var link = Link();

        var blocked = new TaskCompletionSource<BlockEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Blocked += (_, block) => blocked.TrySetResult(block);

        link.Start();
        await LinkWait.ForAsync(link, static s => s.State == ServiceConnectionState.Available, "reached the service");

        var later = ServiceHarness.Moment + TimeSpan.FromMinutes(5);
        var ended = new StatusPayload("Idle", later, null, null, null, 30, [], [], []);
        _service.Events.Publish(IpcEvent.StatusChanged(ended));
        await LinkWait.ForAsync(link, s => s.Status?.Now == later, "took the newer status");

        _service.Events.Publish(IpcEvent.AppBlocked(Running(4), "steam.exe"));

        var settled = await Task.WhenAny(blocked.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(ReferenceEquals(settled, blocked.Task), "The block never reached the interface.");

        Assert.Equal("Active", (await blocked.Task).Status.State);
        Assert.Equal("Idle", link.Snapshot.Status!.State);
        Assert.Equal(later, link.Snapshot.Status.Now);
    }

    /// <summary>Only an older status is refused. Two statuses in the same service clock tick are both news.</summary>
    [Fact]
    public async Task AStatusFromTheSameMomentAsTheHeldOne_IsAdopted()
    {
        _service.Start();

        await using var link = Link();

        link.Start();
        var first = await LinkWait.ForAsync(
            link, static s => s.State == ServiceConnectionState.Available, "reached the service");

        var running = Running(3);
        Assert.Equal(first.Status!.Now, running.Now);

        _service.Events.Publish(IpcEvent.StatusChanged(running));

        var adopted = await LinkWait.ForAsync(link, static s => s.Status?.State == "Active", "took the running session");
        Assert.Equal(running.EndsAt, adopted.Status!.EndsAt);
    }

    [Fact]
    public async Task ASiteBlockedEvent_CarriesTheDomainAndItsStatus()
    {
        _service.Start();

        await using var link = Link();

        var blocked = new TaskCompletionSource<BlockEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Blocked += (_, block) => blocked.TrySetResult(block);

        link.Start();
        await LinkWait.ForAsync(link, static s => s.State == ServiceConnectionState.Available, "reached the service");

        _service.Events.Publish(IpcEvent.SiteBlocked(Running(42), "reddit.com"));

        var settled = await Task.WhenAny(blocked.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(ReferenceEquals(settled, blocked.Task), "The refused site never reached the interface.");

        var arrived = Assert.IsType<SiteBlock>(await blocked.Task);

        Assert.Equal("reddit.com", arrived.Domain);
        Assert.Equal(ServiceHarness.Moment + TimeSpan.FromMinutes(42), arrived.Status.EndsAt);
    }

    /// <summary>Only a SiteBlocked event with a domain and a status is a refused site. The trailing application block proves the earlier ones were read.</summary>
    [Fact]
    public async Task AnEventThatIsNotAWholeSiteBlock_IsNotReportedAsOne()
    {
        _service.Start();

        await using var link = Link();

        var blocks = new List<BlockEvent>();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        link.Blocked += (_, block) =>
        {
            lock (blocks)
            {
                blocks.Add(block);
            }

            if (block is AppBlock)
            {
                arrived.TrySetResult();
            }
        };

        link.Start();
        await LinkWait.ForAsync(link, static s => s.State == ServiceConnectionState.Available, "reached the service");

        _service.Events.Publish(new IpcEvent(IpcEventKind.StatusChanged, ServiceHarness.Moment, Running(1), null, "reddit.com"));
        _service.Events.Publish(new IpcEvent(IpcEventKind.SiteBlocked, ServiceHarness.Moment, Running(2), null, string.Empty));
        _service.Events.Publish(new IpcEvent(IpcEventKind.SiteBlocked, ServiceHarness.Moment, null, null, "reddit.com"));
        _service.Events.Publish(IpcEvent.AppBlocked(Running(4), "steam.exe"));

        var settled = await Task.WhenAny(arrived.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(ReferenceEquals(settled, arrived.Task), "The block never reached the interface.");

        lock (blocks)
        {
            Assert.IsType<AppBlock>(Assert.Single(blocks));
        }
    }

    /// <summary>A running session distinguishable from the last; an identical status publishes nothing.</summary>
    private static StatusPayload Running(int minutes) =>
        new(
            "Active",
            ServiceHarness.Moment,
            ServiceHarness.Moment - TimeSpan.FromMinutes(minutes),
            ServiceHarness.Moment + TimeSpan.FromMinutes(minutes),
            null,
            30,
            [],
            [],
            []);

    [Fact]
    public void AnUnavailableSnapshotCannotBeGivenAStatus()
    {
        Assert.Null(ServiceSnapshot.Unavailable.Status);
        Assert.Null(ServiceSnapshot.Connecting.Status);
        Assert.Throws<ArgumentNullException>(() => _ = ServiceSnapshot.Available(null!));
    }
}
