using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Chronos.Ipc;

public sealed class IpcClient(string pipeName = IpcProtocol.DefaultPipeName)
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var pipe = await ConnectAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, leaveOpen: true);

        return await AskAsync(pipe, reader, request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a connection that stays open: the service answers with the current status, then writes
    /// events until one side goes away. Returns a subscription rather than an
    /// <see cref="IAsyncEnumerable{T}"/> so connection and protocol errors surface here, not on the first read.
    /// </summary>
    public async Task<IpcSubscription> SubscribeAsync(CancellationToken ct)
    {
        var pipe = await ConnectAsync(ct).ConfigureAwait(false);
        StreamReader? reader = null;

        try
        {
            // Kept for the subscription's lifetime: its buffer may already hold the first events.
            reader = new StreamReader(pipe, leaveOpen: true);

            var acknowledgement = await AskAsync(
                pipe,
                reader,
                new IpcRequest { Command = IpcProtocol.SubscribeCommand },
                ct).ConfigureAwait(false);

            return new IpcSubscription(pipe, reader, acknowledgement);
        }
        catch
        {
            reader?.Dispose();
            await pipe.DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }

    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(ConnectTimeout, ct).ConfigureAwait(false);

            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }

    private static async Task<IpcResponse> AskAsync(
        NamedPipeClientStream pipe, StreamReader reader, IpcRequest request, CancellationToken ct)
    {
        // leaveOpen: the answer still has to arrive on this connection.
        await using (var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true })
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, IpcJson.Options).AsMemory(), ct)
                .ConfigureAwait(false);
        }

        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false)
            ?? throw new IOException("The service closed the connection without answering.");

        return JsonSerializer.Deserialize<IpcResponse>(line, IpcJson.Options)
            ?? throw new JsonException("The service returned an empty answer.");
    }
}

/// <summary>A connection in subscription mode: the acknowledgement, then pushed events. Nothing is written back.</summary>
public sealed class IpcSubscription : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;

    internal IpcSubscription(NamedPipeClientStream pipe, StreamReader reader, IpcResponse acknowledgement)
    {
        _pipe = pipe;
        _reader = reader;
        Acknowledgement = acknowledgement;
    }

    /// <summary>The service's answer to the subscription, carrying the status at that moment.</summary>
    public IpcResponse Acknowledgement { get; }

    /// <summary>The events as they arrive. Cancellation throws; the service going away just ends the sequence, so callers tell them apart by their own token.</summary>
    public async IAsyncEnumerable<IpcEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            string? line;

            try
            {
                line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The service vanished mid-line; same ending.
                yield break;
            }

            if (line is null)
            {
                yield break;
            }

            var published = Read(line);
            if (published is not null)
            {
                yield return published;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();

        await _pipe.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>An unreadable line is one lost event; the next carries the whole status again.</summary>
    private static IpcEvent? Read(string line)
    {
        try
        {
            // Without a kind the event is meaningless.
            return JsonSerializer.Deserialize<IpcEvent>(line, IpcJson.Options) is { Kind: not null } published
                ? published
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
