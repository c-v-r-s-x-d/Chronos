using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Chronos.Core.Enforcement;
using Chronos.Core.Rules;
using Chronos.Service.Configuration;
using Chronos.Service.Dns;
using Chronos.Service.Sites;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

/// <summary>Layer L2: availability, the takeover of the interfaces, and giving them back. Nothing here touches the machine; port 53 is never bound.</summary>
public sealed class DnsEnforcerTests : IAsyncLifetime
{
    private const string Blocked = "example.com";
    private const string Allowed = "example.org";

    private const byte RcodeNameError = 3;

    private static readonly Guid SessionId = Guid.Parse("5f0e9d8c-7b6a-4958-8372-6150a4b3c2d1");
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _events = [];
    private readonly ServiceTestClock _clock = new(Now);
    private readonly CapturingLogger<DnsEnforcer> _log = new();
    private readonly CapturingLogger<LoopbackDnsServer> _serverLog = new();
    private readonly List<LoopbackDnsServer> _servers = [];
    private readonly List<int> _portsAsked = [];
    private readonly List<IReadOnlyList<IPAddress>> _forwarders = [];
    private readonly RecordingForwarder _forwarder = new();
    private readonly FakeInterfaceDns _machine = new();
    private readonly MemoryMirror _mirror;
    private readonly FakeDnsControl _control;
    private readonly RecordingDnsCache _cache;
    private readonly ChronosPaths _paths;
    private readonly DnsBackupStore _store;
    private readonly SwappableDnsForwarder _upstream = new();
    private readonly DnsRequestHandler _handler;
    private readonly List<DnsEnforcer> _made = [];
    private readonly FakeDnsSettingsLock _lock = new();
    private readonly FakeSelfCheck _check;
    private readonly List<Socket> _squatters = [];
    private bool _squatOnCreate;

    public DnsEnforcerTests()
    {
        _mirror = new MemoryMirror(_events);
        _control = new FakeDnsControl(_machine, _events);
        _cache = new RecordingDnsCache(_events);
        _check = new FakeSelfCheck(_events);
        _paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        _paths.EnsureDataDirectoryExists();
        _store = new DnsBackupStore(_paths, _mirror, new CapturingLogger<DnsBackupStore>());
        _handler = new DnsRequestHandler(
            _upstream, new DnsResponseCache(_clock), _ => { }, NullLogger<DnsRequestHandler>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var enforcer in _made)
        {
            await enforcer.DisposeAsync();
        }

        foreach (var server in _servers)
        {
            await server.StopAsync();
        }

        foreach (var squatter in _squatters)
        {
            squatter.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TheReasonsAreCodesAndTheNameIsDns()
    {
        Assert.Equal("dns", DnsEnforcer.LayerName);
        Assert.Equal(Chronos.Ipc.LayerNames.Dns, DnsEnforcer.LayerName);
        Assert.Equal("dns", Make().Name);
        Assert.Equal("dns.port-busy", DnsEnforcer.PortBusy);
        Assert.Equal("dns.no-upstream", DnsEnforcer.NoUpstream);
        Assert.Equal("dns.settings-refused", DnsEnforcer.SettingsRefused);
        Assert.Equal("dns.backup-one-place", DnsEnforcer.BackupOnePlace);
        Assert.Equal("dns.loopback-blocked", DnsEnforcer.LoopbackBlocked);
    }

    [Fact]
    public void ThePortAskedForInServiceIs53()
    {
        Assert.Equal(53, DnsEnforcer.DefaultPort);
    }

    [Fact]
    public async Task Probe_IsAvailableWhenThePortCanBeTakenAndLeavesItFree()
    {
        var port = FreePort();

        var availability = await Make(port).ProbeAsync(default);

        Assert.True(availability.IsAvailable);

        // Taken to find out, and let go again: an idle machine holds nothing.
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Loopback, port));
        var tcp = new TcpListener(IPAddress.Loopback, port);
        tcp.Start();
        tcp.Stop();
        Assert.Empty(_servers);
    }

    [Fact]
    public async Task Probe_ReportsPortBusyAndNamesTheHolderWhenTheUdpHalfIsTaken()
    {
        using var holder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        holder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)holder.LocalEndPoint!).Port;

        var availability = await Make(port).ProbeAsync(default);

        Assert.False(availability.IsAvailable);
        Assert.Equal(DnsEnforcer.PortBusy, availability.Reason);

        // The name and the PID, at Information.
        Assert.Contains(
            _log.Entries,
            entry => entry.Level == LogLevel.Information
                && entry.Message.Contains(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task Probe_ReportsPortBusyWhenOnlyTheTcpHalfIsTaken()
    {
        var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();

        try
        {
            var port = ((IPEndPoint)holder.LocalEndpoint).Port;

            var availability = await Make(port).ProbeAsync(default);

            Assert.Equal(DnsEnforcer.PortBusy, availability.Reason);
        }
        finally
        {
            holder.Stop();
        }
    }

    [Fact]
    public async Task Probe_NamesTheSameHolderAtInformationOnceRatherThanEveryPass()
    {
        using var holder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        holder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var enforcer = Make(((IPEndPoint)holder.LocalEndPoint!).Port);

        await enforcer.ProbeAsync(default);
        await enforcer.ProbeAsync(default);

        Assert.Single(_log.Entries, entry => entry.Level == LogLevel.Information);
    }

    [Fact]
    public async Task Probe_NamesTheHolderAgainAfterThePortWasFreeInBetween()
    {
        var holder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        holder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)holder.LocalEndPoint!).Port;
        var enforcer = Make(port);

        await enforcer.ProbeAsync(default);
        holder.Dispose();
        Assert.True((await enforcer.ProbeAsync(default)).IsAvailable);

        using var again = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        again.Bind(new IPEndPoint(IPAddress.Loopback, port));
        await enforcer.ProbeAsync(default);

        Assert.Equal(2, _log.Entries.Count(entry => entry.Level == LogLevel.Information));
    }

    [Fact]
    public async Task Probe_IsAvailableWhileItsOwnServerHoldsThePort()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make(FreePort());
        await enforcer.ReconcileAsync(Plan(Blocked), default);

        var availability = await enforcer.ProbeAsync(default);

        Assert.True(availability.IsAvailable);
    }

    [Fact]
    public void ThePortProbeRefusesAPortOutsideTheRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LoopbackDnsServer.CanTake(-1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => LoopbackDnsServer.CanTake(65536, out _));
    }

    [Fact]
    public async Task Probe_CreatesNoServer()
    {
        await Make(FreePort()).ProbeAsync(default);

        Assert.Empty(_servers);
    }

    [Fact]
    public async Task AnEmptyPlanOnAnIdleMachineTakesNothing()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");

        var result = await Make().ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Equal(ReconcileOutcome.Unchanged, result.Outcome);
        Assert.Empty(_servers);
        Assert.Empty(_control.Calls);
        Assert.Equal(0, _mirror.Writes);
        Assert.Equal(0, _cache.Flushes);
    }

    [Fact]
    public async Task AnEmptyPlanAfterASessionReleasesTheLayerAsClearDoes()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Calls.Clear();

        var result = await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);

        // One rule and one interface.
        Assert.Equal((0, 2), (result.Applied, result.Removed));
        Assert.Equal([(7, (IReadOnlyList<string>?)null)], _control.Calls);
        Assert.False(Assert.Single(_servers).IsRunning);
        Assert.Null(_store.Load());
        Assert.Null(_upstream.Current);
        Assert.NotEqual(RcodeNameError, Rcode(await _handler.AnswerAsync(Query(Blocked), DnsTransportKind.Udp, default)));
    }

    [Fact]
    public async Task AnEmptyPlanAtStartPutsBackACopyARunThatDidNotFinishLeftBehind()
    {
        _machine.Add("{a}", 7, isDhcp: false, "127.0.0.1", "192.168.1.1");
        _store.Save(Backup(Dhcp("{a}", 7, "192.168.1.1")));

        var result = await Make().ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.Equal([(7, (IReadOnlyList<string>?)null)], _control.Calls);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task ASecondEmptyPlanAfterTheReleaseTouchesNothing()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);
        _control.Calls.Clear();
        var flushes = _cache.Flushes;

        var result = await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Equal(ReconcileOutcome.Unchanged, result.Outcome);
        Assert.Empty(_control.Calls);
        Assert.Equal(flushes, _cache.Flushes);
    }

    [Fact]
    public async Task AnIdleLayerLooksForALeftoverCopyOnceAndNotOnEveryPass()
    {
        // A copy appearing behind the running service's back is not something an idle pass goes
        // looking for every fifteen seconds; the one at start is.
        var enforcer = Make();
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);
        _machine.Add("{a}", 7, isDhcp: false, "127.0.0.1", "192.168.1.1");
        _store.Save(Backup(Dhcp("{a}", 7, "192.168.1.1")));

        var result = await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Equal(ReconcileOutcome.Unchanged, result.Outcome);
        Assert.Empty(_control.Calls);
    }

    [Fact]
    public async Task AnEmptyPlanWhoseReleaseIsRefusedReportsSettingsRefusedAndKeepsTheCopy()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Refuse.Add(7);

        var result = await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.Equal(DnsEnforcer.SettingsRefused, result.Detail);
        Assert.NotNull(_store.Load());
    }

    [Fact]
    public async Task ARefusedReleaseIsRetriedOnIdlePassesAndReportedUntilItGoesThrough()
    {
        // The interface is on 127.0.0.1 with nothing listening; the failure stays in status.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Refuse.Add(7);
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);
        _control.Calls.Clear();

        var again = await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Equal(ReconcileOutcome.Failed, again.Outcome);
        Assert.Equal(DnsEnforcer.SettingsRefused, again.Detail);
        Assert.Equal([7], _control.Calls.Select(call => call.Index));

        _control.Refuse.Clear();
        var through = await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Equal(ReconcileOutcome.Changed, through.Outcome);
        Assert.Equal((0, 1), (through.Applied, through.Removed));
        Assert.True(_machine.Of(7).IsDhcp);
        Assert.Null(_store.Load());
        Assert.Equal(3, _cache.Flushes);

        _control.Calls.Clear();
        var after = await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Equal(ReconcileOutcome.Unchanged, after.Outcome);
        Assert.Empty(_control.Calls);
    }

    [Fact]
    public async Task AnIdleRetryHoldsTheSettingsWhileItWrites()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Refuse.Add(7);
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);
        var held = new List<bool>();
        _control.Before = _ => held.Add(_lock.Held);

        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Equal([true], held);
        Assert.False(_lock.Held);
    }

    [Fact]
    public async Task AnIdleRetryWhileTheCommandLineHoldsTheSettingsWritesNothingAndStillReportsTheRefusal()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Refuse.Add(7);
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        // A retry that was refused too: the failure is what is still said while the lock is taken.
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);
        _control.Calls.Clear();
        _lock.HeldElsewhere = true;

        var result = await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.Equal(DnsEnforcer.SettingsRefused, result.Detail);
        Assert.Empty(_control.Calls);
        Assert.Equal([TimeSpan.Zero, TimeSpan.Zero], _lock.Waits.Skip(1));
    }

    [Fact]
    public async Task ARefusedReleaseIsReportedWhileTheCommandLineHoldsTheSettingsOnTheVeryNextPass()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Refuse.Add(7);
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);
        _lock.HeldElsewhere = true;

        var result = await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.Equal(DnsEnforcer.SettingsRefused, result.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAbsentAdapterAloneIsNoFailureWhileTheCommandLineHoldsTheSettings(bool refusedFirst)
    {
        // Gone is not refused, also once a refusal beside it went through on a retry.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _machine.Remove("{a}");
        _control.Refuse.Add(7);
        if (refusedFirst)
        {
            _control.Refuse.Add(9);
        }

        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);
        _control.Refuse.Remove(9);
        if (refusedFirst)
        {
            Assert.Equal(ReconcileOutcome.Changed, (await enforcer.ReconcileAsync(EnforcementPlan.Empty, default)).Outcome);
        }

        _lock.HeldElsewhere = true;

        var result = await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Equal(ReconcileOutcome.Unchanged, result.Outcome);
    }

    [Fact]
    public async Task AnIdlePassWithOnlyAnAbsentAdapterLeftWritesNothingAndSaysNothing()
    {
        // Waiting for an adapter to come back costs one enumeration.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _machine.Remove("{a}");
        _control.Refuse.Add(7);
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);
        _control.Calls.Clear();
        var logged = _log.Entries.Count;
        var writes = _mirror.Writes;

        var result = await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Equal(ReconcileOutcome.Unchanged, result.Outcome);
        Assert.Empty(_control.Calls);
        Assert.Equal(writes, _mirror.Writes);
        Assert.DoesNotContain(_log.Entries.Skip(logged), entry => entry.Level >= LogLevel.Information);
        Assert.NotNull(_store.Load());
    }

    [Fact]
    public async Task AnAdapterThatComesBackAfterTheSessionIsPutBackOnTheNextIdlePass()
    {
        // Otherwise it returns on 127.0.0.1 with nothing listening and no copy anywhere.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        var gone = _machine.Of(7);
        _machine.Remove("{a}");
        _control.Refuse.Add(7);
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);
        _control.Refuse.Clear();
        _machine.Add(gone.Guid, gone.Index, gone.IsDhcp, [.. gone.Servers]);
        _control.Calls.Clear();

        var result = await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.Equal([(7, (IReadOnlyList<string>?)null)], _control.Calls);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task ASessionAfterAnUnfinishedReleaseWhoseCopyIsGoneSavesItAgainFromMemory()
    {
        // The interface still reads 127.0.0.1 first; without the copy in memory it would be left
        // alone for good, with nothing to restore it from.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Refuse.Add(7);
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);
        _store.Clear();
        _control.Refuse.Clear();

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        var copy = Assert.Single(_store.Load()!.Interfaces);
        Assert.True(copy.IsDhcp);
        Assert.Equal(["192.168.1.1"], copy.Servers);
    }

    [Fact]
    public async Task AnAbsentAdapterStaysInTheCopyThroughTheNextSession()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _machine.Remove("{a}");
        _control.Refuse.Add(7);
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        await enforcer.ReconcileAsync(Plan(Blocked), default);
        await enforcer.ClearAsync(default);

        var kept = Assert.Single(_store.Load()!.Interfaces);
        Assert.Equal("{a}", kept.Guid);
        Assert.True(kept.IsDhcp);
    }

    [Fact]
    public async Task ASessionAfterARestoreReadsAgainAnInterfaceTheUserChangedWhileAnotherStaysAbsent()
    {
        // The old original of {b} must not come back over the user's own setting.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _machine.Remove("{a}");
        _control.Refuse.Add(7);
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);
        _machine.SetStatic(9, ["1.1.1.1"]);
        _control.Calls.Clear();

        await enforcer.ReconcileAsync(Plan(Blocked), default);
        await enforcer.ClearAsync(default);

        Assert.Equal(
            ["127.0.0.1,1.1.1.1", "1.1.1.1"],
            _control.Calls.Where(call => call.Index == 9).Select(call => string.Join(',', call.Servers ?? ["dhcp"])));
        Assert.Equal(["1.1.1.1"], _machine.Of(9).Servers);
    }

    [Fact]
    public async Task AClearAfterAnUnfinishedReleasePutsBackOnlyWhatWasLeft()
    {
        // A stored copy wider than the leftover, e.g. a prune that did not reach the file.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Refuse.Add(7);
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);
        _store.Save(Backup(Dhcp("{a}", 7, "192.168.1.1"), Static("{b}", 9, "9.9.9.9")));
        _control.Refuse.Clear();
        _control.Calls.Clear();

        await enforcer.ClearAsync(default);

        Assert.Equal([7], _control.Calls.Select(call => call.Index));
    }

    [Fact]
    public async Task ASecondSessionAfterAReleaseTakesEverythingAgain()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);
        _control.Calls.Clear();

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.Equal(["127.0.0.1", "192.168.1.1"], Assert.Single(_control.Calls).Servers);
        Assert.NotNull(_store.Load());
        Assert.NotNull(_upstream.Current);
        Assert.True(_servers.Last().IsRunning);
        Assert.Equal(RcodeNameError, Rcode(await _handler.AnswerAsync(Query(Blocked), DnsTransportKind.Udp, default)));
    }

    [Fact]
    public async Task AnInterfaceLeftAloneIsReportedAgainInTheNextSession()
    {
        _machine.Add("{a}", 7, isDhcp: true);
        _machine.Add("{b}", 9, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(2, _log.Entries.Count(entry => entry.Level == LogLevel.Information && entry.Message.Contains("Adapter 7")));
    }

    [Fact]
    public async Task EachInterfaceGets127FirstAndItsOwnFirstServerSecond()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1", "192.168.1.2");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9", "1.1.1.1");

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(2, _control.Calls.Count);
        Assert.Equal(7, _control.Calls[0].Index);
        Assert.Equal(["127.0.0.1", "192.168.1.1"], _control.Calls[0].Servers);
        Assert.Equal(9, _control.Calls[1].Index);
        Assert.Equal(["127.0.0.1", "9.9.9.9"], _control.Calls[1].Servers);
    }

    [Fact]
    public async Task TheServerStartsAndHearsItselfThenTheCopyIsSavedThenTheInterfacesMoveThenTheCacheIsFlushed()
    {
        // The check comes before the copy: a layer that cannot hear itself leaves nothing behind.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var runningAtNetsh = false;
        _control.Before = _ => runningAtNetsh = _servers.Single().IsRunning;

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(["server", "check", "backup", "netsh:7", "flush"], _events);
        Assert.True(runningAtNetsh);
    }

    [Fact]
    public async Task TheCopyHoldsTheSettingsAsTheyWereBeforeTheTakeover()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1", "192.168.1.2");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9", "1.1.1.1");

        await Make().ReconcileAsync(Plan(Blocked), default);

        var saved = _store.Load()!;
        Assert.Equal(Now, saved.SavedAt);
        Assert.Collection(
            saved.Interfaces,
            one =>
            {
                Assert.Equal(("{a}", 7, true), (one.Guid, one.Index, one.IsDhcp));
                Assert.Equal(["192.168.1.1", "192.168.1.2"], one.Servers);
            },
            two =>
            {
                Assert.Equal(("{b}", 9, false), (two.Guid, two.Index, two.IsDhcp));
                Assert.Equal(["9.9.9.9", "1.1.1.1"], two.Servers);
            });
    }

    [Fact]
    public async Task TheServerIsAskedForThePortTheLayerWasGiven()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var port = FreePort();

        await Make(port).ReconcileAsync(Plan(Blocked), default);

        Assert.Equal([port], _portsAsked);
        Assert.Equal(port, _servers.Single().Port);
    }

    [Fact]
    public async Task TheRunningServerAnswersTheBlockedNameWithNxdomain()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");

        await Make().ReconcileAsync(Plan(Blocked), default);

        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Connect(IPAddress.Loopback, _servers.Single().Port);
        await client.SendAsync(Query(Blocked));
        var reply = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RcodeNameError, Rcode(reply.Buffer));
    }

    [Fact]
    public async Task ATakeoverIsReportedAsAChange()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");

        var result = await Make().ReconcileAsync(Plan(Blocked, Allowed), default);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.Null(result.Detail);

        // One interface and two rules.
        Assert.Equal(3, result.Applied);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public async Task NoDomainIsWrittenAboveDebug()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();

        await enforcer.ReconcileAsync(Plan(Blocked), default);
        await enforcer.ReconcileAsync(Plan(Blocked, Allowed), default);
        await enforcer.ClearAsync(default);

        Assert.DoesNotContain(
            _log.Entries,
            entry => entry.Level > LogLevel.Debug && (entry.Message.Contains(Blocked) || entry.Message.Contains(Allowed)));
    }

    [Fact]
    public async Task ASecondPassWithTheSamePlanTouchesNothingAndSaysNothingAtInformation()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        var calls = _control.Calls.Count;
        var writes = _mirror.Writes;
        var flushes = _cache.Flushes;
        _log.Clear();

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Unchanged, result.Outcome);
        Assert.Equal(calls, _control.Calls.Count);
        Assert.Equal(writes, _mirror.Writes);
        Assert.Equal(flushes, _cache.Flushes);
        Assert.Single(_servers);
        Assert.DoesNotContain(_log.Entries, entry => entry.Level >= LogLevel.Information);

        // Asked again on this pass too, and a check that is heard says nothing.
        Assert.Equal(2, _check.Ports.Count);
    }

    [Fact]
    public async Task TheSamePlanWithItsRulesInAnotherOrderIsTheSamePlan()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked, Allowed), default);

        var result = await enforcer.ReconcileAsync(Plan(Allowed, Blocked), default);

        Assert.Equal(ReconcileOutcome.Unchanged, result.Outcome);
    }

    [Fact]
    public async Task AddingADomainChangesTheRulesAndRunsNoNetsh()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        var calls = _control.Calls.Count;
        var writes = _mirror.Writes;

        var result = await enforcer.ReconcileAsync(Plan(Blocked, Allowed), default);

        Assert.Equal(calls, _control.Calls.Count);
        Assert.Equal(writes, _mirror.Writes);
        Assert.Equal((ReconcileOutcome.Changed, 1, 0), (result.Outcome, result.Applied, result.Removed));
        Assert.Equal(RcodeNameError, Rcode(await _handler.AnswerAsync(Query(Allowed), DnsTransportKind.Udp, default)));
    }

    [Fact]
    public async Task RemovingADomainLetsItThroughAgain()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked, Allowed), default);

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal((ReconcileOutcome.Changed, 0, 1), (result.Outcome, result.Applied, result.Removed));
        Assert.NotEqual(RcodeNameError, Rcode(await _handler.AnswerAsync(Query(Allowed), DnsTransportKind.Udp, default)));
    }

    [Fact]
    public async Task ARuleWhoseSubdomainFlagChangedIsAChangedRule()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);

        var result = await enforcer.ReconcileAsync(
            new EnforcementPlan(SessionId, null, [new SiteRule(Blocked, includeSubdomains: false)], []), default);

        Assert.Equal((ReconcileOutcome.Changed, 1, 1), (result.Outcome, result.Applied, result.Removed));
    }

    [Fact]
    public async Task AnInterfaceWithNoServerIsLeftAlone()
    {
        _machine.Add("{a}", 7, isDhcp: true);
        _machine.Add("{b}", 9, isDhcp: true, "192.168.1.1");

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal([9], _control.Calls.Select(call => call.Index));
        Assert.Equal(["{b}"], _store.Load()!.Interfaces.Select(state => state.Guid));
    }

    [Fact]
    public async Task AnInterfaceLeftAloneIsReportedOnceRatherThanEveryPass()
    {
        _machine.Add("{a}", 7, isDhcp: true);
        _machine.Add("{b}", 9, isDhcp: true, "192.168.1.1");
        var enforcer = Make();

        await enforcer.ReconcileAsync(Plan(Blocked), default);
        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Single(_log.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("Adapter 7"));
    }

    [Fact]
    public async Task WithNoInterfaceToTakeTheLayerReportsNoUpstreamAndDoesNotComeUp()
    {
        _machine.Add("{a}", 7, isDhcp: true);

        var result = await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Skipped, result.Outcome);
        Assert.Equal(DnsEnforcer.NoUpstream, result.Detail);
        Assert.Empty(_servers);
        Assert.Empty(_control.Calls);
        Assert.Equal(0, _mirror.Writes);
        Assert.Empty(_check.Ports);
    }

    [Fact]
    public async Task AnUnspecifiedAddressIsNeitherPutSecondNorForwardedTo()
    {
        _machine.Add("{a}", 7, isDhcp: false, "0.0.0.0", "8.8.8.8");

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(["127.0.0.1", "8.8.8.8"], Assert.Single(_control.Calls).Servers);
        Assert.Equal([IPAddress.Parse("8.8.8.8")], Assert.Single(_forwarders));
    }

    [Fact]
    public async Task AnIPv6ServerIsNotPutSecond()
    {
        // netsh's ipv4 context takes IPv4 addresses only.
        _machine.Add("{a}", 7, isDhcp: false, "2001:db8::1", "8.8.8.8");

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(["127.0.0.1", "8.8.8.8"], Assert.Single(_control.Calls).Servers);
        Assert.Equal([IPAddress.Parse("8.8.8.8")], Assert.Single(_forwarders));
    }

    [Fact]
    public async Task AnInterfaceWithOnlyAnUnspecifiedAddressHasNothingToPutSecond()
    {
        _machine.Add("{a}", 7, isDhcp: false, "0.0.0.0");

        var result = await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(DnsEnforcer.NoUpstream, result.Detail);
    }

    [Fact]
    public async Task AnInterfaceAlreadyOn127WithNoCopyIsLeftAloneWithAWarning()
    {
        // A half-applied run with its copy lost: re-reading this would save 127.0.0.1 as the
        // original, and the restore would put it back for good.
        _machine.Add("{a}", 7, isDhcp: false, "127.0.0.1", "192.168.1.1");
        var enforcer = Make();

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);
        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(DnsEnforcer.NoUpstream, result.Detail);
        Assert.Empty(_control.Calls);
        Assert.Equal(0, _mirror.Writes);
        Assert.Single(_log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Adapter 7"));
    }

    [Fact]
    public async Task AnInterfaceWithAnyLoopbackServerAndNoCopyIsLeftAlone()
    {
        _machine.Add("{a}", 7, isDhcp: false, "192.168.1.1", "127.0.0.1");
        _machine.Add("{b}", 9, isDhcp: true, "192.168.1.1");

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal([9], _control.Calls.Select(call => call.Index));
    }

    [Fact]
    public async Task ACopyFromAnEarlierRunIsTheOneThatCounts()
    {
        // The service died mid-session: the interface is still taken, the copy is on disk.
        _store.Save(Backup(Dhcp("{a}", 7, "192.168.1.1")));
        _machine.Add("{a}", 7, isDhcp: false, "127.0.0.1", "192.168.1.1");
        var writes = _mirror.Writes;
        var enforcer = Make();

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Empty(_control.Calls);
        Assert.Equal(writes, _mirror.Writes);
        Assert.True(_servers.Single().IsRunning);
        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);

        await enforcer.ClearAsync(default);

        Assert.Equal([(7, (IReadOnlyList<string>?)null)], _control.Calls);
    }

    [Fact]
    public async Task AnInterfaceInTheCopyThatDriftedIsTakenAgainWithTheSavedServerAndTheCopyIsLeftAsItWas()
    {
        _store.Save(Backup(Dhcp("{a}", 7, "192.168.1.1")));
        // Drifted to a list typed by hand; one DHCP hands it is handled by the DHCP rule instead.
        _machine.Add("{a}", 7, isDhcp: false, "10.0.0.1");
        var writes = _mirror.Writes;

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(["127.0.0.1", "192.168.1.1"], Assert.Single(_control.Calls).Servers);
        Assert.Equal(writes, _mirror.Writes);
        Assert.Equal(["192.168.1.1"], _store.Load()!.Interfaces.Single().Servers);
    }

    [Fact]
    public async Task AnInterfaceInTheCopyIsWrittenThroughTheIndexItHasNow()
    {
        // An adapter reinstalled since the copy was taken comes back under another index.
        _store.Save(Backup(Dhcp("{a}", 7, "192.168.1.1")));
        _machine.Add("{a}", 12, isDhcp: true, "192.168.1.1");

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(12, Assert.Single(_control.Calls).Index);
    }

    [Fact]
    public async Task ClearPutsBackAnAdapterThroughTheIndexItHasNowNotTheSavedOne()
    {
        _store.Save(Backup(Dhcp("{a}", 7, "192.168.1.1")));
        _machine.Add("{a}", 12, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Calls.Clear();

        await enforcer.ClearAsync(default);

        Assert.Equal([(12, (IReadOnlyList<string>?)null)], _control.Calls);
        Assert.True(_machine.Of(12).IsDhcp);
    }

    [Fact]
    public async Task AnAdapterGoneFromTheMachineDoesNotMakeClearFailAndStaysInTheCopy()
    {
        // A vanished adapter never makes clear throw.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _machine.Remove("{a}");
        _machine.Add("{c}", 7, isDhcp: true, "10.0.0.1");
        _control.Calls.Clear();

        await enforcer.ClearAsync(default);

        Assert.Equal([9], _control.Calls.Select(call => call.Index));
        Assert.Equal(["{a}"], _store.Load()!.Interfaces.Select(state => state.Guid));
    }

    [Fact]
    public async Task ANewInterfaceBesideACopyIsAddedToItAndTheRestOfItIsKept()
    {
        _store.Save(Backup(Dhcp("{a}", 7, "192.168.1.1")));
        _machine.Add("{a}", 7, isDhcp: false, "127.0.0.1", "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        _clock.Advance(TimeSpan.FromMinutes(1));

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal([9], _control.Calls.Select(call => call.Index));
        var saved = _store.Load()!;
        Assert.Equal(["{a}", "{b}"], saved.Interfaces.Select(state => state.Guid));
        Assert.True(saved.Interfaces[0].IsDhcp);
        Assert.Equal(["192.168.1.1"], saved.Interfaces[0].Servers);

        // Fresher than the copy it extends, so Load prefers it wherever only one place took it.
        Assert.Equal(Now.AddMinutes(1), saved.SavedAt);
    }

    [Fact]
    public async Task AnInterfaceThatAppearsMidSessionIsAddedToTheCopy()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);

        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(["{a}", "{b}"], _store.Load()!.Interfaces.Select(state => state.Guid));
    }

    [Fact]
    public async Task TheCopyIsMatchedToTheInterfaceWhateverTheCapitalsOfItsGuid()
    {
        _store.Save(Backup(Dhcp("{ABC}", 7, "192.168.1.1")));
        _machine.Add("{abc}", 7, isDhcp: false, "127.0.0.1", "192.168.1.1");
        var writes = _mirror.Writes;

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Empty(_control.Calls);
        Assert.Equal(writes, _mirror.Writes);
    }

    [Fact]
    public async Task ACopyNamingOneInterfaceTwiceIsReadAsItsFirstEntry()
    {
        _store.Save(Backup(Dhcp("{a}", 7, "192.168.1.1"), Static("{a}", 7, "9.9.9.9")));
        _machine.Add("{a}", 7, isDhcp: false, "10.0.0.1");

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(["127.0.0.1", "192.168.1.1"], Assert.Single(_control.Calls).Servers);
    }

    [Fact]
    public async Task AnInterfaceWhoseCopyHasNoOutsideServerIsNotMoved()
    {
        _store.Save(Backup(Static("{a}", 7, "127.0.0.1"), Dhcp("{b}", 9, "192.168.1.1")));
        _machine.Add("{a}", 7, isDhcp: true, "10.0.0.1");
        _machine.Add("{b}", 9, isDhcp: true, "192.168.1.1");

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal([9], _control.Calls.Select(call => call.Index));
    }

    [Fact]
    public async Task ADhcpInterfaceFollowsTheServerDhcpHandsItAfterANetworkChange()
    {
        // The router of the last network is not reachable from this one.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Calls.Clear();
        var writes = _mirror.Writes;
        _machine.Hand("{a}", "10.0.0.1", "10.0.0.2");

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        var call = Assert.Single(_control.Calls);
        Assert.Equal(7, call.Index);
        Assert.Equal(["127.0.0.1", "10.0.0.1"], call.Servers);
        Assert.Equal([IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2")], _forwarders.Last());

        // The copy keeps the original mode; restore still hands the interface back to DHCP.
        Assert.Equal(writes, _mirror.Writes);
        Assert.Equal(["192.168.1.1"], _store.Load()!.Interfaces.Single().Servers);
    }

    [Fact]
    public async Task ARePointedDhcpInterfaceIsHandedBackToDhcpAtTheEnd()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _machine.Hand("{a}", "10.0.0.1");
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Calls.Clear();

        await enforcer.ClearAsync(default);

        Assert.Equal([(7, (IReadOnlyList<string>?)null)], _control.Calls);
        Assert.True(_machine.Of(7).IsDhcp);
    }

    [Fact]
    public async Task ADhcpInterfaceWhoseServerDidNotChangeIsNotTouched()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1", "192.168.1.2");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Calls.Clear();
        _machine.Hand("{a}", "192.168.1.1", "10.0.0.9");

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);

        // Only the first outside server is kept second; the rest is not ours to judge.
        Assert.Equal(ReconcileOutcome.Unchanged, result.Outcome);
        Assert.Empty(_control.Calls);
    }

    [Fact]
    public async Task ADhcpInterfaceHandedNoOutsideServerKeepsTheSavedOne()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Calls.Clear();
        _machine.Hand("{a}", "127.0.0.1");

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Unchanged, result.Outcome);
        Assert.Empty(_control.Calls);
        Assert.Equal([IPAddress.Parse("192.168.1.1")], _forwarders.Last());
    }

    [Fact]
    public async Task AStaticInterfaceDoesNotFollowItsDhcpValue()
    {
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Calls.Clear();
        _machine.Hand("{b}", "10.0.0.1");

        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Empty(_control.Calls);
    }

    [Fact]
    public async Task ACopyFromAnEarlierRunFollowsTheCurrentDhcpServerWithoutNetsh()
    {
        // A restart after a re-point: the interface already holds what DHCP hands it now.
        _store.Save(Backup(Dhcp("{a}", 7, "192.168.1.1")));
        _machine.Add("{a}", 7, isDhcp: false, "127.0.0.1", "10.0.0.1");
        _machine.Hand("{a}", "10.0.0.1");

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Empty(_control.Calls);
        Assert.Equal([IPAddress.Parse("10.0.0.1")], _forwarders.Single());
    }

    [Fact]
    public async Task TheRePointIsWrittenUnderTheSettingsLock()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _machine.Hand("{a}", "10.0.0.1");
        var held = new List<bool>();
        _control.Before = _ => held.Add(_lock.Held);

        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal([true], held);
    }

    [Fact]
    public async Task AListStartingWith127AndTheSavedServerIsTaken()
    {
        _store.Save(Backup(Static("{a}", 7, "1.1.1.1")));
        _machine.Add("{a}", 7, isDhcp: false, "127.0.0.1", "1.1.1.1", "9.9.9.9");

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Empty(_control.Calls);
    }

    [Fact]
    public async Task AListStartingWith127AndAnotherServerIsNotTaken()
    {
        _store.Save(Backup(Static("{a}", 7, "1.1.1.1")));
        _machine.Add("{a}", 7, isDhcp: false, "127.0.0.1", "9.9.9.9");

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(["127.0.0.1", "1.1.1.1"], Assert.Single(_control.Calls).Servers);
    }

    [Fact]
    public async Task AListWith127SecondIsNotTaken()
    {
        _store.Save(Backup(Static("{a}", 7, "1.1.1.1")));
        _machine.Add("{a}", 7, isDhcp: false, "1.1.1.1", "127.0.0.1");

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(["127.0.0.1", "1.1.1.1"], Assert.Single(_control.Calls).Servers);
    }

    [Fact]
    public async Task AListOf127AloneIsNotTaken()
    {
        _store.Save(Backup(Static("{a}", 7, "1.1.1.1")));
        _machine.Add("{a}", 7, isDhcp: false, "127.0.0.1");

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(["127.0.0.1", "1.1.1.1"], Assert.Single(_control.Calls).Servers);
    }

    [Fact]
    public async Task ASavedServerAfterALoopbackOneIsTheOnePutSecond()
    {
        // A copy an older, half-applied run wrote: the loopback in it is skipped, not forwarded to.
        _store.Save(Backup(Static("{a}", 7, "127.0.0.1", "1.1.1.1")));
        _machine.Add("{a}", 7, isDhcp: true, "10.0.0.1");

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(["127.0.0.1", "1.1.1.1"], Assert.Single(_control.Calls).Servers);
        Assert.Equal([IPAddress.Parse("1.1.1.1")], Assert.Single(_forwarders));
    }

    [Fact]
    public async Task TheForwarderIsBuiltFromTheSavedServersWithoutRepeatsInInterfaceOrder()
    {
        _machine.Add("{a}", 7, isDhcp: false, "9.9.9.9", "1.1.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "1.1.1.1", "8.8.8.8");

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(
            [IPAddress.Parse("9.9.9.9"), IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")],
            Assert.Single(_forwarders));
    }

    [Fact]
    public async Task TheForwarderIsNotRebuiltWhileTheServersStayTheSame()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();

        await enforcer.ReconcileAsync(Plan(Blocked), default);
        await enforcer.ReconcileAsync(Plan(Blocked, Allowed), default);

        Assert.Single(_forwarders);
    }

    [Fact]
    public async Task TheForwarderIsRebuiltWhenANewInterfaceBringsAServer()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);

        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(2, _forwarders.Count);
        Assert.Equal([IPAddress.Parse("192.168.1.1"), IPAddress.Parse("9.9.9.9")], _forwarders[1]);
    }

    [Fact]
    public async Task AnAdapterNotOnTheMachineLendsTheForwarderNoServerWhileOthersAreThere()
    {
        // Its router first in the list costs every uncached lookup a timeout.
        _store.Save(Backup(Dhcp("{a}", 7, "192.168.1.1"), Static("{c}", 8, "8.8.8.8")));
        _machine.Add("{c}", 8, isDhcp: false, "127.0.0.1", "8.8.8.8");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("9.9.9.9")], _forwarders.Last());
    }

    [Fact]
    public async Task WithNoAdapterOfTheCopyOnTheMachineTheForwarderStillUsesTheCopy()
    {
        _store.Save(Backup(Dhcp("{a}", 7, "192.168.1.1")));

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal([IPAddress.Parse("192.168.1.1")], _forwarders.Last());
    }

    [Fact]
    public async Task AQueryOutsideThePlanGoesToTheForwarderTheLayerInstalled()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");

        await Make().ReconcileAsync(Plan(Blocked), default);
        await _handler.AnswerAsync(Query(Allowed), DnsTransportKind.Udp, default);

        Assert.Equal(1, _forwarder.Calls);
    }

    [Fact]
    public async Task WithNoForwarderInstalledTheSwitchAnswersNothing()
    {
        Assert.Null(await _upstream.ForwardAsync(Query(Allowed), overTcp: false, default));
        Assert.Null(_upstream.Current);
    }

    [Fact]
    public async Task TheSwitchPassesTheQueryAndTheTransportOn()
    {
        _upstream.Use(_forwarder);

        await _upstream.ForwardAsync(Query(Allowed), overTcp: true, default);

        Assert.Equal(1, _forwarder.Calls);
        Assert.True(_forwarder.LastOverTcp);
        Assert.Same(_forwarder, _upstream.Current);
    }

    [Fact]
    public async Task ARefusalOnOneInterfaceDoesNotStopTheOthers()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        _control.Refuse.Add(7);

        var result = await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal([7, 9], _control.Calls.Select(call => call.Index));
        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.Equal(DnsEnforcer.SettingsRefused, result.Detail);
        Assert.Equal(["127.0.0.1", "9.9.9.9"], _machine.Of(9).Servers);
        Assert.Equal(1, _cache.Flushes);
    }

    [Fact]
    public async Task ANetshThatCannotStartIsARefusalOfThatInterfaceAlone()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        _control.Throw.Add(7);

        var result = await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(DnsEnforcer.SettingsRefused, result.Detail);
        Assert.Equal(["127.0.0.1", "9.9.9.9"], _machine.Of(9).Servers);
    }

    [Fact]
    public async Task NothingIsFlushedWhenNoInterfaceMoved()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _control.Refuse.Add(7);

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(0, _cache.Flushes);
    }

    [Fact]
    public async Task ARefusedInterfaceIsTriedAgainOnTheNextPass()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _control.Refuse.Add(7);
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Refuse.Clear();

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(2, _control.Calls.Count);
        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
    }

    [Fact]
    public async Task ACacheThatWillNotFlushIsNotAFailure()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _cache.Result = false;

        var result = await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
    }

    [Fact]
    public async Task APortTakenAfterTheProbeIsReportedAsBusyAndNoInterfaceMoves()
    {
        using var holder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        holder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");

        var result = await Make(((IPEndPoint)holder.LocalEndPoint!).Port).ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Skipped, result.Outcome);
        Assert.Equal(DnsEnforcer.PortBusy, result.Detail);
        Assert.Empty(_control.Calls);

        // Nothing of ours is listening to ask.
        Assert.Empty(_check.Ports);
    }

    [Fact]
    public async Task AServerThatCouldNotStartIsAskedForAgainOnTheNextPass()
    {
        var holder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        holder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make(((IPEndPoint)holder.LocalEndPoint!).Port);
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        holder.Dispose();

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.True(_servers.Last().IsRunning);
    }

    [Fact]
    public async Task ClearPutsEachInterfaceBackInItsOwnModeAndOrder()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9", "1.1.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Calls.Clear();

        await enforcer.ClearAsync(default);

        Assert.Equal(2, _control.Calls.Count);
        Assert.Equal((7, (IReadOnlyList<string>?)null), _control.Calls[0]);
        Assert.Equal(9, _control.Calls[1].Index);
        Assert.Equal(["9.9.9.9", "1.1.1.1"], _control.Calls[1].Servers);
        Assert.True(_machine.Of(7).IsDhcp);
        Assert.Equal(["9.9.9.9", "1.1.1.1"], _machine.Of(9).Servers);
    }

    [Fact]
    public async Task ClearRestoresThenStopsTheServerThenFlushesThenForgetsTheCopy()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _events.Clear();
        var runningAtNetsh = false;
        _control.Before = _ => runningAtNetsh = _servers.Single().IsRunning;

        await enforcer.ClearAsync(default);

        Assert.True(runningAtNetsh);
        Assert.False(_servers.Single().IsRunning);
        Assert.Equal(["netsh:7", "flush"], _events);
        Assert.Null(_store.Load());
        Assert.Null(_mirror.Held);
        Assert.False(File.Exists(_paths.DnsBackupFile));
        Assert.Null(_upstream.Current);
    }

    [Fact]
    public async Task ClearWorksForALayerThatNeverCameUp()
    {
        // Declared unavailable, and still able to take back what an earlier run did.
        using var holder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        holder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _store.Save(Backup(Dhcp("{a}", 7, "192.168.1.1")));
        _machine.Add("{a}", 7, isDhcp: false, "127.0.0.1", "192.168.1.1");
        var enforcer = Make(((IPEndPoint)holder.LocalEndPoint!).Port);
        Assert.False((await enforcer.ProbeAsync(default)).IsAvailable);

        await enforcer.ClearAsync(default);

        Assert.Equal([(7, (IReadOnlyList<string>?)null)], _control.Calls);
        Assert.Null(_store.Load());
        Assert.Equal(1, _cache.Flushes);
    }

    [Fact]
    public async Task AClearThatCouldNotPutAnInterfaceBackThrowsAndKeepsTheCopy()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Refuse.Add(7);
        _control.Calls.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => enforcer.ClearAsync(default));

        // The others are put back all the same, and the server goes with the session.
        Assert.Equal([7, 9], _control.Calls.Select(call => call.Index));
        Assert.False(_servers.Single().IsRunning);
        Assert.NotNull(_store.Load());
    }

    [Fact]
    public async Task ASessionAfterAFailedClearStartsFromTheCopyThatWasKept()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Refuse.Add(7);
        await Assert.ThrowsAsync<InvalidOperationException>(() => enforcer.ClearAsync(default));
        _control.Refuse.Clear();
        var writes = _mirror.Writes;

        await enforcer.ReconcileAsync(Plan(Blocked), default);

        // The interface still reads 127.0.0.1 first; re-reading it would save that as original.
        Assert.Equal(writes, _mirror.Writes);
        Assert.True(_store.Load()!.Interfaces.Single().IsDhcp);
    }

    [Fact]
    public async Task AClearWhileNeitherCopyCanBeReadKeepsWhatTheLastReleaseLeftAndFails()
    {
        // Memory is then the only place that says what the interface was.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Refuse.Add(7);
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);
        _mirror.Refuses = true;
        using (new FileStream(_paths.DnsBackupFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => enforcer.ClearAsync(default));
        }

        _mirror.Refuses = false;
        _control.Refuse.Clear();
        _control.Calls.Clear();

        var retry = await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Equal(ReconcileOutcome.Changed, retry.Outcome);
        Assert.Equal([(7, (IReadOnlyList<string>?)null)], _control.Calls);
    }

    [Fact]
    public async Task ClearWithNoCopyDoesNothingAndDoesNotThrow()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");

        await Make().ClearAsync(default);

        Assert.Empty(_control.Calls);
        Assert.Equal(0, _cache.Flushes);
    }

    [Fact]
    public async Task ACopyTheRegistryRefusedIsAChangeThatSaysSo()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _mirror.Refuses = true;

        var result = await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.Equal(DnsEnforcer.BackupOnePlace, result.Detail);
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("registry"));
    }

    [Fact]
    public async Task ACopyTheFileRefusedIsAChangeThatSaysSo()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        Directory.CreateDirectory(_paths.DnsBackupFile);

        var result = await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(DnsEnforcer.BackupOnePlace, result.Detail);
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("file"));
    }

    [Fact]
    public async Task ACopyNeitherPlaceTookMovesNoInterface()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _mirror.Refuses = true;
        Directory.CreateDirectory(_paths.DnsBackupFile);

        await Assert.ThrowsAsync<AggregateException>(() => Make().ReconcileAsync(Plan(Blocked), default));

        Assert.Empty(_control.Calls);
    }

    [Fact]
    public async Task ACopyNeitherPlaceTookLeavesNoServerThisPassStartedRunning()
    {
        // The server starts before the copy is saved, so a save that fails has one to stop.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _mirror.Refuses = true;
        Directory.CreateDirectory(_paths.DnsBackupFile);

        var enforcer = Make();

        await Assert.ThrowsAsync<AggregateException>(() => enforcer.ReconcileAsync(Plan(Blocked), default));

        Assert.False(Assert.Single(_servers).IsRunning);
        Assert.False(_lock.Held);

        // Forgotten as well as stopped: the next pass starts a server of its own, not the dead one.
        _mirror.Refuses = false;
        Directory.Delete(_paths.DnsBackupFile);

        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(2, _servers.Count);
        Assert.True(_servers[1].IsRunning);
        Assert.Equal(_servers[1].Port, _check.Ports.Last());
    }

    [Fact]
    public async Task AHeldServerKeepsRunningWhenTheCopyForANewInterfaceCannotBeSaved()
    {
        // Only a server the failing pass started goes with it; the session's own stays.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        _mirror.Refuses = true;

        // The copy stays readable, so the held check passes and only the merge fails to save.
        using (new FileStream(_paths.DnsBackupFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<AggregateException>(() => enforcer.ReconcileAsync(Plan(Blocked), default));
        }

        Assert.DoesNotContain(_log.Entries, entry => entry.Message.Contains("saved again", StringComparison.Ordinal));
        Assert.True(Assert.Single(_servers).IsRunning);
        Assert.True((await enforcer.ProbeAsync(default)).IsAvailable);
    }

    [Fact]
    public async Task ACleanBetweenTwoPassesLeavesACopyBehindTheNextTakeover()
    {
        // chronos clean next to a live service: without this, the next pass re-takes the
        // interface from memory and a reboot leaves it on 127.0.0.1 with no copy anywhere.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        new DnsRestore(_store, _control, _machine, NullLogger.Instance).RestoreAndClear();
        Assert.Null(_store.Load());

        await enforcer.ReconcileAsync(Plan(Blocked), default);

        var copy = Assert.Single(_store.Load()!.Interfaces);
        Assert.True(copy.IsDhcp);
        Assert.Equal(["192.168.1.1"], copy.Servers);
        Assert.Equal(["127.0.0.1", "192.168.1.1"], _machine.Of(7).Servers);
    }

    [Fact]
    public async Task ACopyGoneWhileHeldIsSavedAgainBeforeAnyNetsh()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        new DnsRestore(_store, _control, _machine, NullLogger.Instance).RestoreAndClear();
        _events.Clear();

        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(["backup", "check", "netsh:7", "flush"], _events);
    }

    [Fact]
    public async Task ACopyDeletedWithoutARestoreIsSavedAgainOnTheNextPass()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _store.Clear();
        var calls = _control.Calls.Count;

        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.NotNull(_store.Load());
        Assert.Equal(calls, _control.Calls.Count);
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("saved again"));
    }

    [Fact]
    public async Task ACopyThatCannotBeSavedAgainMovesNoInterface()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        new DnsRestore(_store, _control, _machine, NullLogger.Instance).RestoreAndClear();
        _mirror.Refuses = true;
        Directory.CreateDirectory(_paths.DnsBackupFile);
        var calls = _control.Calls.Count;

        await Assert.ThrowsAsync<AggregateException>(() => enforcer.ReconcileAsync(Plan(Blocked), default));

        Assert.Equal(calls, _control.Calls.Count);
        Assert.Equal(["192.168.1.1"], _machine.Of(7).Servers);
    }

    [Fact]
    public async Task ACopySavedAgainToOnePlaceSaysSo()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        new DnsRestore(_store, _control, _machine, NullLogger.Instance).RestoreAndClear();
        _mirror.Refuses = true;

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.Equal(DnsEnforcer.BackupOnePlace, result.Detail);
    }

    [Fact]
    public async Task AHeldPassWithTheCopyInPlaceWritesNoCopy()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        var writes = _mirror.Writes;

        await enforcer.ReconcileAsync(Plan(Blocked), default);
        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(writes, _mirror.Writes);
    }

    [Fact]
    public async Task AHeldPassWithTheCopyInPlaceAsksTheRegistryOnceAndSaysNothing()
    {
        // The check judges the copy Load would take, so both places are read; once, quietly.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        var reads = _mirror.Reads;
        var logged = _log.Entries.Count;

        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(reads + 1, _mirror.Reads);
        Assert.Equal(logged, _log.Entries.Count);
    }

    [Fact]
    public async Task AReSaveToOnePlaceThenAMergeToBothSaysNothingOfOnePlace()
    {
        // The latest save says where the copy is.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        new DnsRestore(_store, _control, _machine, NullLogger.Instance).RestoreAndClear();
        _mirror.Refuses = true;
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        _machine.BeforeRead = () => _mirror.Refuses = false;

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.Null(result.Detail);
        Assert.Equal(2, _store.Sources().Registry!.Interfaces.Count);
    }

    [Fact]
    public async Task AReSaveToOnePlaceOnAPassThatMovesNothingStillSaysSo()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _store.Clear();
        _mirror.Refuses = true;

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Unchanged, result.Outcome);
        Assert.Equal(DnsEnforcer.BackupOnePlace, result.Detail);
    }

    [Fact]
    public async Task ACleanDuringASessionWithAnAbsentAdapterLeavesTheWholeCopyBehindTheNextTakeover()
    {
        // The pass takes {b} again; its original must be on disk by then, not only in memory.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _machine.Remove("{a}");
        _control.Refuse.Add(7);
        new DnsRestore(_store, _control, _machine, NullLogger.Instance).RestoreAndClear();
        Assert.Equal(["9.9.9.9"], _machine.Of(9).Servers);
        var copyAtWrite = new List<string[]>();
        _control.Before = _ => copyAtWrite.Add([.. _store.Load()!.Interfaces.Select(state => state.Guid)]);

        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(["127.0.0.1", "9.9.9.9"], _machine.Of(9).Servers);
        Assert.Equal(["{a}", "{b}"], copyAtWrite.First());
    }

    [Fact]
    public async Task ACleanDuringASessionWithARefusedAdapterLeavesTheWholeCopyBehindTheNextTakeover()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Refuse.Add(7);
        new DnsRestore(_store, _control, _machine, NullLogger.Instance).RestoreAndClear();
        var copyAtWrite = new List<string[]>();
        _control.Before = _ => copyAtWrite.Add([.. _store.Load()!.Interfaces.Select(state => state.Guid)]);

        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(["127.0.0.1", "9.9.9.9"], _machine.Of(9).Servers);
        Assert.Equal(["{a}", "{b}"], copyAtWrite.First());
    }

    [Fact]
    public async Task AnUninstallWithAnAbsentAdapterPutsBackWhatTheServiceTookBeforeItStopped()
    {
        // Clean, a pass that takes {b} again, the stop, then the second restore.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _machine.Remove("{a}");
        _control.Refuse.Add(7);
        var commandLine = new DnsRestore(_store, _control, _machine, NullLogger.Instance);
        commandLine.RestoreAndClear();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        await enforcer.DisposeAsync();

        commandLine.RestoreAndClear();

        Assert.False(_machine.Of(9).IsDhcp);
        Assert.Equal(["9.9.9.9"], _machine.Of(9).Servers);
        Assert.Contains("{a}", _store.Load()!.Interfaces.Select(state => state.Guid));
    }

    [Fact]
    public async Task ACleanWhosePruneReachedOnlyTheFileStillLeavesACopyNamingWhatThePassTookBack()
    {
        // The registry keeps the wider copy under the same moment, and Load takes the file.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _machine.Remove("{a}");
        _control.Refuse.Add(7);
        _mirror.Refuses = true;
        new DnsRestore(_store, _control, _machine, NullLogger.Instance).RestoreAndClear();
        _mirror.Refuses = false;

        await enforcer.ReconcileAsync(Plan(Blocked), default);
        await enforcer.DisposeAsync();

        Assert.Equal(["127.0.0.1", "9.9.9.9"], _machine.Of(9).Servers);
        Assert.Contains("{b}", _store.Load()!.Interfaces.Select(state => state.Guid));
    }

    [Fact]
    public async Task APassWhoseCopyNoLongerNamesEveryInterfaceItHoldsSavesItAgainBeforeAnyNetsh()
    {
        // Defence in depth: "some copy" is not "this session's copy".
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _store.Save(Backup(Dhcp("{a}", 7, "192.168.1.1")));
        _machine.SetStatic(9, ["9.9.9.9"]);
        var copyAtWrite = new List<string[]>();
        _control.Before = _ => copyAtWrite.Add([.. _store.Load()!.Interfaces.Select(state => state.Guid)]);

        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(["{a}", "{b}"], Assert.Single(copyAtWrite));
    }

    [Fact]
    public async Task StoppingSavesTheCopyAgainWhenItNoLongerNamesEveryInterfaceItHolds()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _store.Save(Backup(Dhcp("{a}", 7, "192.168.1.1")));

        await enforcer.DisposeAsync();

        Assert.Equal(["{a}", "{b}"], _store.Load()!.Interfaces.Select(state => state.Guid));
    }

    [Fact]
    public async Task APassWhileTheCommandLineHoldsTheSettingsWritesNothingAndSaysNothing()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _lock.HeldElsewhere = true;

        var result = await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Unchanged, result.Outcome);
        Assert.Empty(_control.Calls);
        Assert.Equal(0, _mirror.Writes);
        Assert.Empty(_servers);
        Assert.Empty(_check.Ports);
        Assert.DoesNotContain(_log.Entries, entry => entry.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task ThePassNeverWaitsForTheSettings()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _lock.HeldElsewhere = true;

        await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal([TimeSpan.Zero], _lock.Waits);
    }

    [Fact]
    public async Task APassSkippedForTheLockTakesOverOnTheNextOne()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        _lock.HeldElsewhere = true;
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _lock.HeldElsewhere = false;

        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(["127.0.0.1", "192.168.1.1"], _machine.Of(7).Servers);
    }

    [Fact]
    public async Task TheSettingsAreHeldFromTheCopyCheckThroughEveryNetshAndLetGoAfter()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        new DnsRestore(_store, _control, _machine, NullLogger.Instance).RestoreAndClear();
        var heldAtRead = false;
        var heldAtNetsh = new List<bool>();
        _machine.BeforeRead = () => heldAtRead = _lock.Held;
        _control.Before = _ => heldAtNetsh.Add(_lock.Held);

        await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.True(heldAtRead);
        Assert.Equal([true, true], heldAtNetsh);
        Assert.False(_lock.Held);
    }

    [Fact]
    public async Task TheSettingsAreLetGoWhenTheServerCouldNotStart()
    {
        using var holder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        holder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");

        var result = await Make(((IPEndPoint)holder.LocalEndPoint!).Port).ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(DnsEnforcer.PortBusy, result.Detail);
        Assert.False(_lock.Held);
    }

    [Fact]
    public async Task TheSettingsAreLetGoWhenTheCopyCouldNotBeSaved()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _mirror.Refuses = true;
        Directory.CreateDirectory(_paths.DnsBackupFile);

        await Assert.ThrowsAsync<AggregateException>(() => Make().ReconcileAsync(Plan(Blocked), default));

        Assert.False(_lock.Held);
    }

    [Fact]
    public async Task StoppingSavesTheCopyAgainWhenItIsGone()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _store.Clear();

        await enforcer.DisposeAsync();

        Assert.Equal(["192.168.1.1"], Assert.Single(_store.Load()!.Interfaces).Servers);
    }

    [Fact]
    public async Task StoppingWithTheCopyInPlaceWritesNothing()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        var writes = _mirror.Writes;

        await enforcer.DisposeAsync();

        Assert.Equal(writes, _mirror.Writes);
    }

    [Fact]
    public async Task StoppingIdleSavesNothing()
    {
        await Make().DisposeAsync();

        Assert.Equal(0, _mirror.Writes);
        Assert.False(File.Exists(_paths.DnsBackupFile));
    }

    [Fact]
    public async Task StoppingWhenTheCopyCannotBeSavedAnywhereSaysSoAndStillStops()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _store.Clear();
        _mirror.Refuses = true;
        Directory.CreateDirectory(_paths.DnsBackupFile);

        await enforcer.DisposeAsync();

        Assert.False(_servers.Single().IsRunning);
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("stopped"));
    }

    [Fact]
    public async Task TheCheckAsksTheRunningServerOnItsOwnPortUnderTheSettingsLock()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var running = false;
        var held = false;
        _check.During = _ =>
        {
            running = _servers.Single().IsRunning;
            held = _lock.Held;
        };

        await Make(FreePort()).ReconcileAsync(Plan(Blocked), default);

        Assert.Equal([_servers.Single().Port], _check.Ports);
        Assert.True(running);
        Assert.True(held);
    }

    [Fact]
    public async Task AFirstPassThatCannotHearItselfTakesNothingAndSaysLoopbackBlocked()
    {
        // The live run: a VPN's filter drops every datagram to 127.0.0.1:53.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _check.Hears = false;

        var result = await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Skipped, result.Outcome);
        Assert.Equal(DnsEnforcer.LoopbackBlocked, result.Detail);
        Assert.Empty(_control.Calls);
        Assert.Equal(0, _mirror.Writes);
        Assert.Null(_store.Load());
        Assert.False(File.Exists(_paths.DnsBackupFile));
        Assert.Equal(0, _cache.Flushes);
        Assert.False(Assert.Single(_servers).IsRunning);
        Assert.Null(_upstream.Current);
        Assert.False(_lock.Held);
    }

    [Fact]
    public async Task AFailedCheckIsWarnedAboutOnceWithThePortAndNoDomain()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _check.Hears = false;

        await Make().ReconcileAsync(Plan(Blocked), default);

        var warning = Assert.Single(_log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("127.0.0.1", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Blocked, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterAFailedCheckTheProbeSaysLoopbackBlockedWithoutStartingAnything()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _check.Hears = false;
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);

        var availability = await enforcer.ProbeAsync(default);

        Assert.False(availability.IsAvailable);
        Assert.Equal(DnsEnforcer.LoopbackBlocked, availability.Reason);
        Assert.Single(_servers);
        Assert.Single(_check.Ports);
    }

    [Fact]
    public async Task TheProbeDoesNotAskAgainWithinTheMinute()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _check.Hears = false;
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _check.Hears = true;
        _clock.Advance(DnsEnforcer.RecheckInterval - TimeSpan.FromSeconds(1));

        var availability = await enforcer.ProbeAsync(default);

        Assert.Equal(DnsEnforcer.LoopbackBlocked, availability.Reason);
        Assert.Single(_check.Ports);
    }

    [Fact]
    public void TheRecheckIsOnceAMinute()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), DnsEnforcer.RecheckInterval);
    }

    [Fact]
    public async Task AMinuteLaterTheProbeAsksAgainQuietlyAndIsAvailableOnceItHearsItself()
    {
        // A VPN turned off is noticed within the minute.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _check.Hears = false;
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _check.Hears = true;
        _clock.Advance(DnsEnforcer.RecheckInterval);
        _log.Clear();
        _serverLog.Clear();

        var availability = await enforcer.ProbeAsync(default);

        Assert.True(availability.IsAvailable);
        Assert.Equal(2, _servers.Count);
        Assert.Equal(_servers[1].Port, _check.Ports[1]);
        Assert.False(_servers[1].IsRunning);
        Assert.Empty(_control.Calls);
        Assert.DoesNotContain(_log.Entries, entry => entry.Level >= LogLevel.Information);
        Assert.DoesNotContain(_serverLog.Entries, entry => entry.Level >= LogLevel.Information);
        Assert.NotEmpty(_serverLog.Entries);

        // Forgotten: the next probe is the ordinary one again, not a minute more of blocked.
        Assert.True((await enforcer.ProbeAsync(default)).IsAvailable);
        Assert.Equal(2, _check.Ports.Count);
    }

    [Fact]
    public async Task ARecheckThatStillFailsWaitsAnotherFullMinute()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _check.Hears = false;
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _clock.Advance(DnsEnforcer.RecheckInterval);
        _log.Clear();
        _serverLog.Clear();

        var first = await enforcer.ProbeAsync(default);
        _clock.Advance(DnsEnforcer.RecheckInterval - TimeSpan.FromSeconds(1));
        var second = await enforcer.ProbeAsync(default);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var third = await enforcer.ProbeAsync(default);

        Assert.All([first, second, third], availability => Assert.Equal(DnsEnforcer.LoopbackBlocked, availability.Reason));
        Assert.Equal(3, _check.Ports.Count);
        Assert.All(_servers, server => Assert.False(server.IsRunning));

        // A VPN left on is not news every minute.
        Assert.DoesNotContain(_log.Entries, entry => entry.Level >= LogLevel.Information);
        Assert.DoesNotContain(_serverLog.Entries, entry => entry.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task ARecheckWhoseServerCannotStartStaysLoopbackBlockedAndAsksNothing()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _check.Hears = false;
        var enforcer = Make(FreePort());
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _clock.Advance(DnsEnforcer.RecheckInterval);
        _check.Hears = true;
        _squatOnCreate = true;
        _serverLog.Clear();

        var availability = await enforcer.ProbeAsync(default);

        Assert.Equal(DnsEnforcer.LoopbackBlocked, availability.Reason);
        Assert.Single(_check.Ports);

        // The quiet start's refusal is as quiet as its success.
        Assert.DoesNotContain(_serverLog.Entries, entry => entry.Level >= LogLevel.Information);
        Assert.NotEmpty(_serverLog.Entries);
    }

    [Fact]
    public async Task ABusyPortIsReportedBeforeARememberedFailedCheck()
    {
        // Somebody else on the port is the more urgent reason, and nothing could be asked anyway.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _check.Hears = false;
        var port = FreePort();
        var enforcer = Make(port);
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        using var holder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        holder.Bind(new IPEndPoint(IPAddress.Loopback, port));

        var availability = await enforcer.ProbeAsync(default);

        Assert.Equal(DnsEnforcer.PortBusy, availability.Reason);
    }

    [Fact]
    public async Task OnceItHearsItselfAgainTheNextPassTakesTheInterfaces()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _check.Hears = false;
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _check.Hears = true;
        _clock.Advance(DnsEnforcer.RecheckInterval);
        await enforcer.ProbeAsync(default);

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        var call = Assert.Single(_control.Calls);
        Assert.Equal(7, call.Index);
        Assert.Equal(["127.0.0.1", "192.168.1.1"], call.Servers);
        Assert.True(_servers.Last().IsRunning);
    }

    [Fact]
    public async Task AHeldLayerThatStopsHearingItselfGivesTheInterfacesBackAsAnEmptyPlanDoes()
    {
        // Mid-session, the VPN comes up.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Calls.Clear();
        _events.Clear();
        var heldAtNetsh = false;
        _control.Before = _ => heldAtNetsh = _lock.Held;
        (bool Held, bool Running) atFlush = default;
        _cache.Before = () => atFlush = (_lock.Held, _servers.Single().IsRunning);
        _check.Hears = false;

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Skipped, result.Outcome);
        Assert.Equal(DnsEnforcer.LoopbackBlocked, result.Detail);
        Assert.Equal([(7, (IReadOnlyList<string>?)null)], _control.Calls);
        Assert.True(_machine.Of(7).IsDhcp);

        // Asked twice before a session is ended over it.
        Assert.Equal(["check", "check", "netsh:7", "flush"], _events);
        Assert.True(heldAtNetsh);

        // Restore, flush and settle under the lock; the server stops after it is let go.
        Assert.Equal((true, true), atFlush);
        Assert.False(_lock.Held);
        Assert.False(Assert.Single(_servers).IsRunning);
        Assert.Null(_store.Load());
        Assert.Null(_upstream.Current);
        Assert.NotEqual(RcodeNameError, Rcode(await _handler.AnswerAsync(Query(Blocked), DnsTransportKind.Udp, default)));
    }

    [Fact]
    public async Task AHeldLayerThatMissesOnceAndThenHearsItselfKeepsTheInterfaces()
    {
        // One dropped datagram does not end a session.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Calls.Clear();
        _log.Clear();
        _check.Next.Enqueue(false);

        var result = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Unchanged, result.Outcome);
        Assert.Equal(3, _check.Ports.Count);
        Assert.Empty(_control.Calls);
        Assert.True(_servers.Single().IsRunning);
        Assert.NotNull(_store.Load());
        Assert.DoesNotContain(_log.Entries, entry => entry.Level >= LogLevel.Information);
        Assert.True((await enforcer.ProbeAsync(default)).IsAvailable);
    }

    [Fact]
    public async Task AFirstPassAsksOnlyOnce()
    {
        // Nothing is held yet, so nothing is lost by saying so at once.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _check.Next.Enqueue(false);

        var result = await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(DnsEnforcer.LoopbackBlocked, result.Detail);
        Assert.Single(_check.Ports);
        Assert.Empty(_control.Calls);
    }

    [Fact]
    public async Task AnAbsentAdapterLeftInTheCopyDoesNotBringTheCheckBackEveryPass()
    {
        // Only a refusal is retried by the pass; an absent adapter waits for the minute re-check,
        // since the pass cannot restore it anyway.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        _machine.Add("{b}", 9, isDhcp: false, "9.9.9.9");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _machine.Remove("{b}");
        _control.Refuse.Add(9);
        _check.Hears = false;

        var released = await enforcer.ReconcileAsync(Plan(Blocked), default);
        var servers = _servers.Count;
        var checks = _check.Ports.Count;
        _clock.Advance(TimeSpan.FromSeconds(15));
        var probe = await enforcer.ProbeAsync(default);

        Assert.Equal(DnsEnforcer.LoopbackBlocked, released.Detail);
        Assert.NotNull(_store.Load());
        Assert.Equal(DnsEnforcer.LoopbackBlocked, probe.Reason);
        Assert.Equal(servers, _servers.Count);
        Assert.Equal(checks, _check.Ports.Count);
    }

    [Fact]
    public async Task AnAbsentAdapterThatComesBackWhileUnheardIsPutBackByTheProbeWithNoServer()
    {
        // No pass runs while the probe says unavailable, so the probe gives it back.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _machine.Remove("{a}");
        _control.Refuse.Add(7);
        _check.Hears = false;
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        Assert.NotNull(_store.Load());

        _machine.Add("{a}", 7, isDhcp: false, "127.0.0.1", "192.168.1.1");
        _control.Refuse.Remove(7);
        _control.Calls.Clear();
        var servers = _servers.Count;
        var checks = _check.Ports.Count;
        var heldAtNetsh = false;
        _control.Before = _ => heldAtNetsh = _lock.Held;

        var probe = await enforcer.ProbeAsync(default);

        Assert.Equal(DnsEnforcer.LoopbackBlocked, probe.Reason);
        Assert.Equal([(7, (IReadOnlyList<string>?)null)], _control.Calls);
        Assert.True(_machine.Of(7).IsDhcp);
        Assert.True(heldAtNetsh);
        Assert.Null(_store.Load());
        Assert.Equal(servers, _servers.Count);
        Assert.Equal(checks, _check.Ports.Count);

        // Putting an adapter back says nothing about the loopback: still inside the minute, the
        // next probe is as blocked as this one, and asks nothing.
        _clock.Advance(TimeSpan.FromSeconds(15));

        var later = await enforcer.ProbeAsync(default);

        Assert.Equal(DnsEnforcer.LoopbackBlocked, later.Reason);
        Assert.Equal(servers, _servers.Count);
        Assert.Equal(checks, _check.Ports.Count);
    }

    [Fact]
    public async Task AnAdapterThatComesBackAndIsRefusedOpensTheProbeToThePass()
    {
        // The refusal is the pass's to retry, which only a pass does.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _machine.Remove("{a}");
        _control.Refuse.Add(7);
        _check.Hears = false;
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _machine.Add("{a}", 7, isDhcp: false, "127.0.0.1", "192.168.1.1");

        var probe = await enforcer.ProbeAsync(default);

        Assert.True(probe.IsAvailable);
        Assert.NotNull(_store.Load());
    }

    [Fact]
    public async Task APassThatHearsItselfForgetsAnEarlierFailure()
    {
        // A refused release, then a pass that hears itself and takes over; once the session ends,
        // the probe must not act on the old failure.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _check.Hears = false;
        _control.Refuse.Add(7);
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _control.Refuse.Clear();
        _check.Hears = true;
        Assert.Equal(ReconcileOutcome.Changed, (await enforcer.ReconcileAsync(Plan(Blocked), default)).Outcome);
        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);
        var checks = _check.Ports.Count;

        var probe = await enforcer.ProbeAsync(default);

        Assert.True(probe.IsAvailable);
        Assert.Equal(checks, _check.Ports.Count);
    }

    [Fact]
    public async Task AfterAMidSessionReleaseTheProbeSaysLoopbackBlocked()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _check.Hears = false;
        await enforcer.ReconcileAsync(Plan(Blocked), default);

        var availability = await enforcer.ProbeAsync(default);

        Assert.Equal(DnsEnforcer.LoopbackBlocked, availability.Reason);
    }

    [Fact]
    public async Task ACopyARunThatDidNotFinishLeftIsPutBackWhenTheServerCannotHearItself()
    {
        // After a crash the interfaces still point at 127.0.0.1, where nothing now answers.
        _machine.Add("{a}", 7, isDhcp: false, "127.0.0.1", "192.168.1.1");
        _store.Save(Backup(Dhcp("{a}", 7, "192.168.1.1")));
        _check.Hears = false;

        var result = await Make().ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(DnsEnforcer.LoopbackBlocked, result.Detail);
        Assert.Equal([(7, (IReadOnlyList<string>?)null)], _control.Calls);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task AReleaseWindowsRefusedAfterAFailedCheckIsFailedAndTriedAgainOnTheNextPass()
    {
        // What could not be put back keeps the pass running, not the remembered failure.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);
        _check.Hears = false;
        _control.Refuse.Add(7);

        var refused = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(ReconcileOutcome.Failed, refused.Outcome);
        Assert.Equal(DnsEnforcer.SettingsRefused, refused.Detail);
        Assert.NotNull(_store.Load());
        Assert.True((await enforcer.ProbeAsync(default)).IsAvailable);

        _control.Refuse.Clear();
        _control.Calls.Clear();

        var retried = await enforcer.ReconcileAsync(Plan(Blocked), default);

        Assert.Equal(DnsEnforcer.LoopbackBlocked, retried.Detail);
        Assert.Equal([(7, (IReadOnlyList<string>?)null)], _control.Calls);
        Assert.Null(_store.Load());
        Assert.Equal(DnsEnforcer.LoopbackBlocked, (await enforcer.ProbeAsync(default)).Reason);
    }

    [Fact]
    public async Task AnEmptyPlanRunsNoCheck()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);

        await enforcer.ReconcileAsync(EnforcementPlan.Empty, default);

        Assert.Single(_check.Ports);
    }

    [Fact]
    public async Task DisposingTheLayerStopsItsServer()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);

        await enforcer.DisposeAsync();

        Assert.False(_servers.Single().IsRunning);
    }

    [Fact]
    public async Task DisposingTheLayerSynchronouslyStopsItsServerToo()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.ReconcileAsync(Plan(Blocked), default);

        enforcer.Dispose();

        Assert.False(_servers.Single().IsRunning);
    }

    [Fact]
    public async Task ADisposedLayerStartsNoServer()
    {
        // A server started now would have nobody left to stop it.
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        var enforcer = Make();
        await enforcer.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => enforcer.ReconcileAsync(Plan(Blocked), default));

        Assert.Empty(_servers);
    }

    [Fact]
    public void ItRefusesToBeBuiltWithoutAnyOfItsParts()
    {
        Assert.Throws<ArgumentNullException>(() => new DnsEnforcer(null!, _handler, _upstream, CreateForwarder, _machine, _control, _store, _cache, _clock, _log, _lock, _check));
        Assert.Throws<ArgumentNullException>(() => new DnsEnforcer(CreateServer, null!, _upstream, CreateForwarder, _machine, _control, _store, _cache, _clock, _log, _lock, _check));
        Assert.Throws<ArgumentNullException>(() => new DnsEnforcer(CreateServer, _handler, null!, CreateForwarder, _machine, _control, _store, _cache, _clock, _log, _lock, _check));
        Assert.Throws<ArgumentNullException>(() => new DnsEnforcer(CreateServer, _handler, _upstream, null!, _machine, _control, _store, _cache, _clock, _log, _lock, _check));
        Assert.Throws<ArgumentNullException>(() => new DnsEnforcer(CreateServer, _handler, _upstream, CreateForwarder, null!, _control, _store, _cache, _clock, _log, _lock, _check));
        Assert.Throws<ArgumentNullException>(() => new DnsEnforcer(CreateServer, _handler, _upstream, CreateForwarder, _machine, null!, _store, _cache, _clock, _log, _lock, _check));
        Assert.Throws<ArgumentNullException>(() => new DnsEnforcer(CreateServer, _handler, _upstream, CreateForwarder, _machine, _control, null!, _cache, _clock, _log, _lock, _check));
        Assert.Throws<ArgumentNullException>(() => new DnsEnforcer(CreateServer, _handler, _upstream, CreateForwarder, _machine, _control, _store, null!, _clock, _log, _lock, _check));
        Assert.Throws<ArgumentNullException>(() => new DnsEnforcer(CreateServer, _handler, _upstream, CreateForwarder, _machine, _control, _store, _cache, null!, _log, _lock, _check));
        Assert.Throws<ArgumentNullException>(() => new DnsEnforcer(CreateServer, _handler, _upstream, CreateForwarder, _machine, _control, _store, _cache, _clock, null!, _lock, _check));
        Assert.Throws<ArgumentNullException>(() => new DnsEnforcer(CreateServer, _handler, _upstream, CreateForwarder, _machine, _control, _store, _cache, _clock, _log, null!, _check));
        Assert.Throws<ArgumentNullException>(() => new DnsEnforcer(CreateServer, _handler, _upstream, CreateForwarder, _machine, _control, _store, _cache, _clock, _log, _lock, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DnsEnforcer(CreateServer, _handler, _upstream, CreateForwarder, _machine, _control, _store, _cache, _clock, _log, _lock, _check, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DnsEnforcer(CreateServer, _handler, _upstream, CreateForwarder, _machine, _control, _store, _cache, _clock, _log, _lock, _check, 65536));
    }

    [Fact]
    public async Task ACancelledPassDoesNothing()
    {
        _machine.Add("{a}", 7, isDhcp: true, "192.168.1.1");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var enforcer = Make();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enforcer.ReconcileAsync(Plan(Blocked), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enforcer.ProbeAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enforcer.ClearAsync(cancelled.Token));

        Assert.Empty(_control.Calls);
    }

    private DnsEnforcer Make(int port = 0)
    {
        var enforcer = new DnsEnforcer(
            CreateServer,
            _handler,
            _upstream,
            CreateForwarder,
            _machine,
            _control,
            _store,
            _cache,
            _clock,
            _log,
            _lock,
            _check,
            port);

        _made.Add(enforcer);
        return enforcer;
    }

    private LoopbackDnsServer CreateServer(int port)
    {
        _portsAsked.Add(port);
        _events.Add("server");

        // Port 0 is a UDP port picked first and the same TCP port taken after it, which another
        // test's connection in TIME_WAIT can refuse; a port both halves are free on is found first.
        var server = new LoopbackDnsServer(
            _handler, _serverLog, port == 0 ? FreePort() : port);
        _servers.Add(server);

        if (_squatOnCreate)
        {
            // Somebody else binds between the layer's look at the port and its start.
            var squatter = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            squatter.Bind(new IPEndPoint(IPAddress.Loopback, server.Port));
            _squatters.Add(squatter);
        }

        return server;
    }

    private IDnsForwarder CreateForwarder(IReadOnlyList<IPAddress> servers)
    {
        _forwarders.Add([.. servers]);
        return _forwarder;
    }

    private static EnforcementPlan Plan(params string[] domains) =>
        new(SessionId, null, [.. domains.Select(domain => new SiteRule(domain, includeSubdomains: true))], []);

    private DnsBackup Backup(params InterfaceDnsState[] states) => new(_clock.UtcNow, states);

    private static InterfaceDnsState Dhcp(string guid, int index, params string[] servers) =>
        new(guid, index, $"Adapter {index}", IsDhcp: true, servers);

    private static InterfaceDnsState Static(string guid, int index, params string[] servers) =>
        new(guid, index, $"Adapter {index}", IsDhcp: false, servers);

    /// <summary>A port nothing holds right now, on either transport.</summary>
    private static int FreePort()
    {
        for (var attempt = 0; ; attempt++)
        {
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)udp.LocalEndPoint!).Port;

            var tcp = new TcpListener(IPAddress.Loopback, port);
            try
            {
                tcp.Start();
                return port;
            }
            catch (SocketException) when (attempt < 20)
            {
                // The TCP half of this one is in use; another port.
            }
            finally
            {
                tcp.Dispose();
            }
        }
    }

    private static byte Rcode(byte[]? reply) => (byte)(reply![3] & 0x0F);

    private static byte[] Query(string name)
    {
        var message = new List<byte>(new byte[12]);

        foreach (var label in name.Split('.'))
        {
            message.Add((byte)label.Length);
            message.AddRange(Encoding.ASCII.GetBytes(label));
        }

        message.AddRange([0, 0, 1, 0, 1]);

        var query = message.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(0, 2), 0x1234);
        query[2] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), 1);

        return query;
    }

    private sealed class RecordingForwarder : IDnsForwarder
    {
        public int Calls { get; private set; }

        public bool LastOverTcp { get; private set; }

        public Task<byte[]?> ForwardAsync(byte[] query, bool overTcp, CancellationToken ct)
        {
            Calls++;
            LastOverTcp = overTcp;

            return Task.FromResult<byte[]?>(null);
        }
    }

    private sealed class FakeSelfCheck(List<string> events) : IDnsSelfCheck
    {
        /// <summary>Whether the query comes back; false is a VPN's filter dropping it.</summary>
        public bool Hears { get; set; } = true;

        /// <summary>The port each check asked, in order.</summary>
        public List<int> Ports { get; } = [];

        /// <summary>Called as each check runs, for tests about what was true at that moment.</summary>
        public Action<int>? During { get; set; }

        /// <summary>Answers taken first, one per check, before <see cref="Hears"/> applies.</summary>
        public Queue<bool> Next { get; } = new();

        public bool Reaches(int port)
        {
            events.Add("check");
            Ports.Add(port);
            During?.Invoke(port);

            return Next.TryDequeue(out var next) ? next : Hears;
        }
    }

    private sealed class RecordingDnsCache(List<string> events) : IDnsCache
    {
        public int Flushes { get; private set; }

        public bool Result { get; set; } = true;

        /// <summary>Called as each flush runs, for tests about what was true at that moment.</summary>
        public Action? Before { get; set; }

        public bool Flush()
        {
            Before?.Invoke();
            Flushes++;
            events.Add("flush");

            return Result;
        }
    }
}
