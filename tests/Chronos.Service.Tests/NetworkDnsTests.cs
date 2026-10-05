using System.Net.NetworkInformation;
using Chronos.Service.Setup;

namespace Chronos.Service.Tests;

/// <summary>
/// Which interfaces the diagnostic package calls active. Runs against the live machine, compared with one held enumeration so adapters coming and going cannot fail it.
/// </summary>
public sealed class NetworkDnsTests
{
    [Fact]
    public void EveryInterfaceReportedIsUpAndIsNotTheLoopback()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces();

        var reported = Named(NetworkDns.Active(adapters));

        foreach (var adapter in adapters.Where(adapter => reported.Contains(adapter.Name)))
        {
            Assert.Equal(OperationalStatus.Up, adapter.OperationalStatus);
            Assert.NotEqual(NetworkInterfaceType.Loopback, adapter.NetworkInterfaceType);
        }
    }

    [Fact]
    public void TheFilterModulesBoundToAnAdapterAreNotInterfacesOfThisMachine()
    {
        // Windows reports every NDIS filter module (WFP MAC layer, QoS scheduler, capture drivers) as an interface that is up. Each would print "no resolvers configured" and hide the real finding.
        var adapters = NetworkInterface.GetAllNetworkInterfaces();

        var reported = Named(NetworkDns.Active(adapters));
        var carrying = adapters
            .Where(adapter => adapter.OperationalStatus is OperationalStatus.Up
                && adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                && adapter.GetIPProperties().UnicastAddresses.Count > 0)
            .Select(adapter => adapter.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(carrying, reported.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NothingWithoutAnAddressOfItsOwnIsReported()
    {
        // An interface with no unicast address carries no DNS query, so its configuration is not what the machine resolves through.
        var adapters = NetworkInterface.GetAllNetworkInterfaces();

        var reported = Named(NetworkDns.Active(adapters));

        foreach (var adapter in adapters.Where(adapter => reported.Contains(adapter.Name)))
        {
            Assert.NotEmpty(adapter.GetIPProperties().UnicastAddresses);
        }
    }

    [Fact]
    public void TheResolversAreTheOnesThePlatformGivesForThatInterface()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces();

        foreach (var reported in NetworkDns.Active(adapters))
        {
            var adapter = adapters.First(candidate => candidate.Name == reported.Name
                && candidate.GetIPProperties().UnicastAddresses.Count > 0);

            Assert.Equal(
                adapter.GetIPProperties().DnsAddresses.Select(address => address.ToString()),
                reported.Servers);
            Assert.Equal(adapter.NetworkInterfaceType.ToString(), reported.Kind);
        }
    }

    [RequiresNetwork]
    public void AMachineWithANetworkHasAtLeastOneInterfaceToReport()
    {
        // On a machine on a network the section must not be empty: an empty [dns] would read as "no resolvers", a finding about this code rather than the machine.
        Assert.NotEmpty(NetworkDns.Active());
    }

    [Fact]
    public void EachAdapterIsAskedForItsPropertiesOnce()
    {
        // Each ask is a call into the IP helper.
        var adapter = new FakeAdapter(
            "{aaa}",
            "Ethernet",
            OperationalStatus.Up,
            NetworkInterfaceType.Ethernet,
            new FakeProperties(new FakeAddresses(1), new FakeIPv4(7)));

        Assert.Single(NetworkDns.Active([adapter]));

        Assert.Equal(1, adapter.PropertiesAsked);
    }

    private static HashSet<string> Named(IEnumerable<InterfaceDns> reported) =>
        [.. reported.Select(adapter => adapter.Name)];
}
