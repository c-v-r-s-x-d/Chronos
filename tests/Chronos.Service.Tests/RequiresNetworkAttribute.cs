using System.Net.NetworkInformation;

namespace Chronos.Service.Tests;

/// <summary>
/// A fact that needs an adapter carrying IPv4 traffic. It skips rather than fails without one.
/// Adapters are asked instead of <see cref="NetworkInterface.GetIsNetworkAvailable"/> because an IPv4 index is what the tests share.
/// For the test that sends a real DNS query this is necessary but not sufficient (a live adapter on an isolated LAN passes);
/// the accurate check is a socket, and opening one in an attribute constructor could hang.
/// </summary>
public sealed class RequiresNetworkAttribute : FactAttribute
{
    public RequiresNetworkAttribute()
    {
        if (!NetworkInterface.GetAllNetworkInterfaces().Any(CarriesIPv4))
        {
            Skip = "Needs an adapter of this machine's own that carries IPv4 traffic.";
        }
    }

    /// <summary>Up, not the loopback, holding an address of its own, numbered for IPv4. Asked here rather than borrowed from the code under test.</summary>
    private static bool CarriesIPv4(NetworkInterface adapter)
    {
        try
        {
            var properties = adapter.GetIPProperties();

            return adapter.OperationalStatus is OperationalStatus.Up
                && adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                && properties.UnicastAddresses.Count > 0
                && properties.GetIPv4Properties() is not null;
        }
        catch (NetworkInformationException)
        {
            // An adapter the platform will not describe is not the adapter being looked for.
            return false;
        }
    }
}
