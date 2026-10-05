using Chronos.App.Services;

namespace Chronos.App.Tests;

internal static class LinkWait
{
    /// <summary>Waits for the link to reach a state; on timeout the message names the state it is in. The wait ends on the transition, not a guessed sleep.</summary>
    public static async Task<ServiceSnapshot> ForAsync(
        IServiceLink link,
        Func<ServiceSnapshot, bool> reached,
        string expected,
        TimeSpan? timeout = null)
    {
        var arrival = new TaskCompletionSource<ServiceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged(object? sender, ServiceSnapshot snapshot)
        {
            if (reached(snapshot))
            {
                arrival.TrySetResult(snapshot);
            }
        }

        link.Changed += OnChanged;

        try
        {
            // Read after subscribing: a state reached in between would otherwise be waited for until the timeout.
            if (reached(link.Snapshot))
            {
                return link.Snapshot;
            }

            var limit = timeout ?? TimeSpan.FromSeconds(20);
            var settled = await Task.WhenAny(arrival.Task, Task.Delay(limit));

            if (!ReferenceEquals(settled, arrival.Task))
            {
                throw new TimeoutException(
                    $"The link never {expected} within {limit}; it is {link.Snapshot.State} " +
                    $"with {(link.Snapshot.Status is null ? "no status" : "a status")}.");
            }

            return await arrival.Task;
        }
        finally
        {
            link.Changed -= OnChanged;
        }
    }
}

/// <summary>Stands in for the pause between reconnect attempts, and records what was asked for.</summary>
internal sealed class RecordedWaits
{
    private readonly List<TimeSpan> _asked = [];

    public IReadOnlyList<TimeSpan> Asked
    {
        get
        {
            lock (_asked)
            {
                return _asked.ToArray();
            }
        }
    }

    public Task NoWaitAsync(TimeSpan duration, CancellationToken ct)
    {
        lock (_asked)
        {
            _asked.Add(duration);
        }

        return Task.CompletedTask;
    }
}
