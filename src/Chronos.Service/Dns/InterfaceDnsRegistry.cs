using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using Chronos.Service.Setup;
using Microsoft.Win32;

namespace Chronos.Service.Dns;

/// <summary>
/// One interface and its configured resolvers, as the registry states them. <see cref="IsDhcp"/> is
/// not derivable from the list: a DHCP interface can have been handed no servers.
/// </summary>
/// <param name="Guid">The adapter's identifier; the name of its key under <see cref="InterfaceDnsRegistry.InterfacesKey"/>.</param>
/// <param name="Index">The IPv4 interface index, which netsh takes to name the adapter.</param>
public sealed record InterfaceDnsState(
    string Guid, int Index, string Name, bool IsDhcp, IReadOnlyList<string> Servers);

public interface IInterfaceDns
{
    /// <summary>Every interface that carries traffic, with the resolvers it resolves through.</summary>
    IReadOnlyList<InterfaceDnsState> Read();

    /// <summary>The IPv4 index of every adapter, by GUID, whether or not it carries traffic. A restore writes through it, since an index can change.</summary>
    IReadOnlyDictionary<string, int> Indexes();

    /// <summary>What DHCP hands the adapter now (DhcpNameServer); Windows keeps it current while a static list is in force.</summary>
    IReadOnlyList<string> Handed(string guid);
}

/// <summary>The DNS settings of the active interfaces, read from the registry.</summary>
/// <remarks>
/// The registry, not <c>netsh show dnsservers</c>, whose output is localized. Adapters are walked
/// (see <see cref="ActiveInterfaces.CarriesTraffic"/>) and the registry consulted per adapter,
/// because many keys under <see cref="InterfacesKey"/> belong to NDIS filter modules.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class InterfaceDnsRegistry : IInterfaceDns
{
    public const string InterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    /// <summary>The static list. Empty or absent means the interface takes DHCP; there is no separate flag.</summary>
    public const string StaticValue = "NameServer";

    public const string DhcpValue = "DhcpNameServer";

    // Windows writes the static list comma-separated and the DHCP list space-separated; either may
    // use either, and treating space as a separator also drops the space left after a comma.
    private static readonly char[] Separators = [',', ' '];

    private readonly RegistryKey _root;
    private readonly string _interfaces;

    public InterfaceDnsRegistry()
        : this(Registry.LocalMachine, InterfacesKey)
    {
    }

    /// <summary>The same reading against another root, for tests.</summary>
    internal InterfaceDnsRegistry(RegistryKey root, string interfaces)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaces);

        _root = root;
        _interfaces = interfaces;
    }

    public IReadOnlyList<InterfaceDnsState> Read() => Read(NetworkInterface.GetAllNetworkInterfaces());

    public IReadOnlyDictionary<string, int> Indexes() => IndexesOf(NetworkInterface.GetAllNetworkInterfaces());

    public IReadOnlyList<string> Handed(string guid) => Split(ReadValues(guid).Dhcp);

    internal static IReadOnlyDictionary<string, int> IndexesOf(IEnumerable<NetworkInterface> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var adapter in adapters)
        {
            try
            {
                // Down adapters too: an adapter that is off is still on the machine.
                if (adapter.GetIPProperties().GetIPv4Properties()?.Index is { } index)
                {
                    indexes.TryAdd(adapter.Id, index);
                }
            }
            catch (NetworkInformationException)
            {
                // Treated as absent by the restore.
            }
        }

        return indexes;
    }

    /// <summary>The same reading over a given adapter list, for tests.</summary>
    internal IReadOnlyList<InterfaceDnsState> Read(IEnumerable<NetworkInterface> adapters) =>
        Describe(adapters, ReadValues);

    internal static IReadOnlyList<InterfaceDnsState> Describe(
        IEnumerable<NetworkInterface> adapters, Func<string, (string? Static, string? Dhcp)> registry)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(registry);

        var described = new List<InterfaceDnsState>();

        foreach (var adapter in adapters)
        {
            if (IndexOf(adapter) is not { } index)
            {
                continue;
            }

            // No key means no resolvers; still recorded, not skipped.
            var (configured, handed) = registry(adapter.Id);

            // DHCP mode is an empty static value. Windows leaves NameServer as an empty string after
            // an interface goes back to DHCP, which must not read as a static list.
            var isDhcp = string.IsNullOrWhiteSpace(configured);

            described.Add(new InterfaceDnsState(
                adapter.Id, index, adapter.Name, isDhcp, Split(isDhcp ? handed : configured)));
        }

        return described;
    }

    /// <summary>The IPv4 index of an adapter this product can take over, or null.</summary>
    private static int? IndexOf(NetworkInterface adapter)
    {
        try
        {
            if (!ActiveInterfaces.CarriesTraffic(adapter, out var properties))
            {
                return null;
            }

            // No IPv4 stack means no index and no key to read. IPv6 resolvers are left alone; the
            // server binds 127.0.0.1 only.
            return properties.GetIPv4Properties()?.Index;
        }
        catch (NetworkInformationException)
        {
            // An adapter whose settings cannot be read has no backup, so it is left alone.
            return null;
        }
    }

    private static IReadOnlyList<string> Split(string? value) =>
        value is null ? [] : value.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The static and DHCP values of one interface's key. Read only; netsh does the writing.</summary>
    private (string? Static, string? Dhcp) ReadValues(string guid)
    {
        using var key = _root.OpenSubKey($@"{_interfaces}\{guid}", writable: false);

        // A non-string value is read as none.
        return (key?.GetValue(StaticValue) as string, key?.GetValue(DhcpValue) as string);
    }
}
