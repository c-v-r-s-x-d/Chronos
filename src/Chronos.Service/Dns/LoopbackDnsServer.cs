using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Chronos.Service.Setup;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Dns;

/// <summary>
/// UDP and TCP sockets on the loopback address, handing whole messages to
/// <see cref="DnsRequestHandler"/> and writing back its reply. Decides nothing about answers.
/// </summary>
/// <remarks>Only 127.0.0.1 is bound, not [::1]: IPv6 resolvers of interfaces are left alone.</remarks>
public sealed class LoopbackDnsServer : IAsyncDisposable
{
    public const int DnsPort = 53;

    /// <summary>
    /// How many queries may be answered at once before a datagram is dropped. A memory bound; the
    /// upstream limit lives in <see cref="DnsForwarder"/>. The read loop never stops, so blocked
    /// names are still answered.
    /// </summary>
    public const int MaxQueriesInFlight = 512;

    /// <summary>
    /// How many connections may be open at once, counted apart from queries so silent connections
    /// cannot take datagram slots. Past it the accept loop waits.
    /// </summary>
    public const int MaxConnections = 16;

    /// <summary>The longest message either transport can carry; TCP uses a two-octet length (RFC 1035 4.2.2).</summary>
    internal const int MaxMessageLength = ushort.MaxValue;

    private const int LengthPrefixLength = 2;

    /// <summary>How long a connection may stay silent, so an idle client cannot hold a slot of <see cref="MaxConnections"/>.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    // Bounded: the wait is on an upstream server and a client, neither trusted with how long a stop takes.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly DnsRequestHandler _handler;
    private readonly ILogger<LoopbackDnsServer> _logger;
    private readonly int _requestedPort;
    private readonly TimeSpan _readTimeout;

    private readonly Lock _sync = new();
    private readonly Lock _pendingSync = new();

    // Queries and connections being served, so a stop can wait for them.
    private readonly List<Task> _pending = [];

    private Socket? _udp;
    private TcpListener? _tcp;
    private CancellationTokenSource? _stopping;
    private Task? _udpLoop;
    private Task? _tcpLoop;

    private LogLevel _announce = LogLevel.Information;

    public LoopbackDnsServer(DnsRequestHandler handler, ILogger<LoopbackDnsServer> logger, int port = DnsPort)
        : this(handler, logger, port, ReadTimeout)
    {
    }

    /// <summary>The same server with a shorter read timeout, for tests.</summary>
    internal LoopbackDnsServer(
        DnsRequestHandler handler,
        ILogger<LoopbackDnsServer> logger,
        int port,
        TimeSpan readTimeout)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(logger);

        // Zero means any free port.
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(readTimeout, TimeSpan.Zero);

        _handler = handler;
        _logger = logger;
        _requestedPort = port;
        _readTimeout = readTimeout;
        Port = port;
    }

    /// <summary>The port actually bound; differs from the requested one only when that was zero.</summary>
    public int Port { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _udp is not null;
            }
        }
    }

    /// <summary>
    /// Takes both sockets and starts serving. False when the port could not be taken, with the
    /// holder in <paramref name="holder"/> when known. Nothing is left half-open on failure.
    /// </summary>
    public bool TryStart(out string? holder) => TryStart(out holder, LogLevel.Information);

    /// <summary>The same, logging the start and stop at <paramref name="announce"/>; the periodic re-check uses Debug.</summary>
    public bool TryStart(out string? holder, LogLevel announce)
    {
        holder = null;

        lock (_sync)
        {
            if (_udp is not null)
            {
                throw new InvalidOperationException("The DNS server is already running.");
            }

            Socket? udp = null;
            TcpListener? tcp = null;

            // The port actually in use; once UDP is bound, the one the platform picked. The holder is looked up by it.
            var port = _requestedPort;

            try
            {
                // No SO_REUSEADDR, so the port is already exclusive; ExclusiveAddressUse is not needed.
                udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                udp.Bind(new IPEndPoint(IPAddress.Loopback, _requestedPort));
                port = ((IPEndPoint)udp.LocalEndPoint!).Port;

                tcp = new TcpListener(IPAddress.Loopback, port);
                tcp.Start();
            }
            catch (SocketException exception)
            {
                udp?.Dispose();
                tcp?.Dispose();

                holder = HolderOf(port);

                // No domain involved, so this may be logged above Debug. A quiet start fails quietly.
                _logger.Log(
                    announce >= LogLevel.Information ? LogLevel.Warning : announce,
                    exception,
                    "The DNS server could not take 127.0.0.1:{Port}; it is held by {Holder}.",
                    port,
                    holder ?? "an unknown process");

                return false;
            }

            var stopping = new CancellationTokenSource();

            // One budget per transport. Never disposed: a task outliving the drain still releases its slot.
            var queries = new SemaphoreSlim(MaxQueriesInFlight, MaxQueriesInFlight);
            var connections = new SemaphoreSlim(MaxConnections, MaxConnections);

            Port = port;
            _udp = udp;
            _tcp = tcp;
            _stopping = stopping;
            _udpLoop = Task.Run(() => ServeUdpAsync(udp, queries, stopping.Token), CancellationToken.None);
            _tcpLoop = Task.Run(() => ServeTcpAsync(tcp, connections, stopping.Token), CancellationToken.None);

            _announce = announce;
            _logger.Log(announce, "The DNS server is listening on 127.0.0.1:{Port}.", port);
            return true;
        }
    }

    /// <summary>Whether both sockets could be taken on this port right now, by taking and releasing them. Does not log.</summary>
    public static bool CanTake(int port, out string? holder)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);

        holder = null;

        var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        TcpListener? tcp = null;
        var bound = port;

        try
        {
            udp.Bind(new IPEndPoint(IPAddress.Loopback, port));
            bound = ((IPEndPoint)udp.LocalEndPoint!).Port;

            tcp = new TcpListener(IPAddress.Loopback, bound);
            tcp.Start();

            return true;
        }
        catch (SocketException)
        {
            // Falls through to the holder lookup after the sockets are closed.
        }
        finally
        {
            udp.Dispose();
            tcp?.Dispose();
        }

        // After the close: our own UDP socket would otherwise be named as the holder.
        holder = HolderOf(bound);
        return false;
    }

    /// <summary>Closes both sockets and waits briefly for work in flight. Safe on a server never started or already stopped.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource stopping;
        LogLevel announce;
        Socket? udp;
        TcpListener? tcp;
        Task?[] loops;

        lock (_sync)
        {
            if (_stopping is null)
            {
                return;
            }

            stopping = _stopping;
            udp = _udp;
            tcp = _tcp;
            loops = [_udpLoop, _tcpLoop];
            announce = _announce;

            _stopping = null;
            _udp = null;
            _tcp = null;
            _udpLoop = null;
            _tcpLoop = null;
        }

        // Cancelled before closing, so loops woken by the close do not report a failure.
        await stopping.CancelAsync().ConfigureAwait(false);

        udp?.Dispose();
        tcp?.Dispose();

        foreach (var loop in loops)
        {
            await QuietlyAsync(loop, DrainTimeout).ConfigureAwait(false);
        }

        Task[] pending;
        lock (_pendingSync)
        {
            pending = [.. _pending];
            _pending.Clear();
        }

        await QuietlyAsync(Task.WhenAll(pending), DrainTimeout).ConfigureAwait(false);

        stopping.Dispose();
        _logger.Log(announce, "The DNS server has stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Waits for finishing work; how it ends never affects the stop.</summary>
    private static async Task QuietlyAsync(Task? work, TimeSpan limit)
    {
        if (work is null)
        {
            return;
        }

        try
        {
            await work.WaitAsync(limit, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // Includes the timeout: a stuck upstream query must not hold the service open.
        }
    }

    /// <summary>
    /// Who holds the port, as named by <see cref="PortOwner"/>, which checks both UDP and TCP (a
    /// resolver on TCP/53 is common). Null when the tables cannot be read or the port is free.
    /// </summary>
    /// <remarks>Port zero is never asked about: the UDP bind itself was refused.</remarks>
    private static string? HolderOf(int port) => port == 0 ? null : PortOwner.Find(port).Holder;

    /// <summary>The receive loop. Never waits for earlier queries, so a slow upstream cannot delay a blocked name.</summary>
    private async Task ServeUdpAsync(Socket socket, SemaphoreSlim slots, CancellationToken ct)
    {
        // One buffer, copied out per datagram.
        var buffer = new byte[MaxMessageLength];
        var client = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult received;

            try
            {
                received = await socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, client, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException
                                                  or OperationCanceledException)
            {
                if (ct.IsCancellationRequested || exception is ObjectDisposedException)
                {
                    return;
                }

                // Windows reports a client that closed before the answer arrived as a failed receive.
                _logger.LogDebug(exception, "A datagram could not be received.");
                continue;
            }

            var query = buffer[..received.ReceivedBytes];

            // Never blocks: past MaxQueriesInFlight the datagram is dropped and the client retries.
            if (!slots.Wait(0, CancellationToken.None))
            {
                _logger.LogDebug(
                    "A datagram was dropped: {Ceiling} queries are already being answered.",
                    MaxQueriesInFlight);
                continue;
            }

            Track(AnswerOverUdpAsync(socket, query, received.RemoteEndPoint, slots, ct));
        }
    }

    private async Task AnswerOverUdpAsync(
        Socket socket,
        byte[] query,
        EndPoint client,
        SemaphoreSlim slots,
        CancellationToken ct)
    {
        try
        {
            var answer = await _handler.AnswerAsync(query, DnsTransportKind.Udp, ct).ConfigureAwait(false);

            // Null means say nothing.
            if (answer is null)
            {
                return;
            }

            await socket.SendToAsync(answer, SocketFlags.None, client, ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Debug only: the exception message may name the domain. One failed query must not stop the loop.
            _logger.LogDebug(exception, "A query over UDP was not answered.");
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>
    /// The accept loop. Each connection gets its own task and one of <see cref="MaxConnections"/>
    /// slots; waiting for a slot delays only the next connection, not UDP.
    /// </summary>
    private async Task ServeTcpAsync(TcpListener listener, SemaphoreSlim slots, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException
                                                  or InvalidOperationException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested || exception is ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                _logger.LogDebug(exception, "A connection could not be accepted.");
                continue;
            }

            try
            {
                await slots.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The stop won the race for the slot; the accepted connection must be closed here.
                client.Dispose();
                return;
            }

            Track(ServeConnectionAsync(client, slots, ct));
        }
    }

    /// <summary>One connection, for as many queries as the client sends, each framed by a two-octet length (RFC 1035 4.2.2).</summary>
    private async Task ServeConnectionAsync(TcpClient client, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var prefix = new byte[LengthPrefixLength];

                while (!ct.IsCancellationRequested)
                {
                    // Timeout per message: it is for silence, not connection age.
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(_readTimeout);

                    if (!await TryReadAsync(stream, prefix, idle.Token).ConfigureAwait(false))
                    {
                        return;
                    }

                    // Only a fully received message is answered: a half-read buffer has a zero tail
                    // that parses, and would be answered and reported as a visit.
                    if (await TryReadMessageAsync(stream, BinaryPrimitives.ReadUInt16BigEndian(prefix), idle.Token)
                            .ConfigureAwait(false) is not { } message)
                    {
                        return;
                    }

                    var answer = await _handler.AnswerAsync(message, DnsTransportKind.Tcp, ct).ConfigureAwait(false);

                    if (answer is null)
                    {
                        // The connection stays usable for the next message.
                        continue;
                    }

                    if (answer.Length > MaxMessageLength)
                    {
                        // Cannot be framed in a two-octet length; drop the connection.
                        _logger.LogDebug(
                            "An answer of {Length} octets does not fit a TCP message and was dropped.",
                            answer.Length);
                        return;
                    }

                    BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)answer.Length);
                    await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
                    await stream.WriteAsync(answer, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            // As with UDP: Debug only, and a client hanging up mid-message is ordinary.
            _logger.LogDebug(exception, "A connection ended before it was finished with.");
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>Fills <paramref name="block"/>; false when the client closed first, which is how connections normally end.</summary>
    private static async Task<bool> TryReadAsync(NetworkStream stream, byte[] block, CancellationToken ct)
    {
        try
        {
            await stream.ReadExactlyAsync(block, ct).ConfigureAwait(false);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    /// <summary>A message of <paramref name="length"/> octets, or null when the client closed before all of it arrived.</summary>
    private static async Task<byte[]?> TryReadMessageAsync(NetworkStream stream, int length, CancellationToken ct)
    {
        var message = new byte[length];

        return await TryReadAsync(stream, message, ct).ConfigureAwait(false) ? message : null;
    }

    /// <summary>Keeps a task where a stop can wait for it. Completed tasks are dropped on the way in.</summary>
    private void Track(Task work)
    {
        lock (_pendingSync)
        {
            _pending.RemoveAll(static task => task.IsCompleted);
            _pending.Add(work);
        }
    }

    /// <summary>Tracked tasks right now; for a test that the list does not grow without bound.</summary>
    internal int Tracked
    {
        get
        {
            lock (_pendingSync)
            {
                return _pending.Count;
            }
        }
    }
}
