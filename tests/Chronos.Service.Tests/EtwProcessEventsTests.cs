using Chronos.Service.Apps;

namespace Chronos.Service.Tests;

public sealed class EtwProcessEventsTests
{
    [Fact]
    public void TheBufferSettingsMeetTheFloorTheSpecificationSets()
    {
        // At least 20 buffers of 64 KB are needed. TraceEvent takes the pool as whole megabytes, so 1.25 MB
        // cannot be expressed and 2 MB is the nearest value that does not fall short. Nothing else here
        // can be tested without a trace session.
        Assert.Equal(64, EtwProcessEvents.BufferQuantumKB);
        Assert.True(EtwProcessEvents.BufferPoolMB * 1024 >= 1280);
    }
}
