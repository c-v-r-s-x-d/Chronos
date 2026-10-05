using System.Collections.Concurrent;
using System.Net;

namespace Chronos.Service.Wfp;

/// <summary>
/// Addresses a block filter must never target, whichever domain resolved to them: loopback,
/// link-local, multicast, the unspecified address (what a filtering DNS answers to refuse a name),
/// RFC 1918 and CGNAT ranges, and the machine's DNS servers and default gateway.
///
/// Static ranges are fixed; DNS servers and the gateway are registered through <see cref="Add"/>
/// at runtime. Instance state, not static, so parallel tests do not collide.
/// </summary>
public sealed class ProtectedAddresses
{
    // No IPv6 RFC 1918 equivalent: unique-local (fc00::/7) is not a universal range, and the IPv6
    // gateway is almost always link-local, which fe80::/10 covers.
    private static readonly IPNetwork[] StaticRanges =
    [
        IPNetwork.Parse("127.0.0.0/8"), // IPv4 loopback
        IPNetwork.Parse("169.254.0.0/16"), // IPv4 link-local
        IPNetwork.Parse("10.0.0.0/8"), // RFC 1918
        IPNetwork.Parse("172.16.0.0/12"), // RFC 1918
        IPNetwork.Parse("192.168.0.0/16"), // RFC 1918
        IPNetwork.Parse("100.64.0.0/10"), // carrier-grade NAT (RFC 6598): the path out through the ISP
        IPNetwork.Parse("0.0.0.0/8"), // "this network" (RFC 1122): what a filtering DNS answers to refuse a name
        IPNetwork.Parse("224.0.0.0/4"), // IPv4 multicast: mDNS, SSDP, local discovery - never a host
        IPNetwork.Parse("::1/128"), // IPv6 loopback
        IPNetwork.Parse("::/128"), // IPv6 unspecified: the same refusal answer in the other family
        IPNetwork.Parse("fe80::/10"), // IPv6 link-local
        IPNetwork.Parse("ff00::/8"), // IPv6 multicast
    ];

    // Concurrent: the reconcile loop reads it every pass while a startup probe writes.
    private readonly ConcurrentDictionary<IPAddress, byte> _runtimeAddresses = new();

    public bool IsProtected(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var normalized = Unmap(address);

        if (_runtimeAddresses.ContainsKey(normalized))
        {
            return true;
        }

        foreach (var range in StaticRanges)
        {
            if (range.Contains(normalized))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Registers an address learned at runtime (DNS server, gateway) as protected for this instance's lifetime.</summary>
    public void Add(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        _runtimeAddresses[Unmap(address)] = 0;
    }

    /// <summary>
    /// Converts an IPv4-mapped IPv6 address to IPv4; the two forms are unequal to
    /// <see cref="IPAddress"/> and would miss the IPv4 ranges. <see cref="AddressPlan"/> uses it too.
    /// </summary>
    internal static IPAddress Unmap(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
