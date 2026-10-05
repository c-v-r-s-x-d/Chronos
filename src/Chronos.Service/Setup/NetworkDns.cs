using System.Diagnostics.CodeAnalysis;
using System.Net.NetworkInformation;

namespace Chronos.Service.Setup;

/// <summary>One active interface and the resolvers it is configured with.</summary>
public sealed record InterfaceDns(string Name, string Kind, IReadOnlyList<string> Servers);

/// <summary>
/// Which interfaces this machine resolves over. Shared by the diagnostic package and the DNS
/// takeover so they agree on the adapter set. They differ on an adapter that cannot be described:
/// the report keeps it as a finding, the takeover drops it (no backup to restore).
/// </summary>
internal static class ActiveInterfaces
{
    /// <summary>
    /// Up, not loopback, and holding a unicast address. Windows lists every NDIS filter module
    /// (WFP MAC layer, QoS scheduler, capture drivers) as an up interface; having an address tells
    /// them apart, since an interface without one carries no DNS query.
    /// </summary>
    /// <exception cref="NetworkInformationException">The platform would not describe the adapter.</exception>
    /// <param name="properties">The adapter's properties, returned so the caller need not ask the IP helper again. Null when false.</param>
    internal static bool CarriesTraffic(
        NetworkInterface adapter, [NotNullWhen(true)] out IPInterfaceProperties? properties)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        properties = null;

        // Order matters: a down or loopback adapter is never asked to describe itself, so it cannot throw.
        if (adapter.OperationalStatus is not OperationalStatus.Up
            || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback)
        {
            return false;
        }

        var described = adapter.GetIPProperties();
        if (described.UnicastAddresses.Count == 0)
        {
            return false;
        }

        properties = described;
        return true;
    }
}

/// <summary>The DNS settings of the interfaces that are up, as the diagnostic package reports them. Read only.</summary>
public static class NetworkDns
{
    /// <summary>Every interface that carries traffic, with the resolvers it hands out.</summary>
    public static IReadOnlyList<InterfaceDns> Active() => Active(NetworkInterface.GetAllNetworkInterfaces());

    /// <summary>Reads a given enumeration of adapters, so a test can compare against the same list.</summary>
    internal static IReadOnlyList<InterfaceDns> Active(IEnumerable<NetworkInterface> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        var active = new List<InterfaceDns>();

        foreach (var adapter in adapters)
        {
            if (Describe(adapter) is { } described)
            {
                active.Add(described);
            }
        }

        return active;
    }

    /// <summary>One interface, or null for one without an address of its own (a filter module).</summary>
    private static InterfaceDns? Describe(NetworkInterface adapter)
    {
        var kind = adapter.NetworkInterfaceType.ToString();

        try
        {
            if (!ActiveInterfaces.CarriesTraffic(adapter, out var properties))
            {
                return null;
            }

            var servers = properties.DnsAddresses.Select(address => address.ToString()).ToArray();

            return new InterfaceDns(adapter.Name, kind, servers);
        }
        catch (NetworkInformationException exception)
        {
            // Keep the adapter as a finding rather than dropping it. The error code, not Message,
            // because Message is localised and the report is English.
            return new InterfaceDns(
                adapter.Name, kind, [$"could not be read: NetworkInformationException {exception.ErrorCode}"]);
        }
    }
}
