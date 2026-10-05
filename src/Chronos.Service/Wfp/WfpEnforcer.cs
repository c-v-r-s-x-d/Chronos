using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using Chronos.Core.Enforcement;
using Chronos.Core.Time;
using Chronos.Service.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Wfp;

/// <summary>
/// Layer L3: blocks the addresses the blocked domains resolve to, plus the built-in DoH endpoints,
/// with Windows Filtering Platform filters.
///
/// The filters are persistent and outlive a reboot or service restart, so the applied state is read
/// back from the filter names on every pass; the names are the state.
///
/// <paramref name="isEnabled"/> is asked once per pass, not at construction (so switching L3 off
/// takes effect without a restart) and not per call (it reads from disk).
/// </summary>
public sealed class WfpEnforcer(
    Func<bool> isEnabled,
    Func<IWfpEngine> createEngine,
    IAddressResolver resolver,
    ProtectedAddresses protectedAddresses,
    Func<IReadOnlyList<IPAddress>> readInfrastructureAddresses,
    IClock clock,
    ILogger<WfpEnforcer> logger) : IEnforcer, IDisposable
{
    public const string LayerName = "wfp";

    // Stable codes, not sentences: the reason reaches a bilingual interface. Disabled is the user's choice, NotAvailable a fault.
    public const string Disabled = "wfp.disabled-by-user";
    public const string NotAvailable = "wfp.engine-unavailable";

    private const string Present = "present";

    /// <summary>Addresses are looked up again every ten minutes, not every cycle.</summary>
    internal static readonly TimeSpan ResolveInterval = TimeSpan.FromMinutes(10);

    private readonly Lock _sync = new();

    // Every address seen for a domain this session, with the pass it was last seen on. A union,
    // never a replacement: a pass that resolves nothing must not unblock earlier results. A domain
    // that answered nothing stays present with an empty set, marking it as asked about. The stamp
    // matters only above the per-domain cap, where the freshest addresses win.
    private readonly Dictionary<string, Dictionary<IPAddress, long>> _known = new(StringComparer.Ordinal);

    // Once per domain per session: a domain over the cap stays over it on every pass.
    private readonly HashSet<string> _warnedDomains = new(StringComparer.Ordinal);

    // Once per session per address and domain, or the Information record would repeat every ten minutes.
    private readonly HashSet<(IPAddress Address, string Domain)> _reportedProtected = [];

    // Keyed by a constant signature, not the exception text, so a permanent failure is not re-reported when its wording differs.
    private readonly RepeatedDiagnostic _engineFailure = new();
    private readonly RepeatedDiagnostic _infrastructureFailure = new();

    private IWfpEngine? _engine;

    // Whether the provider and sub-layer exist for the cached engine. False with a non-null engine
    // is an open session with nothing filed, as on an idle machine. Reset in TakeEngine.
    private bool _objectsCreated;

    private long _passSequence;
    private DateTimeOffset? _lastResolvedAt;
    private bool _infrastructureProtected;
    private bool _disposed;

    // The probe's answer, kept for the reconcile that follows in the same pass: asking reads and
    // parses the config file. The reconcile consumes it, so a reconcile without a probe asks itself.
    private bool? _enabledThisPass;

    public string Name => LayerName;

    public Task<EnforcerAvailability> ProbeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var enabled = isEnabled();

        lock (_sync)
        {
            _enabledThisPass = enabled;
        }

        // A disabled layer never reaches the platform: no session, provider or sub-layer.
        if (!enabled)
        {
            return Task.FromResult(EnforcerAvailability.Unavailable(Disabled));
        }

        // A session only: it answers whether the layer could act without leaving anything on the machine.
        return Task.FromResult(TryEnsureEngine(createObjects: false, out _)
            ? EnforcerAvailability.Available
            : EnforcerAvailability.Unavailable(NotAvailable));
    }

    public async Task<ReconcileResult> ReconcileAsync(EnforcementPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();

        bool? probed;

        lock (_sync)
        {
            probed = _enabledThisPass;
            _enabledThisPass = null;
        }

        // Outside the lock: asking parses a file.
        if (!(probed ?? isEnabled()))
        {
            return ReconcileResult.Skipped(Name, Disabled);
        }

        // Session before the resolve, objects after it: no lookup is wasted when the engine will
        // not open, and the provider and sub-layer wait until an address needs them.
        if (!TryEnsureEngine(createObjects: false, out var engine))
        {
            // Same answer as ProbeAsync; L1 and L2 still apply.
            return ReconcileResult.Skipped(Name, NotAvailable);
        }

        // Before the resolve, so a domain answering with the machine's gateway never reaches a
        // filter. Retried until it works: the read at boot may fail before the network stack settles.
        ProtectInfrastructureAddresses();

        var domains = plan.Sites
            .Select(site => site.Domain)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var resolved = await RefreshIfDueAsync(plan, domains, ct).ConfigureAwait(false);

        // DoH endpoints are blocked only while there is a site rule.
        var includeDoh = domains.Count > 0;
        var addressPlan = AddressPlan.Build(KnownFor(domains), includeDoh, protectedAddresses);

        if (resolved)
        {
            // Only after a pass that asked, the only one where the address set can change.
            ReportWarnings(addressPlan);
            ReportProtectedAddresses(domains);
        }

        // Only an address to block needs the provider and sub-layer. With none, the pass still
        // removes leftover filters through the session alone: they imply the provider exists.
        if (addressPlan.Addresses.Count > 0 && !TryEnsureEngine(createObjects: true, out engine))
        {
            return ReconcileResult.Skipped(Name, NotAvailable);
        }

        try
        {
            return ApplyDelta(engine, plan, addressPlan);
        }
        catch (InvalidOperationException exception)
        {
            // Report the stable code, not an exception sentence. The engine is dropped and reopened next pass.
            logger.LogWarning(exception, "The address filters could not be brought in line with the plan.");
            DropEngine();
            return ReconcileResult.Failed(Name, NotAvailable, exception);
        }
    }

    public Task ClearAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            _known.Clear();
            _warnedDomains.Clear();
            _reportedProtected.Clear();
            _lastResolvedAt = null;
        }

        // Neither availability nor the enabled flag is consulted: a disabled or unavailable layer
        // must still take back what it applied. An ownerless persistent filter is the worst outcome.
        IWfpEngine? cached;

        lock (_sync)
        {
            // Throw rather than return: a silent return reaches the coordinator as Cleared while
            // persistent filters stay on the machine. ClearAllAsync catches this and records Failed.
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(WfpEnforcer));
            }

            cached = _engine;
        }

        var engine = cached;

        if (engine is null)
        {
            try
            {
                // No EnsureObjects here. Enumeration is keyed by provider GUID and succeeds with no
                // provider present, returning nothing; a filter cannot exist without its provider
                // (FWP_E_PROVIDER_NOT_FOUND). So a disabled layer clears without creating platform objects.
                engine = createEngine();
            }
            catch (Exception exception)
            {
                // Without a session handle earlier filters stay (`chronos clean` removes them). Throw
                // rather than report Cleared, as in the disposed branch; ClearAllAsync records Failed.
                LogEngineFailure(exception);
                throw;
            }
        }

        try
        {
            var removal = engine.RemoveEverything();
            if (removal.Filters > 0)
            {
                logger.LogInformation("Removed {Count} address filters.", removal.Filters);
            }
        }
        finally
        {
            // On both paths. RemoveEverything deletes the provider and sub-layer, and a cached engine
            // is trusted to have them (_objectsCreated), so keeping it would reject every add with
            // FWP_E_PROVIDER_NOT_FOUND while the probe says Available.
            //
            // `chronos clean` runs in its own process and does not pass here; a service engine made
            // stale by it is dropped by the catch in ReconcileAsync, costing one cycle of rejected adds.
            //
            // This drops whatever is cached, which in a race may be another reconcile's engine; the
            // next pass reopens, and that engine is stale anyway.
            DropEngine();

            // A session opened only to clear is closed here; DropEngine already disposed the cached one.
            if (cached is null)
            {
                engine.Dispose();
            }
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        IWfpEngine? engine;

        lock (_sync)
        {
            _disposed = true;
            engine = TakeEngine();
        }

        engine?.Dispose();
    }

    /// <summary>The DNS servers of every interface that is up, and every default gateway.</summary>
    public static IReadOnlyList<IPAddress> SystemInfrastructureAddresses()
    {
        var addresses = new List<IPAddress>();

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            var properties = adapter.GetIPProperties();
            addresses.AddRange(properties.DnsAddresses);
            addresses.AddRange(properties.GatewayAddresses.Select(gateway => gateway.Address));
        }

        return addresses;
    }

    /// <summary>
    /// Opens the engine session, and creates the provider and sub-layer only when a filter is about
    /// to be added. Opening a session creates nothing; EnsureObjects creates persistent objects, so
    /// the probe asks for a session only, or an idle machine would carry them across reboots.
    /// </summary>
    private bool TryEnsureEngine(bool createObjects, [NotNullWhen(true)] out IWfpEngine? engine)
    {
        bool disposed;
        IWfpEngine? cached = null;
        var cachedNeedsObjects = false;

        lock (_sync)
        {
            disposed = _disposed;

            if (!disposed && _engine is not null)
            {
                cached = _engine;
                cachedNeedsObjects = createObjects && !_objectsCreated;
            }
        }

        if (cached is not null)
        {
            return TryUseCachedEngine(cached, cachedNeedsObjects, out engine);
        }

        if (disposed)
        {
            // Disposal is final: reopening would leak a handle. The skip alone looks like an engine
            // that will not open, so it is logged. Warning, since a cycle racing shutdown is not a
            // failure; production does not write Debug. No throttle: the loop stops after disposal.
            logger.LogWarning("The address layer was asked to reconcile after it was disposed.");
            engine = null;
            return false;
        }

        IWfpEngine? opened = null;
        IWfpEngine created;
        try
        {
            // Whoever builds the engine opens the session; asking only when needed keeps a disabled layer off the platform.
            created = opened = createEngine();

            if (createObjects)
            {
                created.EnsureObjects();
            }
        }
        catch (Exception exception)
        {
            // Not cached yet, so a session whose EnsureObjects threw would leak a handle every pass.
            opened?.Dispose();
            LogEngineFailure(exception);
            engine = null;
            return false;
        }

        IWfpEngine? surplus;

        lock (_sync)
        {
            surplus = _engine is null ? null : created;
            _engine ??= created;
            engine = _engine;

            if (createObjects)
            {
                // Set even when this engine is the surplus one: the objects belong to the platform, not a session.
                _objectsCreated = true;
            }

            // Reset, so the next failure is news again.
            _engineFailure.IsNews(null);
        }

        surplus?.Dispose();

        return true;
    }

    /// <summary>Returns the cached engine, first creating the platform objects if this pass needs them. EnsureObjects runs outside the lock.</summary>
    private bool TryUseCachedEngine(IWfpEngine cached, bool needed, [NotNullWhen(true)] out IWfpEngine? engine)
    {
        if (!needed)
        {
            engine = cached;
            return true;
        }

        try
        {
            cached.EnsureObjects();
        }
        catch (Exception exception)
        {
            // Every AddBlock through this session would be rejected: drop it and reopen next pass.
            LogEngineFailure(exception);
            DropEngine();
            engine = null;
            return false;
        }

        lock (_sync)
        {
            // Only while still cached: a concurrent clear removed the objects and reset the flag.
            if (ReferenceEquals(_engine, cached))
            {
                _objectsCreated = true;
            }
        }

        engine = cached;
        return true;
    }

    private void ProtectInfrastructureAddresses()
    {
        lock (_sync)
        {
            if (_infrastructureProtected)
            {
                return;
            }
        }

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = readInfrastructureAddresses();
        }
        catch (Exception exception)
        {
            // Every exception, deliberately: a Func has no exception contract, and ReconcileAsync
            // must return a result so L1 and L2 still apply. Static ranges still protect; the next
            // pass retries quietly.
            LogInfrastructureFailure(exception);
            return;
        }

        foreach (var address in addresses)
        {
            protectedAddresses.Add(address);
        }

        lock (_sync)
        {
            _infrastructureProtected = true;
        }

        logger.LogDebug("Protected {Count} DNS server and gateway addresses of this machine.", addresses.Count);
    }

    /// <summary>Resolves when due and merges the answer in; true when this pass actually asked.</summary>
    private async Task<bool> RefreshIfDueAsync(
        EnforcementPlan plan, IReadOnlyList<string> domains, CancellationToken ct)
    {
        if (domains.Count == 0)
        {
            lock (_sync)
            {
                _known.Clear();
                _warnedDomains.Clear();
                _reportedProtected.Clear();
            }

            return false;
        }

        bool due;

        lock (_sync)
        {
            foreach (var gone in _known.Keys.Where(domain => !domains.Contains(domain, StringComparer.Ordinal)).ToList())
            {
                _known.Remove(gone);
                _warnedDomains.Remove(gone);
                _reportedProtected.RemoveWhere(entry => string.Equals(entry.Domain, gone, StringComparison.Ordinal));
            }

            // A new domain cannot wait out the interval, or a mid-session rule stays unblocked here for up to ten minutes.
            due = _lastResolvedAt is not { } last
                || clock.UtcNow - last >= ResolveInterval
                || domains.Any(domain => !_known.ContainsKey(domain));
        }

        if (!due)
        {
            return false;
        }

        IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> answer;
        try
        {
            answer = await resolver.ResolveAsync(plan.Sites, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Same as an empty answer: known addresses stay blocked.
            logger.LogWarning(exception, "The pre-resolver pass failed; the addresses already known are kept.");
            answer = new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.Ordinal);
        }

        lock (_sync)
        {
            // One stamp per pass: its addresses are equally recent, and the cap breaks ties by address.
            var stamp = ++_passSequence;

            foreach (var domain in domains)
            {
                if (!_known.TryGetValue(domain, out var addresses))
                {
                    addresses = [];
                    _known[domain] = addresses;
                }

                if (answer.TryGetValue(domain, out var found))
                {
                    foreach (var address in found)
                    {
                        addresses[address] = stamp;
                    }
                }
            }

            // Set even when nothing resolved, so an unresolvable domain is retried on the interval, not every cycle.
            _lastResolvedAt = clock.UtcNow;
        }

        return true;
    }

    private IReadOnlyDictionary<string, IReadOnlyList<SeenAddress>> KnownFor(IReadOnlyList<string> domains)
    {
        var result = new Dictionary<string, IReadOnlyList<SeenAddress>>(StringComparer.Ordinal);

        lock (_sync)
        {
            foreach (var domain in domains)
            {
                if (_known.TryGetValue(domain, out var addresses) && addresses.Count > 0)
                {
                    result[domain] = [.. addresses.Select(entry => new SeenAddress(entry.Key, entry.Value))];
                }
            }
        }

        return result;
    }

    private void ReportWarnings(AddressPlan addressPlan)
    {
        foreach (var warning in addressPlan.Warnings)
        {
            bool first;

            lock (_sync)
            {
                first = _warnedDomains.Add(warning.Domain);
            }

            // Warning with the domain, once per domain per session.
            if (first)
            {
                logger.LogWarning("{Warning}", warning.Message);
            }
        }
    }

    private void ReportProtectedAddresses(IReadOnlyList<string> domains)
    {
        foreach (var (domain, addresses) in KnownFor(domains))
        {
            // Same predicate as the plan, so an address that got a filter (such as a DoH endpoint
            // that is also the DNS server) is never reported as protected.
            foreach (var address in addresses
                .Select(seen => seen.Address)
                .Where(address => AddressPlan.ScopeFor(address, protectedAddresses) is null))
            {
                bool first;

                lock (_sync)
                {
                    first = _reportedProtected.Add((address, domain));
                }

                // Information, once per address and domain. The domain is from the user's own block
                // list, not learned from an attempt to reach it.
                if (first)
                {
                    logger.LogInformation(
                        "Address {Address} of {Domain} is protected; no filter is created.", address, domain);
                }
            }
        }
    }

    private ReconcileResult ApplyDelta(IWfpEngine engine, EnforcementPlan plan, AddressPlan addressPlan)
    {
        var desired = new HashSet<FilterKey>();
        foreach (var entry in addressPlan.Addresses)
        {
            foreach (var protocol in ProtocolsFor(entry.Scope))
            {
                desired.Add(new FilterKey(entry.Address, protocol));
            }
        }

        var applied = new Dictionary<FilterKey, ulong>();
        var surplus = new List<ulong>();

        foreach (var filter in engine.ListOwnFilters())
        {
            // Filters under our provider are ours. An unreadable name is removed, or the block would stand with no one able to remove it.
            if (!WfpFilterName.TryParse(filter.Name, out var parsed)
                || parsed.SessionId != plan.SessionId
                || !desired.Contains(parsed.Key)
                || !applied.TryAdd(parsed.Key, filter.Id))
            {
                surplus.Add(filter.Id);
            }
        }

        var missing = desired.Where(key => !applied.ContainsKey(key)).ToList();

        if (surplus.Count == 0 && missing.Count == 0)
        {
            return ReconcileResult.Unchanged(Name);
        }

        // Removals first: they free room under the filter ceiling.
        if (surplus.Count > 0)
        {
            engine.RemoveFilters(surplus);
        }

        foreach (var key in missing)
        {
            engine.AddBlock(key.Address, key.Protocol, WfpFilterName.Format(plan, key));
        }

        logger.LogDebug("The address layer now holds {Count} filters.", desired.Count);

        return ReconcileResult.Changed(Name, applied: missing.Count, removed: surplus.Count);
    }

    private static WfpProtocol[] ProtocolsFor(BlockScope scope) => scope switch
    {
        // Port 443 only: closing a DoH endpoint whole would close port 53, which the machine and
        // the pre-resolver and forwarder may use.
        BlockScope.Https443 => [WfpProtocol.Tcp443, WfpProtocol.QuicUdp443],

        // The QUIC filter may be redundant, since the address-only filter has no protocol condition.
        // Unverified that it blocks UDP at ALE_AUTH_CONNECT (only TCP was confirmed), so it is kept
        // at the cost of halving the address ceiling.
        _ => [WfpProtocol.AnyTransport, WfpProtocol.QuicUdp443],
    };

    private void DropEngine()
    {
        IWfpEngine? engine;

        lock (_sync)
        {
            engine = TakeEngine();
        }

        engine?.Dispose();
    }

    /// <summary>Hands the engine over to the caller, which disposes it outside the lock.</summary>
    private IWfpEngine? TakeEngine()
    {
        var engine = _engine;
        _engine = null;

        // Every route that lets go of the provider and sub-layer (clear, failure, disposal) ends
        // here, so the flag cannot stay up over deleted objects. A `chronos clean` in another
        // process does not pass here; see ClearAsync.
        _objectsCreated = false;

        return engine;
    }

    // Warning once, Debug after: both run every cycle, and a permanent cause would flood the log every 15 seconds.
    private void LogInfrastructureFailure(Exception exception)
    {
        // No reset: _infrastructureProtected latches on first success, so this is not reached again.
        if (_infrastructureFailure.IsNews(Present))
        {
            logger.LogWarning(exception, "The machine's DNS servers and gateways could not be read.");
        }
        else
        {
            logger.LogDebug(exception, "The machine's DNS servers and gateways still cannot be read.");
        }
    }

    private void LogEngineFailure(Exception exception)
    {
        if (_engineFailure.IsNews(Present))
        {
            logger.LogWarning(exception, "The filter engine could not be opened; the address layer is off.");
        }
        else
        {
            logger.LogDebug(exception, "The filter engine still cannot be opened.");
        }
    }
}

internal readonly record struct FilterKey(IPAddress Address, WfpProtocol Protocol);

/// <summary>
/// The display name of a filter, which is also the whole of this layer's persisted state.
/// Format: <c>Chronos|session|end|address|scope</c>.
/// </summary>
internal sealed record WfpFilterName(Guid SessionId, FilterKey Key)
{
    private const char Separator = '|';
    private const string Prefix = "Chronos";
    private const string EndFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const string NoEnd = "open-ended";

    public static string Format(EnforcementPlan plan, FilterKey key) => string.Join(
        Separator,
        Prefix,
        plan.SessionId.ToString("D"),
        plan.EndsAt is { } endsAt ? endsAt.UtcDateTime.ToString(EndFormat, CultureInfo.InvariantCulture) : NoEnd,
        key.Address.ToString(),
        ScopeToken(key.Protocol));

    public static bool TryParse(string name, [NotNullWhen(true)] out WfpFilterName? result)
    {
        result = null;

        var parts = name.Split(Separator);
        if (parts.Length != 5 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        // parts[2], the planned end, is for people reading the filter list and is never read back.
        if (!Guid.TryParseExact(parts[1], "D", out var sessionId)
            || !IPAddress.TryParse(parts[3], out var address)
            || !TryProtocol(parts[4], out var protocol))
        {
            return false;
        }

        result = new WfpFilterName(sessionId, new FilterKey(address, protocol));
        return true;
    }

    private static string ScopeToken(WfpProtocol protocol) => protocol switch
    {
        WfpProtocol.Tcp443 => "tcp443",
        WfpProtocol.QuicUdp443 => "udp443",
        _ => "all",
    };

    private static bool TryProtocol(string token, out WfpProtocol protocol)
    {
        switch (token)
        {
            case "all":
                protocol = WfpProtocol.AnyTransport;
                return true;
            case "tcp443":
                protocol = WfpProtocol.Tcp443;
                return true;
            case "udp443":
                protocol = WfpProtocol.QuicUdp443;
                return true;
            default:
                protocol = default;
                return false;
        }
    }
}
