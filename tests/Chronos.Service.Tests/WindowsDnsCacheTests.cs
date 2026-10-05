using Chronos.Service.Sites;

namespace Chronos.Service.Tests;

public sealed class WindowsDnsCacheTests
{
    [Fact]
    public void Flush_SucceedsWithoutAdministratorRights()
    {
        // DnsFlushResolverCache returns TRUE for a plain user; this catches a future API or platform change before the field, where the symptom is a blocked domain resolving until its TTL.
        Assert.True(new WindowsDnsCache().Flush());
    }
}
