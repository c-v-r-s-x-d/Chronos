namespace Chronos.Service.Sessions;

/// <summary>
/// Guards SessionEngine, which is not thread-safe. Never hold it across an await.
/// </summary>
public sealed class SessionGate
{
    public Lock Sync { get; } = new();
}
