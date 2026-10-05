using System.Net;
using System.Net.Sockets;
using Chronos.Core.Enforcement;
using Chronos.Core.Rules;
using Chronos.Core.Time;
using Chronos.Ipc;
using Chronos.Service.Diagnostics;
using Chronos.Service.Sites;
using Chronos.Service.Wfp;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Dns;

/// <summary>
/// Layer L2: a resolver on 127.0.0.1:53 that answers the plan's domains with NXDOMAIN, and the
/// active interfaces pointed at it with their own first server kept second.
/// </summary>
/// <remarks>
/// The backup is the layer's state. An existing backup is authoritative: its interfaces are never
/// read again, since after a crash they hold our 127.0.0.1, not their original servers.
/// Domains are never logged above Debug.
/// </remarks>
public sealed class DnsEnforcer : IEnforcer, IAsyncDisposable, IDisposable
{
    public const string LayerName = LayerNames.Dns;

    // Stable codes, never sentences: they cross into a bilingual interface.
    public const string PortBusy = "dns.port-busy";
    public const string NoUpstream = "dns.no-upstream";
    public const string SettingsRefused = "dns.settings-refused";
    public const string LoopbackBlocked = "dns.loopback-blocked";

    public const string BackupOnePlace = "dns.backup-one-place";

    public const int DefaultPort = LoopbackDnsServer.DnsPort;

    private const string ThisMachine = "127.0.0.1";

    /// <summary>How often a layer that could not hear itself asks again.</summary>
    internal static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(60);

    private readonly Func<int, LoopbackDnsServer> _createServer;
    private readonly DnsRequestHandler _handler;
    private readonly SwappableDnsForwarder _upstream;
    private readonly Func<IReadOnlyList<IPAddress>, IDnsForwarder> _createForwarder;
    private readonly IInterfaceDns _interfaces;
    private readonly INetworkDnsControl _control;
    private readonly DnsBackupStore _backups;
    private readonly DnsRestore _restore;
    private readonly IDnsSettingsLock _settingsLock;
    private readonly IDnsSelfCheck _selfCheck;
    private readonly IDnsCache _cache;
    private readonly IClock _clock;
    private readonly ILogger<DnsEnforcer> _logger;
    private readonly int _port;

    // One pass at a time: a probe, a reconcile and a clear all read and replace the fields below.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly RepeatedDiagnostic _portHolder = new();

    // Interfaces already reported as left alone, so a pass that repeats says nothing.
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    private HashSet<SiteRule> _rules = [];
    private IReadOnlyList<IPAddress> _forwardingTo = [];
    private LoopbackDnsServer? _server;
    private DnsBackup? _backup;

    // What a release could not put back yet (refused, or adapter absent); retried on idle passes.
    private DnsBackup? _unfinished;
    private bool _unfinishedRefused;

    // Once per start, so an idle machine does not read the disk every pass.
    private bool _lookedForLeftover;

    // When a query of our own last failed to reach our server. Null while it reaches.
    private DateTimeOffset? _unheardAt;

    private bool _disposed;

    public DnsEnforcer(
        Func<int, LoopbackDnsServer> createServer,
        DnsRequestHandler handler,
        SwappableDnsForwarder upstream,
        Func<IReadOnlyList<IPAddress>, IDnsForwarder> createForwarder,
        IInterfaceDns interfaces,
        INetworkDnsControl control,
        DnsBackupStore backups,
        IDnsCache cache,
        IClock clock,
        ILogger<DnsEnforcer> logger,
        IDnsSettingsLock settingsLock,
        IDnsSelfCheck selfCheck,
        int port = DefaultPort)
    {
        ArgumentNullException.ThrowIfNull(createServer);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(createForwarder);
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(settingsLock);
        ArgumentNullException.ThrowIfNull(selfCheck);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);

        _createServer = createServer;
        _handler = handler;
        _upstream = upstream;
        _createForwarder = createForwarder;
        _interfaces = interfaces;
        _control = control;
        _backups = backups;
        _restore = new DnsRestore(backups, control, interfaces, logger);
        _cache = cache;
        _clock = clock;
        _logger = logger;
        _settingsLock = settingsLock;
        _selfCheck = selfCheck;
        _port = port;
    }

    public string Name => LayerName;

    internal int Port => _port;

    internal IDnsSelfCheck SelfCheck => _selfCheck;

    public async Task<EnforcerAvailability> ProbeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Ours already; binding again would find ourselves in the way.
            if (_server is not null)
            {
                return EnforcerAvailability.Available;
            }

            // Taken and let go: the resolver comes up only when there is something to block.
            if (LoopbackDnsServer.CanTake(_port, out var holder))
            {
                _portHolder.IsNews(null);

                return await StillUnheardAsync().ConfigureAwait(false)
                    ? EnforcerAvailability.Unavailable(LoopbackBlocked)
                    : EnforcerAvailability.Available;
            }

            var named = holder ?? "an unknown process";

            // Information once per holder; the probe runs every fifteen seconds.
            if (_portHolder.IsNews(named))
            {
                _logger.LogInformation(
                    "127.0.0.1:{Port} is held by {Holder}; the DNS layer is unavailable.", _port, named);
            }
            else
            {
                _logger.LogDebug("127.0.0.1:{Port} is still held by {Holder}.", _port, named);
            }

            return EnforcerAvailability.Unavailable(PortBusy);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ReconcileResult> ReconcileAsync(EnforcementPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            return plan.Sites.Count == 0
                ? await ReconcileIdleAsync().ConfigureAwait(false)
                : await EnforceAsync(plan.Sites).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // No availability check: a layer that never came up still gives back what an earlier run took.
            var result = await ReleaseAsync().ConfigureAwait(false);

            if (result is { Complete: false })
            {
                // Throwing is how the coordinator records Failed.
                throw new InvalidOperationException(
                    $"The DNS settings of {result.Failed} interfaces could not be put back; the backup is kept.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        // Interfaces still held must not outlive the service without a backup.
        if (_backup is { } backup && !_backups.HoldsACopyOf(GuidsOf(backup)))
        {
            try
            {
                Persist(backup);
            }
            catch (AggregateException exception)
            {
                _logger.LogError(exception, "The DNS backup could not be saved again as the service stopped.");
            }
        }

        await StopServerAsync().ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Whether the last self-check failure still stands. Rechecked once a minute with a quietly
    /// started server, so a VPN left on is not news every time.
    /// </summary>
    private async Task<bool> StillUnheardAsync()
    {
        // A refusal keeps the pass running to retry. A merely absent adapter does not: the pass
        // cannot put it back and would take over and release every fifteen seconds for nothing.
        if (_unheardAt is not { } at || _unfinishedRefused)
        {
            return false;
        }

        // An absent adapter that came back is restored here, since no pass runs while the layer is
        // unavailable. Restore only, no server; a refusal hands it to the pass.
        if (_unfinished is { } unfinished)
        {
            RetryUnfinished(unfinished);

            if (_unfinishedRefused)
            {
                return false;
            }
        }

        var now = _clock.UtcNow;
        if (now - at < RecheckInterval)
        {
            return true;
        }

        _unheardAt = now;
        var server = _createServer(_port);

        if (!server.TryStart(out _, LogLevel.Debug))
        {
            // Taken since the look above; nothing of ours to ask.
            await server.DisposeAsync().ConfigureAwait(false);
            return true;
        }

        var heard = _selfCheck.Reaches(server.Port);
        await server.StopAsync().ConfigureAwait(false);

        if (!heard)
        {
            _logger.LogDebug("Queries to 127.0.0.1:{Port} still do not reach the DNS server.", _port);
            return true;
        }

        _logger.LogDebug("Queries to 127.0.0.1:{Port} reach the DNS server again.", _port);
        _unheardAt = null;
        return false;
    }

    /// <summary>An empty plan: a layer that is up is released as a clear releases it.</summary>
    private async Task<ReconcileResult> ReconcileIdleAsync()
    {
        if (_server is null && _backup is null)
        {
            if (_unfinished is { } unfinished)
            {
                return RetryUnfinished(unfinished);
            }

            if (_lookedForLeftover)
            {
                return ReconcileResult.Unchanged(Name);
            }

            _lookedForLeftover = true;
            _backup = _backups.Load();

            if (_backup is null)
            {
                return ReconcileResult.Unchanged(Name);
            }

            _logger.LogInformation("A DNS backup left by an earlier run was found; its interfaces are being put back.");
        }

        var rules = _rules.Count;
        var result = await ReleaseAsync().ConfigureAwait(false);

        return result is { Complete: false }
            ? ReconcileResult.Failed(Name, SettingsRefused)
            : ReconcileResult.Changed(Name, applied: 0, removed: rules + (result?.Restored ?? 0));
    }

    /// <summary>What a release left, tried again under the settings lock. Only adapters present are written to.</summary>
    private ReconcileResult RetryUnfinished(DnsBackup unfinished)
    {
        using var held = _settingsLock.TryAcquire(TimeSpan.Zero);
        if (held is null)
        {
            _logger.LogDebug("A restore from the command line holds the DNS settings; nothing is put back this time.");
            return _unfinishedRefused ? ReconcileResult.Failed(Name, SettingsRefused) : ReconcileResult.Unchanged(Name);
        }

        var result = _restore.RestorePresent(unfinished);

        if (result.Restored > 0)
        {
            Flush();
        }

        _restore.Settle(unfinished, result);
        _unfinished = DnsRestore.Left(unfinished, result);
        _unfinishedRefused = !result.Complete;

        if (!result.Complete)
        {
            return ReconcileResult.Failed(Name, SettingsRefused);
        }

        return result.Restored > 0
            ? ReconcileResult.Changed(Name, applied: 0, removed: result.Restored)
            : ReconcileResult.Unchanged(Name);
    }

    private async Task<ReconcileResult> EnforceAsync(IReadOnlyCollection<SiteRule> sites)
    {
        LoopbackDnsServer? stopAfter = null;

        try
        {
            return EnforceHeld(sites, out stopAfter);
        }
        finally
        {
            // Outside the lock: a mutex is let go on the thread that took it, and this awaits.
            if (stopAfter is not null)
            {
                await stopAfter.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The pass under the settings lock, from the backup check through the last netsh.
    /// Never waits: a command line restore holding the lock means this pass writes nothing.
    /// <paramref name="stopAfter"/> is a server this pass let go of, set even when it throws.
    /// </summary>
    private ReconcileResult EnforceHeld(IReadOnlyCollection<SiteRule> sites, out LoopbackDnsServer? stopAfter)
    {
        stopAfter = null;

        using var held = _settingsLock.TryAcquire(TimeSpan.Zero);
        if (held is null)
        {
            _logger.LogDebug("A restore from the command line holds the DNS settings; this pass writes nothing.");
            return ReconcileResult.Unchanged(Name);
        }

        string? detail = null;

        if (_backup is null)
        {
            _backup = _backups.Load();

            if (_backup is null && _unfinished is not null)
            {
                // Its backup is gone: save again before any netsh, or there is nothing to restore from.
                detail = Persist(_unfinished);
                _backup = _unfinished;
            }

            _unfinished = null;
        }
        else if (!_backups.HoldsACopyOf(GuidsOf(_backup)))
        {
            // No persisted copy names all held interfaces. Saved again before any netsh; throws
            // when neither place takes it, and nothing moves.
            _logger.LogWarning("No DNS backup on this machine covers the interfaces held; it is saved again from memory.");
            detail = Persist(_backup);
        }

        var saved = (_backup?.Interfaces ?? [])
            .GroupBy(state => state.Guid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var added = new List<InterfaceDnsState>();
        var moves = new List<(InterfaceDnsState State, string Secondary)>();

        // The servers each DHCP interface of the backup is handed now, by GUID.
        var handed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        // Adapters present this pass; only they lend the forwarder a server.
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var state in _interfaces.Read())
        {
            present.Add(state.Guid);

            if (saved.TryGetValue(state.Guid, out var original))
            {
                // Never read again: the backup has the original, the interface may have ours.
                var servers = ServersNow(original);
                handed[original.Guid] = servers;

                if (FirstExternal(servers) is { } kept && !IsTaken(state, kept))
                {
                    moves.Add((state, kept));
                }

                continue;
            }

            if (state.Servers.Any(IsLoopback))
            {
                // Points at this machine with no backup: saving it would make 127.0.0.1 the "original".
                if (_reported.Add(state.Guid))
                {
                    _logger.LogWarning(
                        "{Interface} (index {Index}) already points at this machine and there is no backup of it; it is left alone.",
                        state.Name,
                        state.Index);
                }

                continue;
            }

            if (FirstExternal(state.Servers) is not { } own)
            {
                // Without an outside server the machine would resolve nothing once we stop.
                if (_reported.Add(state.Guid))
                {
                    _logger.LogInformation(
                        "{Interface} (index {Index}) has no DNS server outside this machine to keep second; it is left alone.",
                        state.Name,
                        state.Index);
                }

                continue;
            }

            added.Add(state);
            moves.Add((state, own));
        }

        // Only new interfaces are read into it. Saved below, once the server hears itself.
        var merged = added.Count > 0
            ? new DnsBackup(_clock.UtcNow, [.. _backup?.Interfaces ?? [], .. added])
            : _backup;

        var upstream = UpstreamOf(merged, handed, present);
        if (upstream.Count == 0)
        {
            return ReconcileResult.Skipped(Name, NoUpstream);
        }

        var startedNow = false;

        if (_server is null)
        {
            var server = _createServer(_port);

            // The server names the holder in its own log line.
            if (!server.TryStart(out _))
            {
                stopAfter = server;
                return ReconcileResult.Skipped(Name, PortBusy);
            }

            _server = server;
            startedNow = true;
        }

        // Before the first write of this pass: a VPN filter on port 53 would drop what interfaces
        // send here, and the layer would block nothing while holding them.
        if (!HearsItself(_server.Port, holding: !startedNow))
        {
            return Unheard(_server.Port, out stopAfter);
        }

        _unheardAt = null;

        if (added.Count > 0)
        {
            try
            {
                // Before the first netsh. Throws when neither place took it, and then nothing below runs.
                detail = Persist(merged!);
            }
            catch (AggregateException) when (startedNow)
            {
                // A server this pass started goes with the pass.
                stopAfter = _server;
                _server = null;
                throw;
            }

            _backup = merged;
        }

        if (!upstream.SequenceEqual(_forwardingTo))
        {
            _upstream.Use(_createForwarder(upstream));
            _forwardingTo = upstream;
        }

        var (rulesAdded, rulesRemoved) = UseRules(sites);

        var moved = 0;
        var refused = 0;

        foreach (var (state, secondary) in moves)
        {
            if (TryPoint(state, secondary))
            {
                moved++;
            }
            else
            {
                refused++;
            }
        }

        if (moved > 0)
        {
            Flush();
            _logger.LogInformation("Pointed {Count} interfaces at the local DNS resolver.", moved);
        }

        if (refused > 0)
        {
            // The others are done; this one is tried again next pass.
            return ReconcileResult.Failed(Name, SettingsRefused);
        }

        // A server just started counts through its rules: only a release stops it, and that empties them.
        if (moved == 0 && rulesAdded == 0 && rulesRemoved == 0)
        {
            return ReconcileResult.Unchanged(Name) with { Detail = detail };
        }

        return ReconcileResult.Changed(Name, applied: moved + rulesAdded, removed: rulesRemoved) with { Detail = detail };
    }

    /// <summary>Whether our own query comes back. A pass already holding interfaces asks twice, since one datagram may drop.</summary>
    private bool HearsItself(int port, bool holding)
    {
        if (_selfCheck.Reaches(port))
        {
            return true;
        }

        if (!holding)
        {
            return false;
        }

        _logger.LogDebug("A query to 127.0.0.1:{Port} did not come back; asking once more.", port);
        return _selfCheck.Reaches(port);
    }

    /// <summary>
    /// A server that cannot hear itself holds nothing: released under the settings lock, with the
    /// server handed to <paramref name="stopAfter"/> to stop once the lock is let go.
    /// </summary>
    private ReconcileResult Unheard(int port, out LoopbackDnsServer? stopAfter)
    {
        _unheardAt = _clock.UtcNow;

        // Once per episode; the probe then asks at Debug until it is heard again.
        _logger.LogWarning(
            "Queries to 127.0.0.1:{Port} do not reach the DNS server; a VPN or firewall filter is probably dropping them. The DNS layer holds no interfaces.",
            port);

        var result = ReleaseHeld(out stopAfter);

        return result is { Complete: false }
            ? ReconcileResult.Failed(Name, SettingsRefused)
            : ReconcileResult.Skipped(Name, LoopbackBlocked);
    }

    /// <summary>Saves the backup; returns <see cref="BackupOnePlace"/> when only one place took it. Throws when neither did.</summary>
    private string? Persist(DnsBackup backup)
    {
        var where = _backups.Save(backup);
        if (where is DnsBackupSaved.BothPlaces)
        {
            return null;
        }

        _logger.LogWarning(
            "The DNS backup was kept in one place only; the {Missing} did not take it.",
            where is DnsBackupSaved.FileOnly ? "registry" : "file");
        return BackupOnePlace;
    }

    /// <summary>
    /// Restores from the backup, stops the resolver and flushes. Refused or absent interfaces stay
    /// in the backup for idle passes to retry. Null when there was no backup.
    /// </summary>
    private async Task<DnsRestoreResult?> ReleaseAsync()
    {
        var (backup, result) = RestoreHeld();

        await StopServerAsync().ConfigureAwait(false);

        return FinishRelease(backup, result);
    }

    /// <summary>The same release with the server handed back rather than stopped. Does not await.</summary>
    private DnsRestoreResult? ReleaseHeld(out LoopbackDnsServer? stopAfter)
    {
        var (backup, result) = RestoreHeld();

        stopAfter = _server;
        _server = null;

        return FinishRelease(backup, result);
    }

    private (DnsBackup? Backup, DnsRestoreResult? Result) RestoreHeld()
    {
        // What the last release left comes first; it survives when neither stored copy can be read.
        var backup = _backup ?? _unfinished ?? _backups.Load();

        // Restored while the resolver still answers: the interfaces point at it until then.
        return (backup, backup is null ? null : _restore.Restore(backup));
    }

    private DnsRestoreResult? FinishRelease(DnsBackup? backup, DnsRestoreResult? result)
    {
        _handler.UseRules([]);
        _rules = [];
        _upstream.Use(null);
        _forwardingTo = [];

        if (backup is not null && result is not null)
        {
            Flush();

            _restore.Settle(backup, result);
        }

        _unfinished = result is null || backup is null ? null : DnsRestore.Left(backup, result);
        _unfinishedRefused = result is { Failed: > 0 };

        _backup = null;
        _lookedForLeftover = true;
        _reported.Clear();

        return result;
    }

    private async Task StopServerAsync()
    {
        var server = _server;
        _server = null;

        if (server is not null)
        {
            await server.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Hands the handler the new rules when they changed; returns the counts added and removed.</summary>
    private (int Added, int Removed) UseRules(IReadOnlyCollection<SiteRule> sites)
    {
        var desired = sites.ToHashSet();
        var added = desired.Count(rule => !_rules.Contains(rule));
        var removed = _rules.Count(rule => !desired.Contains(rule));

        if (added > 0 || removed > 0)
        {
            _handler.UseRules(sites);
            _rules = desired;
            _logger.LogDebug("The DNS layer now blocks {Count} rules.", desired.Count);
        }

        return (added, removed);
    }

    private bool TryPoint(InterfaceDnsState state, string secondary)
    {
        try
        {
            if (_control.SetStatic(state.Index, [ThisMachine, secondary]))
            {
                _logger.LogDebug("{Interface} (index {Index}) now resolves through this machine.", state.Name, state.Index);
                return true;
            }

            _logger.LogWarning("{Interface} (index {Index}) could not be pointed at this machine.", state.Name, state.Index);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One interface failing must not stop the others.
            _logger.LogWarning(
                exception, "{Interface} (index {Index}) could not be pointed at this machine.", state.Name, state.Index);
            return false;
        }
    }


    private void Flush()
    {
        if (!_cache.Flush())
        {
            _logger.LogDebug("The resolver cache could not be flushed; old answers expire with their TTL.");
        }
    }

    /// <summary>
    /// The servers queries are forwarded to: the outside servers of backed-up adapters present now,
    /// once each; all backed-up adapters only when none is present. Loopback and unspecified are
    /// dropped, since they would be our own socket.
    /// </summary>
    private static List<IPAddress> UpstreamOf(
        DnsBackup? backup, Dictionary<string, IReadOnlyList<string>> now, HashSet<string> present)
    {
        var interfaces = backup?.Interfaces ?? [];

        // An absent adapter's server is likely unreachable and would cost each lookup a timeout.
        var here = ServersOf(interfaces.Where(state => present.Contains(state.Guid)), now);

        return here.Count > 0 ? here : ServersOf(interfaces, now);
    }

    private static List<IPAddress> ServersOf(
        IEnumerable<InterfaceDnsState> interfaces, Dictionary<string, IReadOnlyList<string>> now) =>
        [.. interfaces.SelectMany(state => now.GetValueOrDefault(state.Guid, state.Servers)).Select(External).OfType<IPAddress>().Distinct()];

    private static string[] GuidsOf(DnsBackup backup) => [.. backup.Interfaces.Select(state => state.Guid)];

    /// <summary>
    /// The servers a backed-up interface resolves through now: for a DHCP one, what DHCP hands it
    /// while that has an outside server. The backup is unchanged, so restore still returns it to DHCP.
    /// </summary>
    private IReadOnlyList<string> ServersNow(InterfaceDnsState original)
    {
        if (!original.IsDhcp)
        {
            return original.Servers;
        }

        var now = _interfaces.Handed(original.Guid);

        return FirstExternal(now) is null ? original.Servers : now;
    }

    private static string? FirstExternal(IReadOnlyList<string> servers) =>
        servers.Select(External).OfType<IPAddress>().FirstOrDefault()?.ToString();

    private static IPAddress? External(string server) =>
        IPAddress.TryParse(server, out var address)
            && address.AddressFamily is AddressFamily.InterNetwork
            && !LocalAddress.ReachesThisMachine(address)
                ? address
                : null;

    private static bool IsLoopback(string server) =>
        IPAddress.TryParse(server, out var address) && IPAddress.IsLoopback(address);

    /// <summary>Already ours: 127.0.0.1 first and the saved server second. Anything after is ignored.</summary>
    private static bool IsTaken(InterfaceDnsState state, string secondary) =>
        state.Servers.Count >= 2
            && Same(state.Servers[0], ThisMachine)
            && Same(state.Servers[1], secondary);

    private static bool Same(string server, string expected) =>
        IPAddress.TryParse(server, out var address) && address.Equals(IPAddress.Parse(expected));
}
