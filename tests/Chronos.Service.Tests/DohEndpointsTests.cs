using System.Net;
using Chronos.Service.Wfp;

namespace Chronos.Service.Tests;

public sealed class DohEndpointsTests
{
    private static IPAddress Ip(string value) => IPAddress.Parse(value);

    // Guards the catalogue itself: the twelve addresses are written as literals here, not derived
    // from DohEndpoints.All, so a silently dropped or altered entry cannot pass unnoticed.
    private static readonly IPAddress[] Expected =
    [
        Ip("1.1.1.1"),
        Ip("1.0.0.1"),
        Ip("2606:4700:4700::1111"),
        Ip("2606:4700:4700::1001"),
        Ip("8.8.8.8"),
        Ip("8.8.4.4"),
        Ip("2001:4860:4860::8888"),
        Ip("2001:4860:4860::8844"),
        Ip("9.9.9.9"),
        Ip("149.112.112.112"),
        Ip("2620:fe::fe"),
        Ip("2620:fe::9"),
    ];

    [Fact]
    public void All_ContainsExactlyTheTwelveDocumentedAddresses()
    {
        Assert.Equal(Expected.ToHashSet(), DohEndpoints.All.ToHashSet());
    }

    [Fact]
    public void All_HasTwelveDistinctAddresses()
    {
        Assert.Equal(12, DohEndpoints.All.Distinct().Count());
    }

    [Fact]
    public void All_ContainsBothAddressFamilies()
    {
        Assert.Contains(DohEndpoints.All, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        Assert.Contains(DohEndpoints.All, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
    }
}
