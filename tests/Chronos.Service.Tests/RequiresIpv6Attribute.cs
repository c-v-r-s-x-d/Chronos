using System.Net.Sockets;

namespace Chronos.Service.Tests;

/// <summary>A fact that binds its own IPv6 UDP socket and looks for it in the endpoint table. It skips where the machine has no IPv6 stack.</summary>
public sealed class RequiresIpv6Attribute : FactAttribute
{
    public RequiresIpv6Attribute()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Skip = "Needs an IPv6 stack to bind the endpoint this test looks for.";
        }
    }
}
