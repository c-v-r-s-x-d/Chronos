using System.Net;

namespace Chronos.Service.Wfp;

/// <summary>How much of an address a block closes.</summary>
public enum BlockScope
{
    /// <summary>Every transport and every port.</summary>
    WholeAddress,

    /// <summary>Port 443 only, TCP and QUIC. Closing a DoH endpoint whole would also take port 53 when the machine uses it as its DNS server.</summary>
    Https443,
}

/// <summary>One address to block, the domain that resolved to it, and how far the block reaches.</summary>
public readonly record struct BlockedAddress(IPAddress Address, string Domain, BlockScope Scope);

/// <summary>An address a domain resolved to, and how recently.</summary>
/// <param name="Address">The address itself.</param>
/// <param name="LastSeen">
/// An opaque stamp that grows within a session; larger means seen more recently. Not a clock
/// reading: every address learned on one resolving pass must compare equal.
/// </param>
public readonly record struct SeenAddress(IPAddress Address, long LastSeen);

/// <summary>
/// Something worth reporting about a plan, and its domain. The domain is separate because the
/// text names a growing count and cannot be de-duplicated.
/// </summary>
public readonly record struct AddressPlanWarning(string Domain, string Message);

/// <summary>
/// The addresses <see cref="WfpEnforcer"/> turns into filters, plus warnings raised while building
/// them. Logging level and frequency are the caller's business.
/// </summary>
public sealed record AddressPlan(
    IReadOnlyList<BlockedAddress> Addresses, IReadOnlyList<AddressPlanWarning> Warnings)
{
    // At most 32 addresses per domain and 2000 filters overall. Each address costs two filters
    // (ordinary and QUIC), so the ceiling is 1000 addresses.
    private const int MaxAddressesPerDomain = 32;
    private const int MaxFiltersTotal = 2000;
    private const int MaxAddressesTotal = MaxFiltersTotal / 2;

    // Cannot collide with a real domain, which never contains '<' or '>'.
    internal const string DohSourceLabel = "<doh-endpoint>";

    /// <summary>
    /// Turns resolved domains into a capped address set with scopes from <see cref="ScopeFor"/>,
    /// optionally appending the built-in DoH endpoints. Pure and deterministic regardless of input
    /// order, so reconciling against applied filters causes no churn.
    /// </summary>
    public static AddressPlan Build(
        IReadOnlyDictionary<string, IReadOnlyList<SeenAddress>> resolved,
        bool includeDoh,
        ProtectedAddresses protectedAddresses)
        => Build(resolved, includeDoh, protectedAddresses, MaxAddressesTotal);

    // Internal with the ceiling as a parameter so tests can reach the branch where the DoH reserve fills it.
    internal static AddressPlan Build(
        IReadOnlyDictionary<string, IReadOnlyList<SeenAddress>> resolved,
        bool includeDoh,
        ProtectedAddresses protectedAddresses,
        int maxAddressesTotal)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(protectedAddresses);

        var addresses = new List<BlockedAddress>();
        var warnings = new List<AddressPlanWarning>();
        var totalCapped = false;

        // The twelve built-in DoH endpoints are reserved inside the ceiling before any domain
        // address, so a large block list cannot crowd them out. Domains take what is left.
        var reserved = includeDoh ? DohEndpoints.All.Count : 0;
        var domainCeiling = maxAddressesTotal - reserved;

        // Sorted: Dictionary enumeration order is not guaranteed.
        foreach (var domain in resolved.Keys.OrderBy(domain => domain, StringComparer.Ordinal))
        {
            if (totalCapped)
            {
                break;
            }

            // Grouped on the unmapped form: ::ffff:a.b.c.d and a.b.c.d are one host but unequal
            // IPAddresses, and a mapped form on the V6 layer can never match an IPv4 connection.
            // The group keeps the freshest stamp.
            var candidates = resolved[domain]
                .GroupBy(seen => ProtectedAddresses.Unmap(seen.Address))
                .Select(group => (
                    Address: group.Key,
                    LastSeen: group.Max(seen => seen.LastSeen),
                    Scope: ScopeFor(group.Key, protectedAddresses)))
                .Where(candidate => candidate.Scope is not null)
                .ToList();

            var perDomainCapped = candidates.Count > MaxAddressesPerDomain;

            // Above the cap the most recently seen win, so the set follows a CDN changing addresses.
            // Address breaks ties and survivors are re-sorted, so output does not depend on arrival
            // order; churn would re-create every filter each pass.
            var kept = candidates
                .OrderByDescending(candidate => candidate.LastSeen)
                .ThenBy(candidate => candidate.Address, AddressOrder)
                .Take(MaxAddressesPerDomain)
                .OrderBy(candidate => candidate.Address, AddressOrder)
                .ToList();

            // One address counts once per domain. Deliberate: this can only under-fill the ceiling,
            // and the warning can name the domain that hit it.
            foreach (var candidate in kept)
            {
                if (addresses.Count >= domainCeiling)
                {
                    totalCapped = true;
                    warnings.Add(new AddressPlanWarning(
                        domain,
                        $"Address cap reached while processing domain '{domain}'; "
                        + $"no more than {maxAddressesTotal * 2} filters are created in total"
                        + (reserved > 0
                            ? $", of which {reserved * 2} are reserved for the built-in "
                                + "DNS-over-HTTPS endpoints and are created whatever the domains fill."
                            : ".")));
                    break;
                }

                addresses.Add(new BlockedAddress(candidate.Address, domain, candidate.Scope!.Value));
            }

            // Skipped when the total cap already warned for this domain.
            if (perDomainCapped && !totalCapped)
            {
                warnings.Add(new AddressPlanWarning(
                    domain,
                    $"Domain '{domain}' resolved to {candidates.Count} addresses; "
                    + $"capped at {MaxAddressesPerDomain}."));
            }
        }

        // No cap or protected-list check: room was reserved above, and ScopeFor gives built-in endpoints 443 regardless.
        if (includeDoh)
        {
            foreach (var address in DohEndpoints.All)
            {
                addresses.Add(new BlockedAddress(address, DohSourceLabel, BlockScope.Https443));
            }
        }

        return new AddressPlan(addresses, warnings);
    }

    // A set, since ScopeFor runs per candidate on every pass. Entries are already unmapped.
    private static readonly HashSet<IPAddress> DohAddresses = [.. DohEndpoints.All];

    /// <summary>
    /// How far a block on this address reaches, or <c>null</c> for no filter. Decided by the address
    /// alone: a built-in DoH endpoint always gets 443 only, even when reached by resolving a blocked
    /// domain, so blocking one.one.one.one does not close port 53 on 1.1.1.1. The protected list
    /// does not apply to 443, which carries neither name resolution nor routing. Internal so
    /// <see cref="WfpEnforcer"/> can report why an address got no filter from the same predicate.
    /// </summary>
    internal static BlockScope? ScopeFor(IPAddress address, ProtectedAddresses protectedAddresses)
    {
        var normalized = ProtectedAddresses.Unmap(address);

        if (DohAddresses.Contains(normalized))
        {
            return BlockScope.Https443;
        }

        return protectedAddresses.IsProtected(normalized) ? null : BlockScope.WholeAddress;
    }

    // IPAddress has no total order. Family first, then bytes: arbitrary but fixed, which is all determinism needs.
    private static readonly IComparer<IPAddress> AddressOrder = Comparer<IPAddress>.Create(CompareAddresses);

    private static int CompareAddresses(IPAddress? left, IPAddress? right)
    {
        var familyOrder = ((int)left!.AddressFamily).CompareTo((int)right!.AddressFamily);
        if (familyOrder != 0)
        {
            return familyOrder;
        }

        var leftBytes = left.GetAddressBytes();
        var rightBytes = right.GetAddressBytes();
        for (var i = 0; i < leftBytes.Length; i++)
        {
            var byteOrder = leftBytes[i].CompareTo(rightBytes[i]);
            if (byteOrder != 0)
            {
                return byteOrder;
            }
        }

        return 0;
    }
}
