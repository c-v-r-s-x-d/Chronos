using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Chronos.Service.Dns;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

/// <summary>The query the DNS layer sends itself before it takes an interface. Every port is ephemeral; 53 is never bound or asked.</summary>
public sealed class DnsSelfCheckTests : IAsyncLifetime
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    private readonly CountingForwarder _forwarder = new();
    private readonly List<IDisposable> _sockets = [];
    private LoopbackDnsServer? _server;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.StopAsync();
        }

        foreach (var socket in _sockets)
        {
            socket.Dispose();
        }
    }

    [Fact]
    public void TheTimeoutIsOneSecond()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), LoopbackDnsSelfCheck.Timeout);
        Assert.Equal(LoopbackDnsSelfCheck.Timeout, new LoopbackDnsSelfCheck().Wait);
    }

    [Fact]
    public void ItRefusesAPortOutsideTheRangeAndATimeoutThatIsNotOne()
    {
        var check = new LoopbackDnsSelfCheck();

        Assert.Throws<ArgumentOutOfRangeException>(() => check.Reaches(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => check.Reaches(65536));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoopbackDnsSelfCheck(TimeSpan.Zero));
    }

    [Fact]
    public void TheRealServerOnAnEphemeralPortIsReachedAndForwardsNothing()
    {
        var port = StartServer();

        Assert.True(new LoopbackDnsSelfCheck().Reaches(port));

        // Answered by the server itself: the check never costs an external server anything.
        Assert.Equal(0, _forwarder.Calls);
    }

    [Fact]
    public void AServerThatNeverAnswersIsNotReachedAndTheCheckGivesUpAtItsTimeout()
    {
        // What a VPN's filter looks like from here: the datagram goes out and nothing comes back.
        var silent = Bind();
        var wait = TimeSpan.FromMilliseconds(500);
        var watch = Stopwatch.StartNew();

        Assert.False(new LoopbackDnsSelfCheck(wait).Reaches(PortOf(silent)));

        // Its own wait and not a multiple of it: the bound is under twice the wait.
        Assert.InRange(watch.Elapsed, wait - TimeSpan.FromMilliseconds(50), wait * 2);
    }

    [Fact]
    public void APortNobodyHoldsIsNotReached()
    {
        var port = PortOf(Bind());
        _sockets.ForEach(socket => socket.Dispose());

        Assert.False(new LoopbackDnsSelfCheck(Short).Reaches(port));
    }

    [Fact]
    public async Task TheQueryIsAHeaderWithNoQuestion()
    {
        var responder = Bind();
        var check = Task.Run(() => new LoopbackDnsSelfCheck(Short).Reaches(PortOf(responder)));

        var buffer = new byte[512];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        var received = await responder.ReceiveFromAsync(buffer, SocketFlags.None, from).WaitAsync(TimeSpan.FromSeconds(10));
        await check;

        Assert.Equal(12, received.ReceivedBytes);
        Assert.Equal(0, buffer[2] & 0x80);                                       // a query
        Assert.Equal(0, buffer[2] & 0x78);                                       // opcode QUERY
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4))); // QDCOUNT 0
    }

    [Theory]
    [InlineData(true, true, 1, true)]
    [InlineData(false, true, 1, false)]  // somebody else's id
    [InlineData(true, false, 1, false)]  // a query, not a reply
    [InlineData(true, true, 0, false)]   // an answer our server would never write to this
    public async Task OnlyAFormErrReplyToItsOwnIdCounts(bool sameId, bool isReply, byte rcode, bool reached)
    {
        var responder = Bind();
        var check = Task.Run(() => new LoopbackDnsSelfCheck(Short).Reaches(PortOf(responder)));

        var buffer = new byte[512];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        var received = await responder.ReceiveFromAsync(buffer, SocketFlags.None, from).WaitAsync(TimeSpan.FromSeconds(10));

        var reply = buffer[..12];
        if (!sameId)
        {
            reply[0] ^= 0xFF;
        }

        reply[2] = (byte)(isReply ? 0x80 : 0x00);
        reply[3] = rcode;
        await responder.SendToAsync(reply, SocketFlags.None, received.RemoteEndPoint);

        Assert.Equal(reached, await check);
    }

    [Fact]
    public async Task AReplyShorterThanAHeaderDoesNotCount()
    {
        var responder = Bind();
        var check = Task.Run(() => new LoopbackDnsSelfCheck(Short).Reaches(PortOf(responder)));

        var buffer = new byte[512];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        var received = await responder.ReceiveFromAsync(buffer, SocketFlags.None, from).WaitAsync(TimeSpan.FromSeconds(10));

        // The id, QR and FORMERR all right, and the rest of the header missing.
        var reply = buffer[..4];
        reply[2] = 0x80;
        reply[3] = 1;
        await responder.SendToAsync(reply, SocketFlags.None, received.RemoteEndPoint);

        Assert.False(await check);
    }

    private int StartServer()
    {
        var handler = new DnsRequestHandler(
            _forwarder, new DnsResponseCache(new ServiceTestClock(DateTimeOffset.UnixEpoch)), _ => { }, NullLogger<DnsRequestHandler>.Instance);

        // Port 0 can be refused its TCP half by a connection in TIME_WAIT; another one is taken.
        for (var attempt = 0; ; attempt++)
        {
            var server = new LoopbackDnsServer(handler, NullLogger<LoopbackDnsServer>.Instance, port: 0);
            if (server.TryStart(out _))
            {
                _server = server;
                return server.Port;
            }

            Assert.True(attempt < 20, "No ephemeral port took both halves.");
        }
    }

    private Socket Bind()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _sockets.Add(socket);
        return socket;
    }

    private static int PortOf(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;

    private sealed class CountingForwarder : IDnsForwarder
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<byte[]?> ForwardAsync(byte[] query, bool overTcp, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<byte[]?>(null);
        }
    }
}
