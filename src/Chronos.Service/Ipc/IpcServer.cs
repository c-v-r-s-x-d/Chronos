using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using Chronos.Ipc;
using Chronos.Service.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Ipc;

public sealed class IpcServer(
    CommandDispatcher dispatcher,
    EventBus events,
    ILogger<IpcServer> logger,
    string pipeName = IpcProtocol.DefaultPipeName,
    TimeSpan? connectionTimeout = null)
{
    // Our own cap on connection tasks. The UI needs two connections, the CLI one; the rest is headroom.
    private const int MaxConcurrentConnections = 16;

    // Without this delay a persistent failure to create the listening instance spins a whole core.
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromSeconds(1);

    // How long a stop waits for open connections, so a client that never reads cannot hold it open.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _connectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(10);

    private readonly RepeatedDiagnostic _acceptFailure = new();

    private readonly RepeatedDiagnostic _slotsExhausted = new();

    /// <summary>Null except under <see cref="ForTests"/>; then the pipe uses <see cref="ServiceAcl"/>.</summary>
    private readonly PipeSecurity? _hostAcl;

    // Private so production code has no route to a hand-picked ACL.
    private IpcServer(
        CommandDispatcher dispatcher,
        EventBus events,
        ILogger<IpcServer> logger,
        string pipeName,
        TimeSpan? connectionTimeout,
        PipeSecurity hostAcl)
        : this(dispatcher, events, logger, pipeName, connectionTimeout) => _hostAcl = hostAcl;

    /// <summary>
    /// A server for tests, on a pipe of its own with an ACL naming only the current account.
    /// <see cref="ServiceAcl"/> lacks CreateNewInstance for Interactive, so an unelevated accept
    /// loop cannot create a second instance. Do not widen the production ACL to fix that.
    /// The default pipe name is refused.
    /// </summary>
    internal static IpcServer ForTests(
        CommandDispatcher dispatcher,
        EventBus events,
        ILogger<IpcServer> logger,
        string pipeName,
        TimeSpan? connectionTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        if (string.Equals(pipeName, IpcProtocol.DefaultPipeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"A test server may not host '{IpcProtocol.DefaultPipeName}': that name belongs to the service, "
                + "and the ACL this builds is not the service's.",
                nameof(pipeName));
        }

        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException("The current identity has no user SID to grant the test pipe to.");

        // Deliberately unlike ServiceAcl: nobody but the account running the test.
        var acl = new PipeSecurity();
        acl.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));

        return new IpcServer(dispatcher, events, logger, pipeName, connectionTimeout, acl);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        logger.LogInformation("IPC server listening on pipe {PipeName}.", pipeName);

        // Not disposed: a connection task can outlive this method (the drain is bounded) and must
        // still be able to release its slot. Disposal only matters once AvailableWaitHandle is read.
        var slots = new SemaphoreSlim(MaxConcurrentConnections, MaxConcurrentConnections);

        // Touched only by the accept loop and the drain after it, so no lock.
        var connections = new List<Task>();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await TakeSlotAsync(slots, ct).ConfigureAwait(false);

                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = CreatePipe();

                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (pipe is not null)
                    {
                        await pipe.DisposeAsync().ConfigureAwait(false);
                    }

                    slots.Release();

                    if (exception is OperationCanceledException)
                    {
                        throw;
                    }

                    ReportAcceptFailure(exception);

                    await Task.Delay(AcceptRetryDelay, ct).ConfigureAwait(false);

                    continue;
                }

                _acceptFailure.IsNews(null);

                // Not awaited: a subscription stays connected for hours.
                connections.RemoveAll(static connection => connection.IsCompleted);
                connections.Add(ServeConnectionAsync(pipe, slots, ct));
            }
        }
        finally
        {
            // Wait for handlers so the host does not dispose what a connection is still writing to,
            // but only up to DrainTimeout: the answer write takes no token, so a client that never
            // reads would otherwise hold the stop open.
            var drain = Task.WhenAll(connections);
            var settled = await Task.WhenAny(drain, Task.Delay(DrainTimeout, CancellationToken.None))
                .ConfigureAwait(false);

            if (!ReferenceEquals(settled, drain))
            {
                logger.LogWarning(
                    "IPC server stopped with {Count} connection(s) still open after {Timeout}.",
                    connections.Count(static connection => !connection.IsCompleted),
                    DrainTimeout);
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Takes a connection slot, and reports when the cap is reached. The warning describes the
    /// service's state, since a client that is never accepted cannot be observed.
    /// </summary>
    private async Task TakeSlotAsync(SemaphoreSlim slots, CancellationToken ct)
    {
        if (!slots.Wait(0, ct))
        {
            if (_slotsExhausted.IsNews(nameof(MaxConcurrentConnections)))
            {
                logger.LogWarning(
                    "All {Count} IPC connection slots are in use; a client connecting now is not answered until one is freed.",
                    MaxConcurrentConnections);
            }

            await slots.WaitAsync(ct).ConfigureAwait(false);
        }

        // Clear only once there is headroom, so a queue of clients at the cap does not log per client.
        if (slots.CurrentCount > 0)
        {
            _slotsExhausted.IsNews(null);
        }
    }

    private void ReportAcceptFailure(Exception exception)
    {
        if (_acceptFailure.IsNews($"{exception.GetType().FullName}: {exception.Message}"))
        {
            logger.LogWarning(exception, "IPC server could not accept a connection.");
        }
        else
        {
            logger.LogDebug(exception, "IPC server still cannot accept a connection.");
        }
    }

    private NamedPipeServerStream CreatePipe() =>
        NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            PipeAcl());

    /// <summary>The rights for the next listening instance. Internal so tests can inspect them.</summary>
    internal PipeSecurity PipeAcl() => _hostAcl ?? ServiceAcl();

    /// <summary>
    /// LocalSystem and Administrators get full control; interactive logon sessions get read/write,
    /// which keeps services and scheduled tasks off the pipe. Interactive must never get
    /// CreateNewInstance: that would let any user squat the pipe name. See <see cref="ForTests"/>.
    /// </summary>
    private static PipeSecurity ServiceAcl()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return security;
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream pipe, SemaphoreSlim slots, CancellationToken ct)
    {
        // Catch everything here: nobody awaits this task until shutdown.
        try
        {
            await using (pipe.ConfigureAwait(false))
            {
                await ServeAsync(pipe, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The deadline fired: a client connected and never sent a request.
            logger.LogWarning("IPC connection was dropped after {Timeout} without a request.", _connectionTimeout);
        }
        catch (OperationCanceledException)
        {
            // Shutdown. The loop reports it; this task must end quietly.
        }
        catch (Exception exception)
        {
            // One malformed client must never take down the service that holds the block.
            logger.LogWarning(exception, "IPC connection failed and was dropped.");
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);

        // Not an await using: closing the writer is itself a write. See CloseAsync.
        var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        try
        {
            string? line;

            // The deadline covers only the wait for the request, not a subscription that follows.
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(_connectionTimeout);

                line = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            }

            if (line is null)
            {
                return;
            }

            var request = Parse(line, out var unreadable);
            var subscribing = request is not null
                && string.Equals(request.Command, IpcProtocol.SubscribeCommand, StringComparison.Ordinal);

            // Subscribe before answering, or an event published in between reaches nobody.
            ChannelReader<IpcEvent>? queue = null;
            using var subscription = subscribing ? events.Subscribe(out queue) : null;

            var response = request is null ? unreadable! : dispatcher.Dispatch(request);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, IpcJson.Options)).ConfigureAwait(false);

            // Subscribing switches the connection to write-only. A refused Subscribe is dropped.
            if (response.Accepted && queue is not null)
            {
                await StreamEventsAsync(reader, writer, queue, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await CloseAsync(writer).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes the writer, treating a pipe broken by the closing flush as a client that went away.
    /// Disposing a <see cref="StreamWriter"/> flushes even with AutoFlush on, and would otherwise
    /// throw out of <see cref="ServeConnectionAsync"/>.
    /// </summary>
    private async Task CloseAsync(StreamWriter writer)
    {
        try
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            logger.LogDebug(exception, "A client went away before its connection could be closed.");
        }
    }

    /// <summary>Writes events into the connection until the service stops or the client goes away.</summary>
    private async Task StreamEventsAsync(
        StreamReader reader, StreamWriter writer, ChannelReader<IpcEvent> queue, CancellationToken ct)
    {
        // Cancelled by a stopping service or by the client closing its end.
        var life = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var departure = WatchForDepartureAsync(reader, life);

        try
        {
            // The wait must be cancellable, or every stop would pay the whole drain timeout.
            while (await queue.WaitToReadAsync(life.Token).ConfigureAwait(false))
            {
                while (queue.TryRead(out var published))
                {
                    var json = JsonSerializer.Serialize(published, IpcJson.Options);

                    await writer.WriteLineAsync(json.AsMemory(), life.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The client closed its end: the normal way a subscription ends.
            logger.LogDebug("A subscribed client disconnected.");
        }
        catch (IOException exception)
        {
            // The client vanished mid-write.
            logger.LogDebug(exception, "A subscribed client went away while an event was being written.");
        }
        finally
        {
            await life.CancelAsync().ConfigureAwait(false);

            // Awaited before disposing the source so the watcher cannot touch a disposed one.
            await departure.ConfigureAwait(false);
            life.Dispose();
        }
    }

    /// <summary>
    /// Notices the far end closing. The data read is discarded; without the read a vanished client
    /// is found only on the next write, which can be hours away.
    /// </summary>
    private static async Task WatchForDepartureAsync(StreamReader reader, CancellationTokenSource life)
    {
        try
        {
            var discarded = new char[64];

            while (await reader.ReadAsync(discarded, life.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception)
        {
            // Cancellation or a broken pipe: stop either way.
        }
        finally
        {
            await life.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The request the line holds, or null and the refusal that answers it.</summary>
    private static IpcRequest? Parse(string line, out IpcResponse? refusal)
    {
        IpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<IpcRequest>(line, IpcJson.Options);
        }
        catch (JsonException)
        {
            refusal = IpcResponse.Fail(IpcCodes.RequestInvalidJson);

            return null;
        }

        refusal = request is null ? IpcResponse.Fail(IpcCodes.RequestEmpty) : null;

        return request;
    }
}
