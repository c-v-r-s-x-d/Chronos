using Chronos.App.Diagnostics;
using Chronos.Ipc;

namespace Chronos.App.Services;

/// <summary>The interface's one connection to the service and the one owner of what the interface believes about it: a short-lived connection per command and a long-lived one for the subscription.</summary>
public sealed class ServiceLink : IServiceLink, IAsyncDisposable
{
    private readonly IpcClient _client;

    // Injected so no test waits on wall time; nothing else has a reason to replace it.
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;

    /// <summary>Where a failure goes. Defaults to <see cref="AppLog.Silent"/> so tests need not write files.</summary>
    private readonly AppLog _log;

    private readonly CancellationTokenSource _stopping = new();

    private readonly object _sync = new();

    private ServiceSnapshot _snapshot = ServiceSnapshot.Connecting;

    private Task? _watching;

    public ServiceLink(
        string pipeName = IpcProtocol.DefaultPipeName,
        Func<TimeSpan, CancellationToken, Task>? wait = null,
        AppLog? log = null)
    {
        _client = new IpcClient(pipeName);
        _wait = wait ?? Task.Delay;
        _log = log ?? AppLog.Silent;
    }

    public ServiceSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    public event EventHandler<ServiceSnapshot>? Changed;

    public event EventHandler<BlockEvent>? Blocked;

    public event EventHandler<TimeSpan>? Retrying;

    public void Start()
    {
        if (_watching is not null)
        {
            throw new InvalidOperationException("The link is already running.");
        }

        // On a task of its own: reaching the pipe happens before the first await, and the interface
        // thread must not stop for it.
        _watching = Task.Run(() => WatchAsync(_stopping.Token));
    }

    /// <summary>Sends one command on its own connection; returns the answer, or null when the service could not be reached. A failed command does not decide <see cref="Snapshot"/>: the subscription knows whether the service is there, and a busy connection slot must not show "service unavailable".</summary>
    public async Task<IpcResponse?> SendAsync(IpcRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var answer = await _client.SendAsync(request, ct).ConfigureAwait(false);

            if (answer is { Accepted: false })
            {
                // Every command passes through here, so this is the one place a refusal is logged.
                _log.CommandRefused(request, answer);
            }

            return answer;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception fault)
        {
            // Nothing listening, or the connection broke before the answer arrived. The caller
            // reports it on screen rather than receiving an exception, but it is still logged.
            _log.CommandUnanswered(request, fault);

            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_watching is not null)
        {
            try
            {
                await _watching.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _watching = null;
        }

        _stopping.Dispose();
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        var backoff = new ReconnectBackoff();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SubscribedAsync(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Nothing listening, the service went away mid-handshake, or an unreadable answer: one state on screen.
                Publish(ServiceSnapshot.Unavailable);
            }

            try
            {
                var delay = backoff.Next();
                Retrying?.Invoke(this, delay);
                await _wait(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SubscribedAsync(ReconnectBackoff backoff, CancellationToken ct)
    {
        await using var subscription = await _client.SubscribeAsync(ct).ConfigureAwait(false);

        var acknowledgement = subscription.Acknowledgement;
        if (!acknowledgement.Accepted || !AnswerCheck.IsUsable(acknowledgement))
        {
            // Reached and refused (protocol version) or accepted with an unusable status. The service
            // runs but nothing it says will reach the screen, and "connected" would mislead.
            //
            // Behind the transition: a version mismatch lasts until reinstall, and logging each
            // attempt would swamp the file.
            if (Publish(ServiceSnapshot.Unavailable))
            {
                if (acknowledgement.Accepted)
                {
                    _log.ServiceSentAnUnusableAcknowledgement();
                }
                else
                {
                    _log.ServiceSpeaksAnotherProtocol();
                }
            }

            return;
        }

        backoff.Reset();
        // Usable above, which includes being there.
        Publish(ServiceSnapshot.Available(acknowledgement.Status!));

        await foreach (var published in subscription.ReadEventsAsync(ct).ConfigureAwait(false))
        {
            // Every event carries the whole status, so one that was dropped costs nothing. A block's
            // status is built on another thread and can arrive after a newer one; it is not adopted.
            if (published.Status is { } status && AnswerCheck.IsUsable(status) && !(Snapshot.Status?.Now > status.Now))
            {
                Publish(ServiceSnapshot.Available(status));
            }

            // The name goes on afterwards, and only for a block. Publish above may have said nothing,
            // since a block during a running session usually carries the status already shown.
            if (published is { Kind: IpcEventKind.AppBlocked, AppName: { Length: > 0 } appName, Status: { } blocked } && AnswerCheck.IsUsable(blocked))
            {
                Blocked?.Invoke(this, new AppBlock(appName, blocked));
            }

            // The domain came from an attempt: it goes to the screen and never to _log.
            if (published is { Kind: IpcEventKind.SiteBlocked, Domain: { Length: > 0 } domain, Status: { } refused } && AnswerCheck.IsUsable(refused))
            {
                Blocked?.Invoke(this, new SiteBlock(domain, refused));
            }
        }

        // The stream ran out: the service is gone. Everything it said is stale and must not stay on
        // screen, so it is cleared here rather than left for a failed reconnect to discover.
        Publish(ServiceSnapshot.Unavailable);
    }

    /// <summary>Whether the state actually moved, which is the only kind worth writing down.</summary>
    private bool Publish(ServiceSnapshot next)
    {
        ServiceConnectionState was;

        lock (_sync)
        {
            if (_snapshot == next)
            {
                // A reconnect that keeps failing would republish the same unavailability every few seconds.
                return false;
            }

            was = _snapshot.State;
            _snapshot = next;

            // Raised while the lock is held, so the order of the notifications is the order of the
            // states themselves. The handler hands the work to the interface thread and returns.
            Changed?.Invoke(this, next);
        }

        Record(was, next.State);

        return true;
    }

    /// <summary>The channel going away and coming back. Behind the check above so a long outage writes one line, and outside the lock so a disk write does not hold it.</summary>
    private void Record(ServiceConnectionState was, ServiceConnectionState now)
    {
        if (now == ServiceConnectionState.Unavailable)
        {
            _log.ServiceLost();
        }
        else if (now == ServiceConnectionState.Available && was != ServiceConnectionState.Available)
        {
            // Only on the way in. Available follows Available on every status the service sends,
            // and a line for each of those would be a record of the session rather than of the link.
            _log.ServiceReached();
        }
    }
}
