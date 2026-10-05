using System.Net;

namespace Chronos.Service.Wfp;

/// <summary>
/// Fixed list of public DNS-over-HTTPS provider addresses a browser may reach directly, bypassing
/// the system resolver. Covers the built-in DoH providers of Chrome and Firefox: Cloudflare, Google
/// Public DNS and Quad9, two IPv4 and two IPv6 addresses each.
/// </summary>
public static class DohEndpoints
{
    public static IReadOnlyList<IPAddress> All { get; } =
    [
        // Cloudflare (1.1.1.1 / 1.0.0.1)
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("1.0.0.1"),
        IPAddress.Parse("2606:4700:4700::1111"),
        IPAddress.Parse("2606:4700:4700::1001"),

        // Google Public DNS
        IPAddress.Parse("8.8.8.8"),
        IPAddress.Parse("8.8.4.4"),
        IPAddress.Parse("2001:4860:4860::8888"),
        IPAddress.Parse("2001:4860:4860::8844"),

        // Quad9
        IPAddress.Parse("9.9.9.9"),
        IPAddress.Parse("149.112.112.112"),
        IPAddress.Parse("2620:fe::fe"),
        IPAddress.Parse("2620:fe::9"),
    ];
}
