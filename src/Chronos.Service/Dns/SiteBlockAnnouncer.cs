using Chronos.Ipc;
using Chronos.Service.Ipc;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Dns;

/// <summary>
/// The sink behind <see cref="AttemptNotices"/>: a refused site becomes a
/// <see cref="IpcEventKind.SiteBlocked"/> event on the bus.
/// </summary>
/// <remarks>
/// Runs on the resolver's answer path, so it never throws or waits. The status is built under the
/// session gate, hence on the thread pool. The domain is logged at Debug only.
/// </remarks>
public sealed class SiteBlockAnnouncer
{
    /// <summary>How many announcements may wait for a thread at once. Past it an attempt is dropped.</summary>
    public const int MaxPending = 16;

    private readonly EventBus _events;
    private readonly Func<StatusPayload> _status;
    private readonly ILogger<SiteBlockAnnouncer> _logger;
    private readonly Action<Action> _schedule;

    private int _pending;

    /// <param name="schedule">Where the work runs; the thread pool unless a test says otherwise.</param>
    public SiteBlockAnnouncer(
        EventBus events,
        Func<StatusPayload> status,
        ILogger<SiteBlockAnnouncer> logger,
        Action<Action>? schedule = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(logger);

        _events = events;
        _status = status;
        _logger = logger;
        _schedule = schedule ?? (static work => ThreadPool.QueueUserWorkItem(static run => run(), work, preferLocal: false));
    }

    public void Announce(string domain)
    {
        if (_events.SubscriberCount == 0)
        {
            return;
        }

        if (Interlocked.Increment(ref _pending) > MaxPending)
        {
            Interlocked.Decrement(ref _pending);
            _logger.LogDebug("The notice of a blocked attempt at {Domain} was dropped: too many are waiting.", domain);
            return;
        }

        try
        {
            _schedule(() => Publish(domain));
        }
        catch (Exception exception)
        {
            Interlocked.Decrement(ref _pending);
            _logger.LogDebug(exception, "The notice of a blocked attempt at {Domain} could not be scheduled.", domain);
        }
    }

    private void Publish(string domain)
    {
        try
        {
            _events.Publish(IpcEvent.SiteBlocked(_status(), domain));
        }
        catch (Exception exception)
        {
            // An unhandled exception on a pool thread ends the process.
            _logger.LogDebug(exception, "The notice of a blocked attempt at {Domain} was not published.", domain);
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }
    }
}
