using Chronos.Ipc;

namespace Chronos.Service.Tests;

internal static class TestStatus
{
    /// <summary>A status for tests that only need the reconcile loop to have one.</summary>
    public static StatusPayload Blank() =>
        new("Idle", DateTimeOffset.UnixEpoch, null, null, null, 0, [], [], []);
}
