using Chronos.Ipc;

namespace Chronos.App.Services;

/// <summary>What a view model may know about the service: the snapshot, change notice, and a way to send a command. The seam is for tests without a pipe; <see cref="ServiceLink"/> is the one owner.</summary>
public interface IServiceLink
{
    ServiceSnapshot Snapshot { get; }

    event EventHandler<ServiceSnapshot>? Changed;

    /// <summary>An application was closed or a site refused. Separate from <see cref="Changed"/>: the status inside may match the one on screen and the block must still be reported.</summary>
    event EventHandler<BlockEvent>? Blocked;

    /// <summary>Raised before each wait for a new attempt, with the length of that wait.</summary>
    event EventHandler<TimeSpan>? Retrying;

    /// <summary>Answers null when the service could not be reached at all.</summary>
    Task<IpcResponse?> SendAsync(IpcRequest request, CancellationToken ct);
}
