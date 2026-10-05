using System.Net;
using System.Net.NetworkInformation;

namespace Chronos.Service.Tests;

// Adapters as the platform describes them, for the readers that walk them.
internal sealed class FakeAdapter(
    string id,
    string name,
    OperationalStatus status,
    NetworkInterfaceType kind,
    IPInterfaceProperties? properties)
    : NetworkInterface
{
    public override string Id => id;

    public override string Name => name;

    /// <summary>Deliberately not the name: Windows gives an adapter two strings (the user's rename and the driver's), and equal fakes would hide reporting the wrong one.</summary>
    public override string Description => $"{name} driver description";

    public override OperationalStatus OperationalStatus => status;

    public override NetworkInterfaceType NetworkInterfaceType => kind;

    public int PropertiesAsked { get; private set; }

    public override IPInterfaceProperties GetIPProperties()
    {
        PropertiesAsked++;

        return properties ?? throw new NetworkInformationException();
    }

    // Everything below exists because the base class is abstract. Throwing rather than answering, so a
    // test says so if this code starts reading one of them.
    public override long Speed => throw new NotSupportedException();

    public override bool IsReceiveOnly => throw new NotSupportedException();

    public override bool SupportsMulticast => throw new NotSupportedException();

    public override PhysicalAddress GetPhysicalAddress() => throw new NotSupportedException();

    public override bool Supports(NetworkInterfaceComponent networkInterfaceComponent) =>
        throw new NotSupportedException();
}

internal sealed class FakeProperties(UnicastIPAddressInformationCollection unicast, IPv4InterfaceProperties? ipv4)
    : IPInterfaceProperties
{
    public override UnicastIPAddressInformationCollection UnicastAddresses => unicast;

    /// <summary>Null is what Windows answers for an adapter with no IPv4 stack.</summary>
    public override IPv4InterfaceProperties GetIPv4Properties() => ipv4!;

    public override IPv6InterfaceProperties GetIPv6Properties() => throw new NotSupportedException();

    public override IPAddressInformationCollection AnycastAddresses => throw new NotSupportedException();

    public override MulticastIPAddressInformationCollection MulticastAddresses => throw new NotSupportedException();

    public override IPAddressCollection DnsAddresses { get; } = new NoAddresses();

    public override string DnsSuffix => throw new NotSupportedException();

    public override GatewayIPAddressInformationCollection GatewayAddresses => throw new NotSupportedException();

    public override IPAddressCollection DhcpServerAddresses => throw new NotSupportedException();

    public override bool IsDynamicDnsEnabled => throw new NotSupportedException();

    public override bool IsDnsEnabled => throw new NotSupportedException();

    public override IPAddressCollection WinsServersAddresses => throw new NotSupportedException();
}

internal sealed class FakeIPv4(int index) : IPv4InterfaceProperties
{
    public override int Index => index;

    public override bool IsAutomaticPrivateAddressingActive => throw new NotSupportedException();

    public override bool IsAutomaticPrivateAddressingEnabled => throw new NotSupportedException();

    public override bool IsDhcpEnabled => throw new NotSupportedException();

    public override bool IsForwardingEnabled => throw new NotSupportedException();

    public override int Mtu => throw new NotSupportedException();

    public override bool UsesWins => throw new NotSupportedException();
}

/// <summary>How many addresses an interface holds and nothing else: the count is all the filtering asks.</summary>
internal sealed class FakeAddresses(int count) : UnicastIPAddressInformationCollection
{
    public override int Count => count;
}


/// <summary>An adapter handed no resolvers.</summary>
internal sealed class NoAddresses : IPAddressCollection
{
    public override int Count => 0;

    public override IEnumerator<IPAddress> GetEnumerator() => Enumerable.Empty<IPAddress>().GetEnumerator();
}
