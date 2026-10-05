using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using Chronos.Core.Enforcement;
using Chronos.Core.Rules;
using Chronos.Service.Wfp;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Tests;

/// <summary>
/// A filter engine that never touches the platform: it remembers what it was told to add and answers enumeration from that.
/// It also models whether the provider object exists, since the enforcer caches an engine and the platform's objects can go out from under it:
/// <see cref="AddBlock"/> refuses without the provider, as FwpmFilterAdd0 does (FWP_E_PROVIDER_NOT_FOUND, 0x80320005).
/// </summary>
internal sealed class FakeWfpEngine : IWfpEngine
{
    private readonly Dictionary<ulong, WfpFilterInfo> _filters = [];
    private ulong _nextId = 1;

    public List<(IPAddress Address, WfpProtocol Protocol, string Name)> Added { get; } = [];

    public List<ulong> Removed { get; } = [];

    public int EnsureObjectsCalls { get; private set; }

    public int RemoveEverythingCalls { get; private set; }

    /// <summary>How many sessions were closed; a count, since "at least one" would let a leaked handle through.</summary>
    public int DisposeCalls { get; private set; }

    public bool Disposed => DisposeCalls > 0;

    /// <summary>Whether the provider object is on the "platform" right now.</summary>
    public bool ProviderExists { get; private set; }

    /// <summary>The sub-layer, tracked apart from the provider because a removal reports them apart; it is deleted first, so a killed run leaves a provider alone.</summary>
    public bool SubLayerExists { get; private set; }

    public Exception? AddThrows { get; set; }

    /// <summary>Which call to <see cref="AddBlock"/> starts throwing, counting from one.</summary>
    public int AddThrowsFromCall { get; set; } = 1;

    /// <summary>"add" and "remove" in call order, so a test can pin which came first.</summary>
    public List<string> Operations { get; } = [];

    /// <summary>The most filters that existed on this engine at any one instant.</summary>
    public int PeakFilterCount { get; private set; }

    /// <summary>A filter already present before this enforcer ran. The provider is deliberately not seeded with it: that would hide a regression where production never creates the provider.</summary>
    public ulong Seed(string name)
    {
        var id = _nextId++;
        _filters[id] = new WfpFilterInfo(id, name);
        Observe();
        return id;
    }

    /// <summary>What a probe that never opened a session leaves on the machine: the two platform objects and no filter.</summary>
    public void SeedProviderWithoutFilters() => SeedObjects(provider: true, subLayer: true);

    /// <summary>One structural object without the other, as a half-finished run or a hand deletion leaves it. A removal reports the two apart so the machine's actual state can be named.</summary>
    public void SeedObjects(bool provider, bool subLayer)
    {
        ProviderExists = provider;
        SubLayerExists = subLayer;
    }

    /// <summary>A session that opens and then refuses to create the provider: rights for the session but not for the object.</summary>
    public Exception? EnsureObjectsThrows { get; set; }

    public void EnsureObjects()
    {
        EnsureObjectsCalls++;

        if (EnsureObjectsThrows is { } failure)
        {
            throw failure;
        }

        ProviderExists = true;
        SubLayerExists = true;
    }

    public ulong AddBlock(IPAddress address, WfpProtocol protocol, string name)
    {
        if (!ProviderExists)
        {
            throw new InvalidOperationException("FwpmFilterAdd0 failed with 0x80320005.");
        }

        if (AddThrows is { } failure && Added.Count + 1 >= AddThrowsFromCall)
        {
            throw failure;
        }

        Operations.Add("add");
        Added.Add((address, protocol, name));

        var id = _nextId++;
        _filters[id] = new WfpFilterInfo(id, name);
        Observe();
        return id;
    }

    public IReadOnlyList<WfpFilterInfo> ListOwnFilters() => [.. _filters.Values];

    public void RemoveFilters(IEnumerable<ulong> ids)
    {
        Operations.Add("remove");

        foreach (var id in ids)
        {
            Removed.Add(id);
            _filters.Remove(id);
        }
    }

    public WfpRemoval RemoveEverything()
    {
        RemoveEverythingCalls++;

        // The provider and sub-layer go with the filters, leaving a cached engine naming objects that are gone.
        // Reported as removed only when present: deleting an absent object answers FWP_E_PROVIDER_NOT_FOUND or FWP_E_SUBLAYER_NOT_FOUND, read as "nothing to take off".
        var removal = new WfpRemoval(_filters.Count, ProviderExists, SubLayerExists);
        ProviderExists = false;
        SubLayerExists = false;

        Removed.AddRange(_filters.Keys);
        _filters.Clear();
        return removal;
    }

    public void Dispose() => DisposeCalls++;

    private void Observe() => PeakFilterCount = Math.Max(PeakFilterCount, _filters.Count);
}

public sealed class WfpEnforcerTests
{
    private static readonly Guid Session = Guid.Parse("6f2b1d54-9c3a-4e77-8b21-0d5f9a4c1e83");
    private static readonly Guid OtherSession = Guid.Parse("11112222-3333-4444-5555-666677778888");
    private static readonly DateTimeOffset Start = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndsAt = Start.AddHours(2);

    // Documentation ranges (RFC 5737): nothing routed anywhere, and outside every protected range.
    private const string SiteAddress = "93.184.216.34";
    private const string OtherSiteAddress = "198.51.100.5";
    private const string DohAddress = "1.1.1.1"; // one of DohEndpoints.All

    private readonly FakeWfpEngine _engine = new();
    private readonly FakeAddressResolver _resolver = new();
    private readonly ProtectedAddresses _protectedAddresses = new();
    private readonly ServiceTestClock _clock = new(Start);
    private readonly CapturingLogger<WfpEnforcer> _logger = new();

    private List<IPAddress> _infrastructure = [];
    private Func<IReadOnlyList<IPAddress>>? _infrastructureReader;
    private Exception? _engineThrows;

    private int EngineCreations { get; set; }

    // A field rather than a captured argument: the flag is asked for when a session begins, and a test must be able to change the answer after construction.
    private bool _enabled = true;

    /// <summary>How many times the layer asked for the flag; in the service each ask parses the configuration file.</summary>
    private int EnabledReads { get; set; }

    private bool ReadEnabled()
    {
        EnabledReads++;
        return _enabled;
    }

    private WfpEnforcer Create(bool enabled = true)
    {
        _enabled = enabled;
        return CreateEnforcer();
    }

    private WfpEnforcer CreateEnforcer() => new(
        ReadEnabled,
        CreateEngine,
        _resolver,
        _protectedAddresses,
        ReadInfrastructure,
        _clock,
        _logger);

    private IReadOnlyList<IPAddress> ReadInfrastructure() =>
        _infrastructureReader is { } reader ? reader() : _infrastructure;

    private IWfpEngine CreateEngine()
    {
        EngineCreations++;

        if (_engineThrows is { } failure)
        {
            throw failure;
        }

        return _engine;
    }

    private static EnforcementPlan Plan(params string[] domains) => PlanFor(Session, domains);

    private static EnforcementPlan PlanFor(Guid session, params string[] domains) => new(
        session,
        EndsAt,
        [.. domains.Select(domain => new SiteRule(domain, includeSubdomains: true))],
        []);

    // Spelled out rather than taken from the implementation: recovery after a restart reads exactly these names back.
    private static string FilterName(Guid session, string address, string scope) => string.Join(
        '|',
        "Chronos",
        session.ToString("D"),
        EndsAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        address,
        scope);

    private void SeedApplied(Guid session, params (string Address, string Scope)[] filters)
    {
        foreach (var (address, scope) in filters)
        {
            _engine.Seed(FilterName(session, address, scope));
        }
    }

    private void SeedAppliedDohEndpoints(Guid session)
    {
        foreach (var address in DohEndpoints.All)
        {
            _engine.Seed(FilterName(session, address.ToString(), "tcp443"));
            _engine.Seed(FilterName(session, address.ToString(), "udp443"));
        }
    }

    private (IPAddress Address, WfpProtocol Protocol, string Name)[] AddedFor(string address)
    {
        var parsed = IPAddress.Parse(address);
        return [.. _engine.Added.Where(filter => filter.Address.Equals(parsed))];
    }

    /// <summary>Both halves of the engine-failure diagnostic, warning and repeat alike.</summary>
    private (LogLevel Level, string Message)[] EngineEntries() =>
    [
        .. _logger.Entries.Where(entry => entry.Message.Contains("filter engine", StringComparison.Ordinal)),
    ];

    // A layer the user switched off does not merely apply nothing - it never
    // reaches the platform at all, so no engine session, provider or sub-layer is created.
    [Fact]
    public async Task Probe_ReportsDisabledAndCreatesNoPlatformObject()
    {
        var availability = await Create(enabled: false).ProbeAsync(CancellationToken.None);

        Assert.False(availability.IsAvailable);
        Assert.Equal(WfpEnforcer.Disabled, availability.Reason);
        Assert.Equal(0, EngineCreations);
        Assert.Equal(0, _engine.EnsureObjectsCalls);
    }

    // The flag is read when a session begins, not when the service starts. Block lists and durations are re-read on every command;
    // switching L3 off and starting a session without a restart must not still get filters.
    [Fact]
    public async Task Probe_AndReconcile_ReadTheFlagEachTimeRatherThanAtConstruction()
    {
        _resolver.Enqueue(("example.com", [SiteAddress]));
        var enforcer = Create();

        Assert.True((await enforcer.ProbeAsync(CancellationToken.None)).IsAvailable);

        _enabled = false;

        var availability = await enforcer.ProbeAsync(CancellationToken.None);
        var result = await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.False(availability.IsAvailable);
        Assert.Equal(WfpEnforcer.Disabled, availability.Reason);
        Assert.Equal(ReconcileOutcome.Skipped, result.Outcome);
        Assert.Equal(WfpEnforcer.Disabled, result.Detail);
        Assert.Empty(_engine.Added);
    }

    // Once per pass, not twice. The flag comes off disk (ConfigStore.Load parses the file and logs on clamped values), and the coordinator runs
    // ProbeAsync then ReconcileAsync back to back every fifteen seconds, so asking in both adds a second parse and log line for nothing.
    [Fact]
    public async Task Probe_AndReconcile_AskForTheFlagOncePerPass()
    {
        _resolver.Enqueue(("example.com", [SiteAddress]));
        var enforcer = Create();

        await enforcer.ProbeAsync(CancellationToken.None);
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(1, EnabledReads);

        // And a reconcile arriving without a probe before it still asks for itself rather than
        // trusting an answer from the previous pass: the answer is never older than one pass.
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(2, EnabledReads);
    }

    [Fact]
    public async Task Reconcile_SkipsAndCreatesNoPlatformObjectWhenTheLayerIsDisabled()
    {
        var result = await Create(enabled: false).ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Skipped, result.Outcome);
        Assert.Equal(WfpEnforcer.Disabled, result.Detail);
        Assert.Equal(0, EngineCreations);
        Assert.Equal(0, _resolver.Calls);
    }

    [Fact]
    public async Task Probe_ReportsAvailableOnceTheEngineSessionIsOpen()
    {
        var availability = await Create().ProbeAsync(CancellationToken.None);

        Assert.True(availability.IsAvailable);

        // What the probe created is persistent, so every machine running the service would carry a Chronos provider and sub-layer
        // whether or not it ever blocked anything. Available means the session opened, which is all the probe is asked.
        Assert.Equal(0, _engine.EnsureObjectsCalls);
    }

    [Fact]
    public async Task Probe_DoesNotCreatePlatformObjects()
    {
        // The probe runs every 15 seconds whether or not a session exists, and a
        // provider and a sub-layer created there are persistent - so an idle machine that never
        // blocked anything carried two Chronos-named platform objects across every reboot.
        var enforcer = Create();

        await enforcer.ProbeAsync(CancellationToken.None);

        Assert.Equal(0, _engine.EnsureObjectsCalls);
        Assert.False(_engine.ProviderExists);
    }

    [Fact]
    public async Task Reconcile_WithNothingToBlockCreatesNoPlatformObjects()
    {
        var enforcer = Create();

        await enforcer.ProbeAsync(CancellationToken.None);
        var result = await enforcer.ReconcileAsync(EnforcementPlan.Empty, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Unchanged, result.Outcome);
        Assert.Equal(0, _engine.EnsureObjectsCalls);
        Assert.False(_engine.ProviderExists);
    }

    [Fact]
    public async Task Reconcile_WithSomethingToBlockCreatesThePlatformObjectsFirst()
    {
        // The order matters: FwpmFilterAdd0 naming a provider that does not exist is rejected with FWP_E_PROVIDER_NOT_FOUND, which the fake models.
        var enforcer = Create();
        _resolver.Enqueue(("example.com", [SiteAddress]));

        await enforcer.ProbeAsync(CancellationToken.None);
        var result = await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.True(_engine.ProviderExists);
        Assert.NotEmpty(_engine.Added);
    }

    [Fact]
    public async Task Probe_ReportsUnavailableWhenTheEngineCannotBeOpened()
    {
        _engineThrows = new InvalidOperationException("FwpmEngineOpen0 failed with 0x80320005.");

        var availability = await Create().ProbeAsync(CancellationToken.None);

        Assert.False(availability.IsAvailable);
        Assert.Equal(WfpEnforcer.NotAvailable, availability.Reason);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Probe_OpensTheEngineOnlyOnce()
    {
        var enforcer = Create();

        await enforcer.ProbeAsync(CancellationToken.None);
        await enforcer.ProbeAsync(CancellationToken.None);

        Assert.Equal(1, EngineCreations);

        // "Only once" is about the session; no EnsureObjects call is expected, since the probe must not reach the platform on an idle machine.
        Assert.Equal(0, _engine.EnsureObjectsCalls);
    }

    // An address a blocked domain resolved to is closed whole, and the QUIC filter goes on regardless.
    [Fact]
    public async Task Reconcile_ClosesAResolvedAddressOnEveryTransportAndOnQuic()
    {
        _resolver.Enqueue(("example.com", [SiteAddress]));

        await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        var added = AddedFor(SiteAddress);
        Assert.Equal(2, added.Length);
        Assert.Contains(added, filter => filter.Protocol == WfpProtocol.AnyTransport);
        Assert.Contains(added, filter => filter.Protocol == WfpProtocol.QuicUdp443);
    }

    // A DoH endpoint loses port 443 and nothing else.
    [Fact]
    public async Task Reconcile_ClosesADohEndpointOnPortFourFortyThreeOnly()
    {
        _resolver.Enqueue(("example.com", [SiteAddress]));

        await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        var added = AddedFor(DohAddress);
        Assert.Equal(2, added.Length);
        Assert.Contains(added, filter => filter.Protocol == WfpProtocol.Tcp443);
        Assert.Contains(added, filter => filter.Protocol == WfpProtocol.QuicUdp443);
        Assert.DoesNotContain(added, filter => filter.Protocol == WfpProtocol.AnyTransport);
    }

    // Follows from the test above, and is asserted separately because it is the whole reason the
    // DoH block was narrowed: the built-in endpoints are the public DNS servers a machine may be
    // configured with, and closing port 53 on them would cost it name resolution.
    [Fact]
    public async Task Reconcile_LeavesPortFiftyThreeOpenOnEveryDohEndpoint()
    {
        _resolver.Enqueue(("example.com", [SiteAddress]));

        await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        var dohFilters = _engine.Added.Where(filter => DohEndpoints.All.Contains(filter.Address)).ToArray();

        Assert.Equal(DohEndpoints.All.Count * 2, dohFilters.Length);

        // AnyTransport is the only scope that would take port 53 with it; the other two name 443.
        Assert.DoesNotContain(dohFilters, filter => filter.Protocol == WfpProtocol.AnyTransport);
        Assert.All(
            dohFilters,
            filter => Assert.True(filter.Protocol is WfpProtocol.Tcp443 or WfpProtocol.QuicUdp443));
    }

    // The scope belongs to the address. A domain resolving to a built-in DoH endpoint takes the same port-443 pair,
    // never AnyTransport, which would close port 53 on one of the pre-resolver's default servers.
    [Fact]
    public async Task Reconcile_ClosesOnlyPortFourFortyThreeOnADomainResolvingToADohEndpoint()
    {
        _resolver.Enqueue(("one.one.one.one", [DohAddress]));

        await Create().ReconcileAsync(Plan("one.one.one.one"), CancellationToken.None);

        var added = AddedFor(DohAddress);
        Assert.Equal(2, added.Length);
        Assert.Contains(added, filter => filter.Protocol == WfpProtocol.Tcp443);
        Assert.Contains(added, filter => filter.Protocol == WfpProtocol.QuicUdp443);
        Assert.DoesNotContain(added, filter => filter.Protocol == WfpProtocol.AnyTransport);
    }

    // The same rule from the other side: a DoH endpoint the machine also uses as its DNS server
    // still gets its 443 pair, because the protected list does not reach a port-443 filter.
    [Fact]
    public async Task Reconcile_BlocksPortFourFortyThreeOnADohEndpointThatIsAlsoThisMachinesResolver()
    {
        _infrastructure = [IPAddress.Parse(DohAddress)];
        _resolver.Enqueue(("example.com", [SiteAddress]));

        await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        var added = AddedFor(DohAddress);
        Assert.Equal(2, added.Length);
        Assert.Contains(added, filter => filter.Protocol == WfpProtocol.Tcp443);
        Assert.Contains(added, filter => filter.Protocol == WfpProtocol.QuicUdp443);
        Assert.DoesNotContain(added, filter => filter.Protocol == WfpProtocol.AnyTransport);
    }

    [Fact]
    public async Task Reconcile_NamesEveryFilterWithTheSessionAndItsPlannedEnd()
    {
        _resolver.Enqueue(("example.com", [SiteAddress]));

        await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.NotEmpty(_engine.Added);
        Assert.All(_engine.Added, filter =>
        {
            Assert.Contains(Session.ToString("D"), filter.Name, StringComparison.Ordinal);
            Assert.Contains("2026-08-18T14:00:00Z", filter.Name, StringComparison.Ordinal);
        });
    }

    // A second pass over the same set touches nothing and says nothing out loud.
    [Fact]
    public async Task Reconcile_IsUnchangedAndSilentOnASecondPassOverTheSameAddresses()
    {
        _resolver.Enqueue(("example.com", [SiteAddress]));
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        _engine.Added.Clear();
        _engine.Removed.Clear();
        _logger.Clear();

        var second = await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        // The platform first, the reported outcome second: a status alone could claim it does not touch the platform without it being true.
        Assert.Empty(_engine.Added);
        Assert.Empty(_engine.Removed);
        Assert.DoesNotContain(_logger.Entries, entry => entry.Level >= LogLevel.Information);
        Assert.Equal(ReconcileOutcome.Unchanged, second.Outcome);
    }

    [Fact]
    public async Task Reconcile_AddsFiltersOnlyForAddressesThatAreNotYetApplied()
    {
        _resolver.Enqueue(("example.com", [SiteAddress]));
        _resolver.Enqueue(("example.com", [SiteAddress, OtherSiteAddress]));
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        _engine.Added.Clear();
        _engine.Removed.Clear();
        _clock.Advance(TimeSpan.FromMinutes(11));

        var second = await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Changed, second.Outcome);
        Assert.Equal(2, second.Applied);
        Assert.Equal(0, second.Removed);
        Assert.Equal(2, AddedFor(OtherSiteAddress).Length);
        Assert.Empty(AddedFor(SiteAddress));
    }

    [Fact]
    public async Task Reconcile_RemovesTheFiltersOfADomainThePlanNoLongerNames()
    {
        _resolver.Enqueue(("a.example.com", [SiteAddress]), ("b.example.com", [OtherSiteAddress]));
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("a.example.com", "b.example.com"), CancellationToken.None);

        _engine.Added.Clear();
        _engine.Removed.Clear();

        var second = await enforcer.ReconcileAsync(Plan("a.example.com"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Changed, second.Outcome);
        Assert.Equal(2, second.Removed);
        Assert.Empty(_engine.Added);
        Assert.DoesNotContain(
            _engine.ListOwnFilters(),
            filter => filter.Name.Contains(OtherSiteAddress, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reconcile_RemovesEveryFilterWhenThePlanNamesNoSites()
    {
        SeedApplied(Session, (SiteAddress, "all"), (SiteAddress, "udp443"));

        var result = await Create().ReconcileAsync(Plan(), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.Equal(2, result.Removed);
        Assert.Empty(_engine.ListOwnFilters());
        Assert.Equal(0, _resolver.Calls);
    }

    // Every ten minutes, not every reconcile cycle.
    [Fact]
    public async Task Reconcile_ResolvesAgainOnlyOnceEveryTenMinutes()
    {
        _resolver.Enqueue(("example.com", [SiteAddress]));
        var enforcer = Create();

        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(6));
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(2, _resolver.Calls);
    }

    // A rule added mid-session names a domain no pass has ever asked about; waiting out the rest
    // of the ten minutes would leave it unblocked at L3 for up to ten minutes.
    [Fact]
    public async Task Reconcile_ResolvesImmediatelyForADomainItHasNeverSeen()
    {
        _resolver.Enqueue(("a.example.com", [SiteAddress]));
        _resolver.Enqueue(("b.example.com", [OtherSiteAddress]));
        var enforcer = Create();

        await enforcer.ReconcileAsync(Plan("a.example.com"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await enforcer.ReconcileAsync(Plan("a.example.com", "b.example.com"), CancellationToken.None);

        Assert.Equal(2, _resolver.Calls);
        Assert.Equal(2, AddedFor(OtherSiteAddress).Length);
    }

    // A pass that resolves nothing must not take the block off the addresses an earlier pass found (a CDN changing addresses).
    [Fact]
    public async Task Reconcile_KeepsAddressesFromEarlierPassesWhenAResolveComesBackEmpty()
    {
        _resolver.Enqueue(("example.com", [SiteAddress]));
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        _engine.Added.Clear();
        _engine.Removed.Clear();
        _clock.Advance(TimeSpan.FromMinutes(11));

        var second = await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(2, _resolver.Calls);
        Assert.Equal(ReconcileOutcome.Unchanged, second.Outcome);
        Assert.Empty(_engine.Removed);
        Assert.Contains(
            _engine.ListOwnFilters(),
            filter => filter.Name.Contains(SiteAddress, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reconcile_AddsNewlyResolvedAddressesToTheOnesItAlreadyKnows()
    {
        _resolver.Enqueue(("example.com", [SiteAddress]));
        _resolver.Enqueue(("example.com", [OtherSiteAddress]));
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(11));

        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        var names = _engine.ListOwnFilters().Select(filter => filter.Name).ToArray();
        Assert.Contains(names, name => name.Contains(SiteAddress, StringComparison.Ordinal));
        Assert.Contains(names, name => name.Contains(OtherSiteAddress, StringComparison.Ordinal));
    }

    // The cap warning belongs to this layer, because AddressPlan has no logger.
    [Fact]
    public async Task Reconcile_WarnsAboutTheAddressCapNamingTheDomain()
    {
        var many = Enumerable.Range(1, 40).Select(i => $"93.184.216.{i}").ToArray();
        _resolver.Enqueue(("big.example.com", many));

        await Create().ReconcileAsync(Plan("big.example.com"), CancellationToken.None);

        var warning = Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("big.example.com", warning.Message, StringComparison.Ordinal);
    }

    // The record must be visible at the default log level, and Debug is not. A domain from the user's own block list may be logged at any level:
    // it reports nothing about a visit and is already known to whoever reads the log.
    [Fact]
    public async Task Reconcile_RecordsEveryProtectedAddressAtInformationWithItsSourceDomain()
    {
        _resolver.Enqueue(("example.com", ["192.168.1.1", SiteAddress]));

        await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        var entry = Assert.Single(
            _logger.Entries,
            candidate => candidate.Message.Contains("192.168.1.1", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("example.com", entry.Message, StringComparison.Ordinal);
        Assert.Empty(AddedFor("192.168.1.1"));
    }

    // Once per session for each pair of address and domain; the address stays protected all session, so a record on every pass would repeat the same fact every ten minutes.
    [Fact]
    public async Task Reconcile_RecordsAProtectedAddressOncePerAddressAndDomainPerSession()
    {
        _resolver.Enqueue(("example.com", ["192.168.1.1"]));
        _resolver.Enqueue(("example.com", ["192.168.1.1", "10.0.0.7"]));
        var enforcer = Create();

        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);
        _clock.Advance(WfpEnforcer.ResolveInterval);
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(2, _resolver.Calls);
        Assert.Single(
            _logger.Entries,
            entry => entry.Message.Contains("192.168.1.1", StringComparison.Ordinal));
        Assert.Single(
            _logger.Entries,
            entry => entry.Message.Contains("10.0.0.7", StringComparison.Ordinal));
    }

    // Without this, a domain resolving to the machine's own gateway would cut it off the network.
    [Fact]
    public async Task Reconcile_ProtectsTheMachinesDnsServersAndGatewayBeforeResolvingAnything()
    {
        var gateway = IPAddress.Parse("203.0.113.1");
        _infrastructure = [gateway, IPAddress.Parse("203.0.113.2")];
        _resolver.Enqueue(("example.com", ["203.0.113.1", SiteAddress]));

        await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.True(_protectedAddresses.IsProtected(gateway));
        Assert.Empty(AddedFor("203.0.113.1"));
        Assert.Equal(2, AddedFor(SiteAddress).Length);
    }

    // The filters outlive the service, the in-memory map does not, so a fresh enforcer has to recognise the filters from their names alone.
    [Fact]
    public async Task Reconcile_RecognisesTheFiltersAnEarlierRunLeftBehind()
    {
        SeedApplied(Session, (SiteAddress, "all"), (SiteAddress, "udp443"));
        SeedAppliedDohEndpoints(Session);
        _resolver.Enqueue(("example.com", [SiteAddress]));

        var result = await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Unchanged, result.Outcome);
        Assert.Empty(_engine.Added);
        Assert.Empty(_engine.Removed);
    }

    [Fact]
    public async Task Reconcile_AfterARestartDoesNotDoubleTheFilterCount()
    {
        _resolver.Enqueue(("example.com", [SiteAddress]));
        await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);
        var afterFirstRun = _engine.ListOwnFilters().Count;

        _resolver.Enqueue(("example.com", [SiteAddress]));
        var restarted = await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Unchanged, restarted.Outcome);
        Assert.Equal(afterFirstRun, _engine.ListOwnFilters().Count);
    }

    [Fact]
    public async Task Reconcile_RemovesAFilterForAnAddressThePlanNoLongerHolds()
    {
        SeedApplied(Session, (SiteAddress, "all"), (SiteAddress, "udp443"));
        SeedAppliedDohEndpoints(Session);
        var stale = _engine.Seed(FilterName(Session, OtherSiteAddress, "all"));
        _resolver.Enqueue(("example.com", [SiteAddress]));

        var result = await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.Equal([stale], _engine.Removed);
        Assert.Empty(_engine.Added);
    }

    // Our provider, so our filter: a name this version cannot read belongs to an older one, and
    // leaving it would leave a block with nobody to take it off.
    [Fact]
    public async Task Reconcile_RemovesAFilterWhoseNameCannotBeParsed()
    {
        SeedApplied(Session, (SiteAddress, "all"), (SiteAddress, "udp443"));
        SeedAppliedDohEndpoints(Session);
        var strange = _engine.Seed("Chronos block for something");
        _resolver.Enqueue(("example.com", [SiteAddress]));

        var result = await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.Equal([strange], _engine.Removed);
    }

    // One session at a time, so a filter naming another one is left over from a session that is
    // already gone, whatever address it names.
    [Fact]
    public async Task Reconcile_RemovesAFilterBelongingToAnotherSession()
    {
        SeedApplied(Session, (SiteAddress, "all"), (SiteAddress, "udp443"));
        SeedAppliedDohEndpoints(Session);
        var foreign = _engine.Seed(FilterName(OtherSession, SiteAddress, "all"));
        _resolver.Enqueue(("example.com", [SiteAddress]));

        var result = await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.Equal([foreign], _engine.Removed);
        Assert.Empty(_engine.Added);
    }

    [Fact]
    public async Task Reconcile_FailsWhenTheEngineRejectsAFilter()
    {
        _resolver.Enqueue(("example.com", [SiteAddress]));
        _engine.AddThrows = new InvalidOperationException("FwpmFilterAdd0 failed with 0x80320008.");

        var result = await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.Equal(WfpEnforcer.NotAvailable, result.Detail);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Clear_RemovesEverythingTheProviderOwns()
    {
        SeedApplied(Session, (SiteAddress, "all"), (SiteAddress, "udp443"));

        await Create().ClearAsync(CancellationToken.None);

        Assert.Equal(1, _engine.RemoveEverythingCalls);
        Assert.Empty(_engine.ListOwnFilters());
    }

    // A layer that reports itself unavailable must still be able to take back what
    // it applied earlier, and a persistent filter with nobody left to remove it is the worst
    // outcome this layer has.
    [Fact]
    public async Task Clear_RemovesEverythingEvenWhenTheLayerIsDisabled()
    {
        SeedApplied(Session, (SiteAddress, "all"), (SiteAddress, "udp443"));

        await Create(enabled: false).ClearAsync(CancellationToken.None);

        Assert.Equal(1, _engine.RemoveEverythingCalls);
        Assert.Empty(_engine.ListOwnFilters());
    }

    [Fact]
    public async Task Clear_ForgetsTheAddressesLearnedSoFar()
    {
        _resolver.Enqueue(("example.com", [SiteAddress]));
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        await enforcer.ClearAsync(CancellationToken.None);
        _resolver.Enqueue(("example.com", [SiteAddress]));
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(2, _resolver.Calls);
    }

    // RemoveEverything takes the provider and sub-layer with it, and TryEnsureEngine hands a cached engine back without EnsureObjects.
    // An enforcer keeping its engine across a clear would report Available and then fail every add, blocking nothing for a whole cycle.
    [Fact]
    public async Task Reconcile_AfterAClearRebuildsTheProviderWithinTheSameCycle()
    {
        _resolver.Enqueue(("example.com", [SiteAddress]));
        var enforcer = Create();

        var first = await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);
        Assert.Equal(ReconcileOutcome.Changed, first.Outcome);

        await enforcer.ClearAsync(CancellationToken.None);
        Assert.False(_engine.ProviderExists);

        _resolver.Enqueue(("example.com", [SiteAddress]));
        var availability = await enforcer.ProbeAsync(CancellationToken.None);
        var second = await enforcer.ReconcileAsync(
            PlanFor(OtherSession, "example.com"), CancellationToken.None);

        Assert.True(availability.IsAvailable);
        Assert.Equal(ReconcileOutcome.Changed, second.Outcome);
        Assert.True(_engine.ProviderExists);
        Assert.NotEmpty(AddedFor(SiteAddress));
    }

    [Fact]
    public async Task Dispose_ClosesTheEngineItOpened()
    {
        var enforcer = Create();
        await enforcer.ProbeAsync(CancellationToken.None);

        enforcer.Dispose();

        Assert.True(_engine.Disposed);
    }

    [Fact]
    public void Dispose_IsSafeWhenNoEngineWasEverOpened()
    {
        Create(enabled: false).Dispose();

        Assert.Equal(0, EngineCreations);
    }

    // A session opened and then refused the provider: the handle is unreachable the moment
    // TryEnsureEngine returns false, so leaving it open loses one OS handle per pass - and the
    // causes of this failure, no rights or a policy that forbids the object, are the kind that
    // are still there on the next pass and the one after that.
    [Fact]
    public async Task Reconcile_ClosesTheSessionItOpenedWhenTheObjectsCannotBeCreated()
    {
        var enforcer = Create();
        _resolver.Enqueue(("example.com", [SiteAddress]));
        _engine.EnsureObjectsThrows =
            new InvalidOperationException("FwpmProviderAdd0 failed with 0x80070005.");

        // A clear landing in the middle of a pass is the one way the second engine call of a reconcile finds nothing cached: the call that needs the provider opens a session of its own.
        // Driven from the infrastructure read, which runs between the two calls and outside the lock, so the order is fixed.
        _infrastructureReader = () =>
        {
            enforcer.ClearAsync(CancellationToken.None).GetAwaiter().GetResult();
            return [];
        };

        var result = await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        // The same answer an engine that will not open gives: L1 and L2 still stand.
        Assert.Equal(ReconcileOutcome.Skipped, result.Outcome);
        Assert.Equal(WfpEnforcer.NotAvailable, result.Detail);

        // Two sessions opened over the pass, two closed. "At least one" would pass with the
        // second one leaked, which is the whole of what this is about.
        Assert.Equal(2, EngineCreations);
        Assert.Equal(2, _engine.DisposeCalls);
    }

    // The read that fails is the one taken at boot, before the network stack has settled, and the
    // engine is cached from then on. A protection that is only ever attempted once would leave the
    // gateway blockable for the life of the service - with persistent filters, for the life of the
    // machine.
    [Fact]
    public async Task Reconcile_TriesTheInfrastructureReadAgainAfterItFails()
    {
        var gateway = IPAddress.Parse("203.0.113.1");
        var attempts = 0;
        _infrastructureReader = () =>
        {
            attempts++;
            return attempts == 1
                ? throw new NetworkInformationException()
                : (IReadOnlyList<IPAddress>)[gateway];
        };
        _resolver.Enqueue(("example.com", [SiteAddress]));
        _resolver.Enqueue(("example.com", [SiteAddress, "203.0.113.1"]));
        var enforcer = Create();

        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(11));
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.True(_protectedAddresses.IsProtected(gateway));
        Assert.Empty(AddedFor("203.0.113.1"));
    }

    // A Func carries no exception contract, so the reader may throw anything at all; whatever it
    // throws, L1 and L2 must still be applied in full, which they cannot be if
    // ReconcileAsync throws instead of returning a result.
    [Fact]
    public async Task Reconcile_ReturnsAResultWhenTheInfrastructureReadThrowsSomethingUnexpected()
    {
        _infrastructureReader = () => throw new UnauthorizedAccessException("no rights to read adapters");
        _resolver.Enqueue(("example.com", [SiteAddress]));

        var result = await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    // Disposal is final: a disposed enforcer that quietly reopened the engine would hold an OS
    // handle nobody is left to close.
    [Fact]
    public async Task Reconcile_RefusesToReopenTheEngineAfterDisposal()
    {
        var enforcer = Create();
        await enforcer.ProbeAsync(CancellationToken.None);
        enforcer.Dispose();

        var result = await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Skipped, result.Outcome);
        Assert.Equal(WfpEnforcer.NotAvailable, result.Detail);
        Assert.Equal(1, EngineCreations);
    }

    // Clearing a disabled layer must still take its old filters off, but it has no
    // business creating a provider or holding a session open afterwards.
    // Enumerating is keyed by the provider GUID and needs no provider object -
    // verified against the platform by WfpEngineTests.ListingDoesNotNeedTheProviderToExist.
    [Fact]
    public async Task Clear_OnADisabledLayerCreatesNoObjectsAndLeavesNothingOpen()
    {
        SeedApplied(Session, (SiteAddress, "all"), (SiteAddress, "udp443"));

        await Create(enabled: false).ClearAsync(CancellationToken.None);

        Assert.Equal(1, _engine.RemoveEverythingCalls);
        Assert.Equal(0, _engine.EnsureObjectsCalls);
        Assert.True(_engine.Disposed);
    }

    // Two filters can carry one name if an earlier pass died between the add and its own next
    // enumeration. Recording both as applied would leave the second one blocking forever, and it
    // is persistent, so forever means across reboots.
    [Fact]
    public async Task Reconcile_RemovesADuplicateOfAFilterItAlreadyHas()
    {
        SeedApplied(Session, (SiteAddress, "all"), (SiteAddress, "udp443"));
        SeedAppliedDohEndpoints(Session);
        _engine.Seed(FilterName(Session, SiteAddress, "all")); // the same name a second time
        _resolver.Enqueue(("example.com", [SiteAddress]));

        var result = await Create().ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.Single(_engine.Removed);
        Assert.Empty(_engine.Added);
        Assert.Single(_engine.ListOwnFilters(), filter => filter.Name == FilterName(Session, SiteAddress, "all"));
    }

    // Ordering bounds the machine total: the desired set is capped at 2000 filters, so removing before adding stays at 2000 at every instant.
    // Adding first, a service dropping 1900 filters while applying a 200-address plan would peak at 2324.
    [Fact]
    public async Task Reconcile_RemovesBeforeItAddsSoTheFilterCeilingHoldsAtEveryInstant()
    {
        for (var i = 0; i < 1900; i++)
        {
            _engine.Seed(FilterName(Session, $"100.64.{i / 256}.{i % 256}", "all"));
        }

        var domains = Enumerable.Range(0, 10).Select(d => $"d{d}.example.com").ToArray();
        _resolver.Enqueue([.. domains.Select((domain, d) =>
            (domain, Enumerable.Range(0, 20).Select(k => $"203.0.{d}.{k}").ToArray()))]);

        var result = await Create().ReconcileAsync(Plan(domains), CancellationToken.None);

        Assert.Equal(1900, result.Removed);
        Assert.Equal((20 * 10 * 2) + (DohEndpoints.All.Count * 2), result.Applied);

        // Every remove preceding every add is how the ceiling is met.
        Assert.True(
            _engine.PeakFilterCount <= 2000,
            $"the machine held {_engine.PeakFilterCount} filters at once, above the 2000 ceiling");
        Assert.True(
            _engine.Operations.LastIndexOf("remove") < _engine.Operations.IndexOf("add"),
            $"operations ran in the order [{string.Join(", ", _engine.Operations.Distinct())}]");
    }

    // Once per domain per session. A domain over the cap is over it on every
    // resolving pass for the rest of the session, so warning per pass would put the same line in
    // the log every ten minutes without ever saying anything new.
    [Fact]
    public async Task Reconcile_WarnsAboutTheCapOncePerDomainPerSessionRatherThanOnEveryResolvingPass()
    {
        var many = Enumerable.Range(1, 40).Select(i => $"93.184.216.{i}").ToArray();
        _resolver.Enqueue(("big.example.com", many));
        _resolver.Enqueue(("big.example.com", [.. Enumerable.Range(41, 4).Select(i => $"93.184.216.{i}")]));
        var enforcer = Create();

        await enforcer.ReconcileAsync(Plan("big.example.com"), CancellationToken.None);
        _clock.Advance(WfpEnforcer.ResolveInterval);
        await enforcer.ReconcileAsync(Plan("big.example.com"), CancellationToken.None);

        Assert.Equal(2, _resolver.Calls);
        Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    // Above the cap the most recently seen addresses win: accumulation follows a CDN moving its addresses, and keeping the first 32 would freeze the set on addresses the domain stopped using.
    // Ten passes of four new addresses each; the last pass must be blocked.
    [Fact]
    public async Task Reconcile_AboveThePerDomainCapKeepsTheMostRecentlySeenAddresses()
    {
        for (var pass = 0; pass < 10; pass++)
        {
            _resolver.Enqueue((
                "cdn.example.com",
                [.. Enumerable.Range(0, 4).Select(k => $"93.184.{pass}.{k}")]));
        }

        var enforcer = Create();
        for (var pass = 0; pass < 10; pass++)
        {
            await enforcer.ReconcileAsync(Plan("cdn.example.com"), CancellationToken.None);
            _clock.Advance(WfpEnforcer.ResolveInterval);
        }

        Assert.Equal(10, _resolver.Calls);

        // The last pass's four, blocked; the first pass's four, evicted to make room for them.
        foreach (var k in Enumerable.Range(0, 4))
        {
            Assert.Equal(2, AddedFor($"93.184.9.{k}").Length);
        }

        var applied = _engine.ListOwnFilters()
            .Select(filter => filter.Name)
            .ToArray();
        Assert.All(
            Enumerable.Range(0, 4),
            k => Assert.Contains(applied, name => name.Contains($"|93.184.9.{k}|", StringComparison.Ordinal)));
        Assert.All(
            Enumerable.Range(0, 4),
            k => Assert.DoesNotContain(applied, name => name.Contains($"|93.184.0.{k}|", StringComparison.Ordinal)));
    }

    // The strongest evidence that names-as-state works: a pass that dies part way through leaves
    // whatever it managed to file, and the next pass picks up exactly there - no duplicate, no
    // removal, no lost address - because nothing was ever remembered in the first place.
    [Fact]
    public async Task Reconcile_ConvergesAfterAnAddFailedPartWayThrough()
    {
        var addresses = Enumerable.Range(1, 10).Select(i => $"93.184.216.{i}").ToArray();
        var desired = (addresses.Length * 2) + (DohEndpoints.All.Count * 2);
        _resolver.Enqueue(("example.com", addresses));
        _engine.AddThrows = new InvalidOperationException("FwpmFilterAdd0 failed with 0x80320008.");
        _engine.AddThrowsFromCall = 5;
        var enforcer = Create();

        var first = await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Failed, first.Outcome);
        Assert.Equal(4, _engine.ListOwnFilters().Count);

        _engine.AddThrows = null;
        _engine.Removed.Clear();

        var second = await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Changed, second.Outcome);
        Assert.Equal(desired - 4, second.Applied);
        Assert.Equal(0, second.Removed);
        Assert.Empty(_engine.Removed);

        var names = _engine.ListOwnFilters().Select(filter => filter.Name).ToArray();
        Assert.Equal(desired, names.Length);
        Assert.Equal(desired, names.Distinct(StringComparer.Ordinal).Count());
    }

    // Retrying every pass is right, but a cause that will not go away - no rights, no adapter
    // information - would otherwise put a Warning in the log every 15 seconds for the life of the
    // service, about 240 an hour, and bury everything worth reading.
    [Fact]
    public async Task Reconcile_WarnsOnceAboutAnInfrastructureReadThatKeepsFailing()
    {
        _infrastructureReader = () => throw new PlatformNotSupportedException("no adapter information here");
        _resolver.Enqueue(("example.com", [SiteAddress]));
        var enforcer = Create();

        for (var pass = 0; pass < 4; pass++)
        {
            await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);
            _clock.Advance(TimeSpan.FromSeconds(15));
        }

        var entries = _logger.Entries
            .Where(entry => entry.Message.Contains("DNS servers and gateways", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(4, entries.Length);
        Assert.Single(entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(3, entries.Count(entry => entry.Level == LogLevel.Debug));
    }

    // The same throttle on the other diagnostic, and the reason it has to be a separate test:
    // this one is reachable a second time, so it also has to stop suppressing when it should.
    [Fact]
    public async Task Reconcile_WarnsOnceAboutAnEngineThatKeepsFailingToOpen()
    {
        _engineThrows = new InvalidOperationException("FwpmEngineOpen0 failed with 0x80320005.");
        var enforcer = Create();

        for (var pass = 0; pass < 4; pass++)
        {
            await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);
            _clock.Advance(TimeSpan.FromSeconds(15));
        }

        var entries = EngineEntries();

        Assert.Equal(4, entries.Length);
        Assert.Single(entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(3, entries.Count(entry => entry.Level == LogLevel.Debug));
    }

    // An engine that opens is the cause going away, and the failure after it is a new fault
    // rather than the old one still standing. Without the reset the layer would fall silent about
    // every later one for the life of the service - Debug is not written in production.
    [Fact]
    public async Task Reconcile_WarnsAgainAboutAnEngineThatFailsAfterOneThatOpened()
    {
        _engineThrows = new InvalidOperationException("FwpmEngineOpen0 failed with 0x80320005.");
        var enforcer = Create();

        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Single(EngineEntries(), entry => entry.Level == LogLevel.Warning);

        _engineThrows = null;
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        // The clear is what puts the layer back in a position to open an engine again: a cached
        // one is handed out without ever asking the factory, so nothing could fail otherwise.
        await enforcer.ClearAsync(CancellationToken.None);

        _logger.Clear();
        _engineThrows = new InvalidOperationException("FwpmEngineOpen0 failed with 0x80320005.");
        await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        var entry = Assert.Single(EngineEntries());
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    // Silence here is not a small lie: it propagates as ReconcileResult.Cleared, so ConfirmCleared
    // and state.Clear record the session as fully removed while persistent filters stay on the
    // machine and survive the reboot. ClearAsync has no result channel, so it says so by throwing.
    [Fact]
    public async Task Clear_AfterDisposalThrowsRatherThanReportingSuccess()
    {
        SeedApplied(Session, (SiteAddress, "all"), (SiteAddress, "udp443"));
        var enforcer = Create();
        enforcer.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => enforcer.ClearAsync(CancellationToken.None));

        // The decision not to open a session after disposal stands; only the report changes.
        Assert.Equal(0, _engine.RemoveEverythingCalls);
        Assert.Equal(2, _engine.ListOwnFilters().Count);
    }

    // The same rule from the other branch of the same method. An engine that will not open leaves
    // the persistent filters exactly where they are, so answering Cleared has ConfirmCleared and
    // state.Clear record a session as fully removed while the block outlives the reboot. There is
    // no result channel here either, so the failure is reported by letting it out.
    [Fact]
    public async Task Clear_ThrowsRatherThanReportingSuccessWhenTheEngineWillNotOpen()
    {
        _engineThrows = new InvalidOperationException("FwpmEngineOpen0 failed with 0x80320005.");
        var enforcer = Create();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => enforcer.ClearAsync(CancellationToken.None));

        Assert.Contains("0x80320005", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(0, _engine.RemoveEverythingCalls);
    }

    [Fact]
    public async Task Clear_IsRecordedAsFailedWhenTheEngineWillNotOpen()
    {
        _engineThrows = new InvalidOperationException("FwpmEngineOpen0 failed with 0x80320005.");
        var coordinator = new ReconcileCoordinator([Create()]);

        var results = await coordinator.ClearAllAsync(CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.Equal(WfpEnforcer.LayerName, result.EnforcerName);
    }

    [Fact]
    public async Task Clear_AfterDisposalIsRecordedAsFailedRatherThanCleared()
    {
        var enforcer = Create();
        enforcer.Dispose();
        var coordinator = new ReconcileCoordinator([enforcer]);

        var results = await coordinator.ClearAllAsync(CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.Equal(WfpEnforcer.LayerName, result.EnforcerName);
    }

    // Reconciling a disposed enforcer is a programming error but leaves the same Information line as an engine that will not open.
    // Warning, not Error (a cycle racing shutdown is not a failure) and not Debug, which production does not write. No domain or path is named.
    [Fact]
    public async Task Reconcile_AfterDisposalSaysSoInTheLog()
    {
        var enforcer = Create();
        enforcer.Dispose();

        var result = await enforcer.ReconcileAsync(Plan("example.com"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Skipped, result.Outcome);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }
}
