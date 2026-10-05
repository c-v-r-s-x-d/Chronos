using Chronos.Service.Sites;

namespace Chronos.Service.Tests;

internal sealed class FakeDnsCache : IDnsCache
{
    public int FlushCount { get; private set; }

    public bool Result { get; set; } = true;

    public bool Flush()
    {
        FlushCount++;
        return Result;
    }
}
