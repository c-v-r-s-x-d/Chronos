using System.Threading.Channels;
using Chronos.Ipc;

namespace Chronos.Service.Ipc;

/// <summary>
/// Fans events out to the listening connections, one queue per subscriber so a slow reader
/// cannot hold up the rest.
/// </summary>
public sealed class EventBus
{
    /// <summary>
    /// How far a subscriber may fall behind before it loses the oldest events. A lost
    /// <see cref="IpcEventKind.StatusChanged"/> is harmless; a lost block event costs one screen.
    /// Accepted, because the alternative is a publisher that waits.
    /// </summary>
    private const int QueueCapacity = 64;

    private readonly Lock _sync = new();
    private readonly List<Channel<IpcEvent>> _subscribers = [];

    public int SubscriberCount
    {
        get
        {
            lock (_sync)
            {
                return _subscribers.Count;
            }
        }
    }

    public IDisposable Subscribe(out ChannelReader<IpcEvent> events)
    {
        // DropOldest means TryWrite in Publish can neither fail nor wait.
        var queue = Channel.CreateBounded<IpcEvent>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        lock (_sync)
        {
            _subscribers.Add(queue);
        }

        events = queue.Reader;

        return new Subscription(this, queue);
    }

    /// <summary>Never blocks and never throws, whatever the subscribers are doing.</summary>
    public void Publish(IpcEvent published)
    {
        ArgumentNullException.ThrowIfNull(published);

        Channel<IpcEvent>[] targets;

        // Copied under the lock, written outside it.
        lock (_sync)
        {
            if (_subscribers.Count == 0)
            {
                return;
            }

            targets = [.. _subscribers];
        }

        foreach (var target in targets)
        {
            target.Writer.TryWrite(published);
        }
    }

    private void Remove(Channel<IpcEvent> queue)
    {
        lock (_sync)
        {
            _subscribers.Remove(queue);
        }
    }

    private sealed class Subscription(EventBus bus, Channel<IpcEvent> queue) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            bus.Remove(queue);

            // Complete the writer so a connection task waiting on the reader stops and frees its slot.
            queue.Writer.TryComplete();
        }
    }
}
