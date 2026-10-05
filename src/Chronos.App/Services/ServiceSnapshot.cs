using Chronos.Ipc;

namespace Chronos.App.Services;

public enum ServiceConnectionState
{
    /// <summary>Nothing has been asked yet. Unknown and unavailable are different answers.</summary>
    Connecting,

    Available,

    Unavailable,
}

/// <summary>What the interface may believe about the service right now. A status exists only alongside <see cref="ServiceConnectionState.Available"/>, so nothing stale can be shown.</summary>
public sealed record ServiceSnapshot
{
    private ServiceSnapshot(ServiceConnectionState state, StatusPayload? status)
    {
        State = state;
        Status = status;
    }

    public ServiceConnectionState State { get; }

    public StatusPayload? Status { get; }

    public static ServiceSnapshot Connecting { get; } = new(ServiceConnectionState.Connecting, null);

    public static ServiceSnapshot Unavailable { get; } = new(ServiceConnectionState.Unavailable, null);

    public static ServiceSnapshot Available(StatusPayload status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return new ServiceSnapshot(ServiceConnectionState.Available, status);
    }
}
