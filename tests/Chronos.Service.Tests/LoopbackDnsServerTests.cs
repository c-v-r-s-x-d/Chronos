using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Chronos.Core.Rules;
using Chronos.Service.Dns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

/// <summary>
/// The server, over both sockets it takes. Every test uses an ephemeral port; port 53 is never bound here.
/// Waits are on events, not sleeps; the "must not happen" waits are bounded by <see cref="Grace"/>.
/// </summary>
public sealed class LoopbackDnsServerTests
{
    private const string Blocked = "blocked.example";
    private const string Allowed = "allowed.example";

    private const ushort TypeA = 1;
    private const ushort ClassInternet = 1;

    private const byte RecursionDesired = 0x01;
    private const byte Qr = 0x80;

    private const byte RcodeNoError = 0;
    private const byte RcodeNameError = 3;

    /// <summary>How long anything that must happen is given. Never reached on a working server.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>How long anything that must not happen is watched for.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(750);

    private readonly ServiceTestClock _clock = new(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly CapturingLogger<LoopbackDnsServer> _log = new();
    private readonly TestForwarder _forwarder = new();
    private readonly ConcurrentQueue<string> _notified = new();

    [Fact]
    public void AHandlerIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => new LoopbackDnsServer(null!, _log, port: 0));
    }

    [Fact]
    public void ALoggerIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => new LoopbackDnsServer(Handler(), null!, port: 0));
    }

    [Fact]
    public void ANegativePortIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoopbackDnsServer(Handler(), _log, port: -1));
    }

    [Fact]
    public void APortAboveTheRangeOfPortsIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LoopbackDnsServer(Handler(), _log, port: ushort.MaxValue + 1));
    }

    [Fact]
    public async Task TheLastPortThereIsIsAcceptedAndReportedBeforeAnythingIsBound()
    {
        await using var server = new LoopbackDnsServer(Handler(), _log, port: ushort.MaxValue);

        Assert.Equal(ushort.MaxValue, server.Port);
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task AnEphemeralPortIsAskedForAsZeroAndReportedAsWhatWasBound()
    {
        await using var unstarted = new LoopbackDnsServer(Handler(), _log, port: 0);
        Assert.Equal(0, unstarted.Port);

        await using var server = Start();

        Assert.NotEqual(0, server.Port);
        Assert.True(server.IsRunning);
    }

    [Fact]
    public async Task ItAnswersAQueryOverUdp()
    {
        await using var server = Start();

        var answer = await AskOverUdp(server.Port, Query(Blocked));

        Assert.Equal(RcodeNameError, Rcode(answer));
    }

    [Fact]
    public async Task ItAnswersAQueryOverTcp()
    {
        await using var server = Start();

        var answer = await AskOverTcp(server.Port, Query(Blocked));

        Assert.Equal(RcodeNameError, Rcode(answer));
    }

    /// <summary>The length is measured against header plus question: the query carries no OPT record.</summary>
    [Fact]
    public async Task AnAnswerOverTcpIsFramedByItsOwnLength()
    {
        var query = Query(Blocked);
        await using var server = Start();
        using var client = await ConnectAsync(server.Port);
        var stream = client.GetStream();

        await WriteFramedAsync(stream, query);

        var prefix = new byte[2];
        using var cts = new CancellationTokenSource(Patience);
        await stream.ReadExactlyAsync(prefix, cts.Token);

        Assert.Equal(query.Length, BinaryPrimitives.ReadUInt16BigEndian(prefix));

        var answer = new byte[query.Length];
        await stream.ReadExactlyAsync(answer, cts.Token);
        Assert.Equal(RcodeNameError, Rcode(answer));
    }

    [Fact]
    public async Task AnAnswerCarriesTheIdentifierOfTheQueryItAnswers()
    {
        await using var server = Start();

        var answer = await AskOverUdp(server.Port, Query(Blocked, id: 0x2A3B));

        Assert.Equal(0x2A3B, BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(0, 2)));
    }

    /// <summary>The handler is given the message, not the receive buffer; only the forwarder's view shows the difference.</summary>
    [Fact]
    public async Task TheQueryHandedOnIsTheDatagramAndNotTheBufferItLandedIn()
    {
        _forwarder.Answer = Answer(Allowed);
        var query = Query(Allowed);
        await using var server = Start();

        await AskOverUdp(server.Port, query);

        Assert.Equal(query, Assert.Single(_forwarder.Calls).Query);
    }

    [Fact]
    public async Task TheMessageHandedOnIsTheFramedOneAndNothingElse()
    {
        _forwarder.Answer = Answer(Allowed);
        var query = Query(Allowed);
        await using var server = Start();

        await AskOverTcp(server.Port, query);

        Assert.Equal(query, Assert.Single(_forwarder.Calls).Query);
    }

    /// <summary>The fake hands out a fresh copy each time, so this compares against an array the server never touched.</summary>
    [Fact]
    public async Task AForwardedAnswerReachesTheClientUnchanged()
    {
        _forwarder.Answer = Answer(Allowed);
        await using var server = Start();

        var answer = await AskOverUdp(server.Port, Query(Allowed));

        Assert.Equal(_forwarder.Answer, answer);
    }

    [Fact]
    public async Task AQueryOverUdpIsForwardedAsAUdpOne()
    {
        _forwarder.Answer = Answer(Allowed);
        await using var server = Start();

        await AskOverUdp(server.Port, Query(Allowed));

        Assert.False(Assert.Single(_forwarder.Calls).OverTcp);
    }

    [Fact]
    public async Task AQueryOverTcpIsForwardedAsATcpOne()
    {
        _forwarder.Answer = Answer(Allowed);
        await using var server = Start();

        await AskOverTcp(server.Port, Query(Allowed));

        Assert.True(Assert.Single(_forwarder.Calls).OverTcp);
    }

    [Fact]
    public async Task OneConnectionServesOneQueryAfterAnother()
    {
        await using var server = Start();
        using var client = await ConnectAsync(server.Port);
        var stream = client.GetStream();

        await WriteFramedAsync(stream, Query(Blocked, id: 0x0101));
        var first = await ReadFramedAsync(stream);

        await WriteFramedAsync(stream, Query(Blocked, id: 0x0202));
        var second = await ReadFramedAsync(stream);

        Assert.Equal(0x0101, BinaryPrimitives.ReadUInt16BigEndian(first.AsSpan(0, 2)));
        Assert.Equal(0x0202, BinaryPrimitives.ReadUInt16BigEndian(second.AsSpan(0, 2)));
    }

    [Fact]
    public async Task AnAnswerAsLongAsThePrefixCanCountIsSentWhole()
    {
        _forwarder.Answer = Answer(Allowed, length: ushort.MaxValue);
        await using var server = Start();

        var answer = await AskOverTcp(server.Port, Query(Allowed));

        Assert.Equal(ushort.MaxValue, answer.Length);
        Assert.Equal(_forwarder.Answer, answer);
    }

    [Fact]
    public async Task AnAnswerTooLongToFrameEndsThatConnectionAndNothingElse()
    {
        _forwarder.Answer = Answer(Allowed, length: ushort.MaxValue + 1);
        await using var server = Start();

        using (var client = await ConnectAsync(server.Port))
        {
            var stream = client.GetStream();
            await WriteFramedAsync(stream, Query(Allowed));

            Assert.True(await ClosedWithinAsync(stream, Patience));
        }

        Assert.Equal(RcodeNameError, Rcode(await AskOverTcp(server.Port, Query(Blocked))));
    }

    [Fact]
    public async Task ItKeepsAnsweringAfterADatagramThatIsNotAQuery()
    {
        await using var server = Start();

        await SendOverUdp(server.Port, [1, 2, 3]);

        Assert.Equal(RcodeNameError, Rcode(await AskOverUdp(server.Port, Query(Blocked))));
    }

    /// <summary>A stray answer is not reflected (the handler sends nothing); the server must carry on.</summary>
    [Fact]
    public async Task ItSaysNothingBackToAnAnswerAndKeepsServing()
    {
        var stray = Query(Allowed);
        stray[2] |= Qr;
        await using var server = Start();

        using (var speaker = Speaker())
        {
            await SendAsync(speaker, server.Port, stray);
            Assert.Null(await ReceiveWithinAsync(speaker, Grace));
        }

        Assert.Equal(RcodeNameError, Rcode(await AskOverUdp(server.Port, Query(Blocked))));
        Assert.Empty(_forwarder.Calls);
    }

    [Fact]
    public async Task AMessageThereIsNoAnswerToLeavesTheConnectionOpen()
    {
        var stray = Query(Allowed);
        stray[2] |= Qr;
        await using var server = Start();
        using var client = await ConnectAsync(server.Port);
        var stream = client.GetStream();

        await WriteFramedAsync(stream, stray);
        await WriteFramedAsync(stream, Query(Blocked));

        Assert.Equal(RcodeNameError, Rcode(await ReadFramedAsync(stream)));
    }

    [Fact]
    public async Task ItKeepsAnsweringAfterAClientThatHangsUpMidMessage()
    {
        await using var server = Start();

        using (var half = await ConnectAsync(server.Port))
        {
            // A length, and then nothing: the message it promised never arrives.
            using var cts = new CancellationTokenSource(Patience);
            await half.GetStream().WriteAsync(new byte[] { 0x00, 0x20 }, cts.Token);
        }

        Assert.Equal(RcodeNameError, Rcode(await AskOverTcp(server.Port, Query(Blocked))));
    }

    /// <summary>A half message leaves a zero-filled buffer tail that would parse; it must not be answered, forwarded or reported.</summary>
    [Fact]
    public async Task AMessageThatArrivedOnlyInPartIsNotServed()
    {
        await using var server = Start();

        using (var half = await ConnectAsync(server.Port))
        {
            var query = Query(Allowed);
            var prefix = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)(query.Length + 8));

            using var cts = new CancellationTokenSource(Patience);
            await half.GetStream().WriteAsync(prefix, cts.Token);
            await half.GetStream().WriteAsync(query, cts.Token);
        }

        Assert.False(await _forwarder.Entered.WaitAsync(Grace));

        Assert.Empty(_forwarder.Calls);
        Assert.Empty(_notified);
        Assert.Equal(RcodeNameError, Rcode(await AskOverTcp(server.Port, Query(Blocked))));
    }

    /// <summary>The idle timeout runs from the last message, not from accept. Built with three seconds of tolerance; queries are a second apart.</summary>
    [Fact]
    public async Task TheSilenceThatClosesAConnectionIsMeasuredFromTheLastMessage()
    {
        await using var server = Start(TimeSpan.FromSeconds(3));
        using var client = await ConnectAsync(server.Port);
        var stream = client.GetStream();

        // Five seconds of asking; a deadline set at accept would cut it off at the fourth query.
        for (ushort query = 1; query <= 5; query++)
        {
            await WriteFramedAsync(stream, Query(Blocked, id: query));
            var answer = await ReadFramedWithinAsync(stream, Patience);

            Assert.NotNull(answer);
            Assert.Equal(query, BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(0, 2)));

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void AConnectionIsGivenFiveSecondsToSaySomething()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), LoopbackDnsServer.ReadTimeout);
    }

    /// <summary>
    /// The client closes its UDP socket before the answer; Windows reports the unreachable port to the sender and the receive loop must survive.
    /// The log assertion proves the platform delivered it, on either the send or the next receive.
    /// </summary>
    [Fact]
    public async Task ItKeepsAnsweringAfterAClientThatVanishedBeforeTheAnswer()
    {
        _forwarder.Answer = Answer(Allowed);
        await using var server = Start();

        using (var gone = Speaker())
        {
            await SendAsync(gone, server.Port, Query(Allowed));
        }

        // Twice: the first query after the loss meets the failed receive; the second proves the loop survives.
        Assert.Equal(RcodeNameError, Rcode(await AskOverUdp(server.Port, Query(Blocked))));
        Assert.Equal(RcodeNameError, Rcode(await AskOverUdp(server.Port, Query(Blocked))));

        Assert.True(
            await ReportedAsync(entry => entry.Level == LogLevel.Debug
                && (entry.Message.Contains("could not be received", StringComparison.Ordinal)
                    || entry.Message.Contains("was not answered", StringComparison.Ordinal))),
            "The client that vanished was never reported, so nothing here met the unreachable port.");
    }

    [Fact]
    public async Task AConnectionThatSaysNothingIsClosed_ButNotAtOnce()
    {
        await using var server = Start();
        using var quiet = await ConnectAsync(server.Port);
        var stream = quiet.GetStream();

        Assert.False(await ClosedWithinAsync(stream, TimeSpan.FromSeconds(2)));
        Assert.True(await ClosedWithinAsync(stream, Patience));
    }

    /// <summary>Eight queries are held inside the handler at once, which a loop serving one datagram at a time could not do.</summary>
    [Fact]
    public async Task ItReadsTheNextQueryWithoutWaitingForTheLastToBeAnswered()
    {
        _forwarder.Answer = Answer(Allowed);
        _forwarder.Blocks = true;
        await using var server = Start();
        var speakers = new List<UdpClient>();

        try
        {
            for (var query = 0; query < 8; query++)
            {
                speakers.Add(await AskingSpeaker(server.Port, Query(Allowed)));
            }

            await EnteredAsync(8);
            _forwarder.Unblock();

            foreach (var speaker in speakers)
            {
                Assert.Equal(RcodeNoError, Rcode(await ReceiveAsync(speaker)));
            }
        }
        finally
        {
            DisposeAll(speakers);
        }
    }

    /// <summary>Pins what the two ceilings are; the tests below measure against the server's own constants.</summary>
    [Fact]
    public void TheCeilingsAreTheOnesThePlanGives()
    {
        Assert.Equal(512, LoopbackDnsServer.MaxQueriesInFlight);
        Assert.Equal(16, LoopbackDnsServer.MaxConnections);
    }

    /// <summary>Slow external server: queries stuck upstream at the forwarder's limit must not delay a blocked name answered from the plan.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AQueryTheForwarderIsNotNeededForIsAnsweredWhileEveryForwardIsStuck(bool overTcp)
    {
        _forwarder.Answer = Answer(Allowed);
        _forwarder.Blocks = true;
        await using var server = Start();
        var speakers = new List<UdpClient>();

        try
        {
            for (var query = 0; query < DnsForwarder.MaxConcurrentForwards; query++)
            {
                speakers.Add(await AskingSpeaker(server.Port, Query(Allowed)));
            }

            await EnteredAsync(DnsForwarder.MaxConcurrentForwards);

            var answer = overTcp
                ? await AskOverTcp(server.Port, Query(Blocked))
                : await AskOverUdp(server.Port, Query(Blocked));

            Assert.Equal(RcodeNameError, Rcode(answer));
        }
        finally
        {
            _forwarder.Unblock();
            DisposeAll(speakers);
        }
    }

    /// <summary>
    /// At <see cref="LoopbackDnsServer.MaxQueriesInFlight"/> no more are taken on; the datagram past it is dropped, not queued, and the loop keeps reading.
    /// </summary>
    [Fact]
    public async Task ADatagramArrivingPastTheCeilingIsDroppedAndTheLoopKeepsReading()
    {
        _forwarder.Answer = Answer(Allowed);
        _forwarder.Blocks = true;
        await using var server = Start();
        using var speaker = Speaker();

        try
        {
            for (var query = 0; query < LoopbackDnsServer.MaxQueriesInFlight; query++)
            {
                await SendAsync(speaker, server.Port, Query(Allowed));
            }

            await EnteredAsync(LoopbackDnsServer.MaxQueriesInFlight);

            await SendAsync(speaker, server.Port, Query(Allowed));

            // The ceiling holds: nothing else is taken on while every slot is held.
            Assert.False(await _forwarder.Entered.WaitAsync(Grace));

            _forwarder.Unblock();

            // What was past it was dropped, not queued: a late answer goes to a client that has asked again.
            Assert.False(await _forwarder.Entered.WaitAsync(Grace));

            // The loop went on reading through all of it.
            Assert.Equal(RcodeNameError, Rcode(await AskOverUdp(server.Port, Query(Blocked))));
        }
        finally
        {
            _forwarder.Unblock();
        }
    }

    /// <summary>Connections are counted apart from datagrams: with every connection slot held open and silent, UDP resolution carries on.</summary>
    [Fact]
    public async Task SilentConnectionsFillingTheirCeilingDoNotCostTheDatagramsAnything()
    {
        await using var server = Start(Patience);
        var connections = new List<TcpClient>();

        try
        {
            for (var open = 0; open < LoopbackDnsServer.MaxConnections; open++)
            {
                connections.Add(await ServedConnection(server.Port));
            }

            Assert.Equal(RcodeNameError, Rcode(await AskOverUdp(server.Port, Query(Blocked))));
        }
        finally
        {
            DisposeAll(connections);
        }
    }

    /// <summary>The last connection that fits is served; the first past it waits for room and is served once one closes.</summary>
    [Fact]
    public async Task AConnectionPastTheCeilingIsServedOnceAnotherCloses()
    {
        await using var server = Start(Patience);
        var connections = new List<TcpClient>();

        try
        {
            for (var open = 0; open < LoopbackDnsServer.MaxConnections; open++)
            {
                connections.Add(await ServedConnection(server.Port));
            }

            using var waiting = await ConnectAsync(server.Port);
            var stream = waiting.GetStream();
            await WriteFramedAsync(stream, Query(Blocked));

            Assert.Null(await ReadFramedWithinAsync(stream, Grace));

            connections[0].Dispose();

            var answer = await ReadFramedWithinAsync(stream, Patience);

            Assert.NotNull(answer);
            Assert.Equal(RcodeNameError, Rcode(answer));
        }
        finally
        {
            DisposeAll(connections);
        }
    }

    /// <summary>Both queries are inside the handler together; a server handling connections one after another could not manage it.</summary>
    [Fact]
    public async Task TwoConnectionsAreServedAtTheSameTime()
    {
        _forwarder.Answer = Answer(Allowed);
        _forwarder.Blocks = true;
        await using var server = Start();
        using var first = await ConnectAsync(server.Port);
        using var second = await ConnectAsync(server.Port);

        await WriteFramedAsync(first.GetStream(), Query(Allowed, id: 0x0101));
        await WriteFramedAsync(second.GetStream(), Query(Allowed, id: 0x0202));

        await EnteredAsync(2);
        _forwarder.Unblock();

        Assert.Equal(RcodeNoError, Rcode(await ReadFramedAsync(first.GetStream())));
        Assert.Equal(RcodeNoError, Rcode(await ReadFramedAsync(second.GetStream())));
    }

    /// <summary>A connection accepted but still waiting for a slot is closed when stop wins the race; nobody else can close it.</summary>
    [Fact]
    public async Task AConnectionAcceptedWithNoRoomLeftIsClosedByAStop()
    {
        var server = Start(Patience);
        var connections = new List<TcpClient>();

        try
        {
            for (var open = 0; open < LoopbackDnsServer.MaxConnections; open++)
            {
                connections.Add(await ServedConnection(server.Port));
            }

            // Accepted by the loop, which is then waiting for a slot that only a close will free.
            using var waiting = await ConnectAsync(server.Port);
            var stream = waiting.GetStream();
            Assert.False(await ClosedWithinAsync(stream, Grace));

            await server.StopAsync();

            Assert.True(await ClosedWithinAsync(stream, Patience));
        }
        finally
        {
            DisposeAll(connections);
            await server.StopAsync();
        }
    }

    /// <summary>A port that could be bound twice would say nothing about who holds it; this pins that it cannot.</summary>
    [Fact]
    public async Task WhileItRunsNeitherSocketCanBeTakenBySomebodyElse()
    {
        await using var server = Start();

        Assert.Throws<SocketException>(() => new UdpClient(new IPEndPoint(IPAddress.Loopback, server.Port)));
        Assert.Throws<SocketException>(
            () =>
            {
                using var listener = new TcpListener(IPAddress.Loopback, server.Port);
                listener.Start();
            });

        // Not even a process that asks to share (SO_REUSEADDR). The server asks for nothing here; this pins the platform behaviour.
        Assert.Throws<SocketException>(() => Share(SocketType.Dgram, ProtocolType.Udp, server.Port));
        Assert.Throws<SocketException>(() => Share(SocketType.Stream, ProtocolType.Tcp, server.Port));
    }

    private static void Share(SocketType type, ProtocolType protocol, int port)
    {
        using var thief = new Socket(AddressFamily.InterNetwork, type, protocol);
        thief.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, optionValue: true);

        thief.Bind(new IPEndPoint(IPAddress.Loopback, port));
    }

    /// <summary>The layer is unavailable and the holder is named in the answer and in the log.</summary>
    [Fact]
    public async Task ItNamesTheHolderWhenThePortIsTaken()
    {
        using var squatter = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var taken = ((IPEndPoint)squatter.Client.LocalEndPoint!).Port;
        await using var server = new LoopbackDnsServer(Handler(), _log, taken);

        Assert.False(server.TryStart(out var holder));

        Assert.False(server.IsRunning);

        // This process is the holder: the socket above is the squatter.
        var us = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        Assert.Contains(us, holder);
        Assert.Contains(
            _log.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains(us, StringComparison.Ordinal));
    }

    /// <summary>UDP free, TCP taken: the TCP holder is named and the UDP socket is released first, or the next attempt would fail against itself.</summary>
    [Fact]
    public async Task ItNamesTheHolderOfTheTcpHalfAndTakesNoSocketAtAll()
    {
        var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();

        try
        {
            var taken = ((IPEndPoint)squatter.LocalEndpoint).Port;
            await using var server = new LoopbackDnsServer(Handler(), _log, taken);

            Assert.False(server.TryStart(out var holder));

            Assert.False(server.IsRunning);

            // The listener is in this process, so this process is the holder.
            var us = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            Assert.Contains(us, holder);
            Assert.Contains(
                _log.Entries,
                entry => entry.Level == LogLevel.Warning && entry.Message.Contains(us, StringComparison.Ordinal));

            using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, taken));
            Assert.NotNull(probe.Client.LocalEndPoint);
        }
        finally
        {
            squatter.Dispose();
        }
    }

    /// <summary>TCP held by another process: the holder must be looked up after the UDP socket is closed, or it names this process.</summary>
    [Fact]
    public async Task ThePortCheckNamesAnotherProcessHoldingTcpAndNotItself()
    {
        using var other = await ForeignHolder.StartAsync(udp: false);

        Assert.False(LoopbackDnsServer.CanTake(other.Port, out var holder));

        Assert.Contains(other.Id.ToString(CultureInfo.InvariantCulture), holder);
        Assert.DoesNotContain(
            $"process {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)})", holder, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePortCheckNamesAnotherProcessHoldingUdp()
    {
        using var other = await ForeignHolder.StartAsync(udp: true);

        Assert.False(LoopbackDnsServer.CanTake(other.Port, out var holder));

        Assert.Contains(other.Id.ToString(CultureInfo.InvariantCulture), holder);
        Assert.DoesNotContain(
            $"process {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)})", holder, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartNamesAnotherProcessHoldingTcpAndNotItself()
    {
        using var other = await ForeignHolder.StartAsync(udp: false);
        await using var server = new LoopbackDnsServer(Handler(), _log, other.Port);

        Assert.False(server.TryStart(out var holder));

        Assert.Contains(other.Id.ToString(CultureInfo.InvariantCulture), holder);
        Assert.DoesNotContain(
            $"process {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)})", holder, StringComparison.Ordinal);
    }

    /// <summary>A listener in a process of its own on an ephemeral loopback port, so nothing of this process is on it.</summary>
    private sealed class ForeignHolder : IDisposable
    {
        private readonly Process _process;

        private ForeignHolder(Process process, int port)
        {
            _process = process;
            Port = port;
        }

        public int Port { get; }

        public int Id => _process.Id;

        public static async Task<ForeignHolder> StartAsync(bool udp)
        {
            var open = udp
                ? "$s=[Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback,0));$p=$s.Client.LocalEndPoint.Port"
                : "$s=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$s.Start();$p=$s.LocalEndpoint.Port";

            var info = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add($"{open};[Console]::Out.WriteLine($p);[Console]::Out.Flush();Start-Sleep 120");

            var process = Process.Start(info)!;

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);

                return new ForeignHolder(process, int.Parse(line!, CultureInfo.InvariantCulture));
            }
            catch
            {
                process.Kill(entireProcessTree: true);
                process.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _process.Kill(entireProcessTree: true);
            _process.Dispose();
        }
    }

    [Fact]
    public async Task StopAsyncReleasesBothPorts()
    {
        var server = Start();
        var port = server.Port;

        await server.StopAsync();

        Assert.False(server.IsRunning);

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        using var tcp = new TcpListener(IPAddress.Loopback, port);
        tcp.Start();
        tcp.Dispose();
    }

    [Fact]
    public async Task DisposeAsyncReleasesBothPorts()
    {
        var server = Start();
        var port = server.Port;

        await server.DisposeAsync();

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        using var tcp = new TcpListener(IPAddress.Loopback, port);
        tcp.Start();
        tcp.Dispose();
    }

    [Fact]
    public async Task AStartAndAStopAreSaidAtInformation()
    {
        var server = Start();

        await server.StopAsync();

        Assert.Equal(2, _log.Entries.Count(entry => entry.Level == LogLevel.Information));
    }

    /// <summary>The layer's re-check starts a server once a minute while a VPN drops its queries; two Information lines a minute would flood the log.</summary>
    [Fact]
    public async Task AServerStartedQuietlyAnswersAndSaysNothingAboveDebugUntilStartedAgainAloud()
    {
        var server = new LoopbackDnsServer(Handler(), _log, port: 0);
        Assert.True(server.TryStart(out _, LogLevel.Debug));

        Assert.Equal(RcodeNameError, Rcode(await AskOverUdp(server.Port, Query(Blocked))));
        await server.StopAsync();

        Assert.DoesNotContain(_log.Entries, entry => entry.Level >= LogLevel.Information);
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("listening", StringComparison.Ordinal));
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("stopped", StringComparison.Ordinal));

        // Quiet is one start's choice, not the server's from then on.
        Assert.True(server.TryStart(out _));
        await server.StopAsync();

        Assert.Equal(2, _log.Entries.Count(entry => entry.Level == LogLevel.Information));
    }

    [Fact]
    public async Task AQuietStartThatIsRefusedSaysSoAtItsOwnLevel()
    {
        using var squatter = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var server = new LoopbackDnsServer(
            Handler(), _log, ((IPEndPoint)squatter.Client.LocalEndPoint!).Port);

        Assert.False(server.TryStart(out _, LogLevel.Debug));

        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("could not take", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Entries, entry => entry.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task ARefusedStartSaysSoAsAWarning()
    {
        using var squatter = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var server = new LoopbackDnsServer(
            Handler(), _log, ((IPEndPoint)squatter.Client.LocalEndPoint!).Port);

        Assert.False(server.TryStart(out _));

        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("could not take", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopAsyncCanBeCalledTwice()
    {
        var server = new LoopbackDnsServer(Handler(), _log, port: 0);
        Assert.True(server.TryStart(out _));

        await server.StopAsync();
        await server.StopAsync();

        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task StopAsyncOnAServerThatNeverStartedDoesNothing()
    {
        var server = new LoopbackDnsServer(Handler(), _log, port: 0);

        await server.StopAsync();

        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task StopAsyncAfterAFailedStartDoesNothing()
    {
        using var squatter = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var server = new LoopbackDnsServer(
            Handler(), _log, ((IPEndPoint)squatter.Client.LocalEndPoint!).Port);
        Assert.False(server.TryStart(out _));

        await server.StopAsync();

        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task StartingATwiceStartedServerIsARefusalAndNotASecondPairOfSockets()
    {
        await using var server = Start();
        var port = server.Port;

        Assert.Throws<InvalidOperationException>(() => server.TryStart(out _));

        Assert.True(server.IsRunning);
        Assert.Equal(port, server.Port);
        Assert.Equal(RcodeNameError, Rcode(await AskOverUdp(port, Query(Blocked))));
    }

    [Fact]
    public async Task AStoppedServerCanBeStartedAgain()
    {
        await using var server = new LoopbackDnsServer(Handler(), _log, port: 0);
        Assert.True(server.TryStart(out _));
        await server.StopAsync();

        Assert.True(server.TryStart(out _));

        Assert.Equal(RcodeNameError, Rcode(await AskOverUdp(server.Port, Query(Blocked))));
    }

    /// <summary>The forwarder is released first, so the query in flight can finish.</summary>
    [Fact]
    public async Task StopAsyncIsSafeWhileQueriesAreInFlight()
    {
        _forwarder.Answer = Answer(Allowed);
        _forwarder.Blocks = true;
        var server = Start();
        using var speaker = await AskingSpeaker(server.Port, Query(Allowed));
        await EnteredAsync(1);

        _forwarder.Unblock();
        await server.StopAsync();

        Assert.False(server.IsRunning);
    }

    /// <summary>The query's token is the server's own, so a stop reaches the forwarder.</summary>
    [Fact]
    public async Task StopAsyncCancelsAQueryThatIsStillWaitingOnTheUpstreamServer()
    {
        _forwarder.Answer = Answer(Allowed);
        _forwarder.Blocks = true;
        var server = Start();
        using var speaker = await AskingSpeaker(server.Port, Query(Allowed));
        await EnteredAsync(1);

        try
        {
            await server.StopAsync().WaitAsync(Patience);

            Assert.False(server.IsRunning);
            Assert.True(_forwarder.Cancelled);
        }
        finally
        {
            _forwarder.Unblock();
        }
    }

    /// <summary>The forwarder takes a second and ignores cancellation; stop must not return before it finishes.</summary>
    [Fact]
    public async Task StopAsyncWaitsForAQueryThatIsStillBeingAnswered()
    {
        _forwarder.Answer = Answer(Allowed);
        _forwarder.Holds = TimeSpan.FromSeconds(1);
        var server = Start();
        using var speaker = await AskingSpeaker(server.Port, Query(Allowed));
        await EnteredAsync(1);

        var watch = Stopwatch.StartNew();
        await server.StopAsync();

        Assert.True(
            watch.Elapsed >= TimeSpan.FromMilliseconds(500),
            $"The stop returned after {watch.ElapsedMilliseconds} ms, without waiting for the query.");
    }

    /// <summary>The same over TCP, where in flight means a connection.</summary>
    [Fact]
    public async Task StopAsyncWaitsForAConnectionThatIsStillBeingAnswered()
    {
        _forwarder.Answer = Answer(Allowed);
        _forwarder.Holds = TimeSpan.FromSeconds(1);
        var server = Start();
        using var client = await ConnectAsync(server.Port);
        await WriteFramedAsync(client.GetStream(), Query(Allowed));
        await EnteredAsync(1);

        var watch = Stopwatch.StartNew();
        await server.StopAsync();

        Assert.True(
            watch.Elapsed >= TimeSpan.FromMilliseconds(500),
            $"The stop returned after {watch.ElapsedMilliseconds} ms, without waiting for the connection.");
    }

    /// <summary>The set a stop waits on is pruned as queries finish; otherwise a week of queries leaks a task each.</summary>
    [Fact]
    public async Task WhatIsWaitedForIsWhatIsStillRunningAndNotEverythingEverServed()
    {
        await using var server = Start();

        for (var query = 0; query < 32; query++)
        {
            Assert.Equal(RcodeNameError, Rcode(await AskOverUdp(server.Port, Query(Blocked))));
        }

        // Not zero: the last query may not be noticed yet, and the accept loop's connection is not counted. Far short of thirty-two.
        Assert.InRange(server.Tracked, 0, 4);
    }

    /// <summary>The forwarder ignores the token; the wait on in-flight work is bounded so stop still ends.</summary>
    [Fact]
    public async Task StopAsyncReturnsEvenWhenAQueryIgnoresBeingCancelled()
    {
        _forwarder.Answer = Answer(Allowed);
        _forwarder.Blocks = true;
        _forwarder.IgnoresCancellation = true;
        var server = Start();
        using var speaker = await AskingSpeaker(server.Port, Query(Allowed));
        await EnteredAsync(1);

        try
        {
            await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.False(server.IsRunning);
        }
        finally
        {
            _forwarder.Unblock();
        }
    }

    private DnsRequestHandler Handler()
    {
        var handler = new DnsRequestHandler(
            _forwarder,
            new DnsResponseCache(_clock),
            _notified.Enqueue,
            NullLogger<DnsRequestHandler>.Instance);

        handler.UseRules([new SiteRule(Blocked, includeSubdomains: false)]);
        return handler;
    }

    /// <summary>A running server on a port the platform picked. Never <see cref="LoopbackDnsServer.DnsPort"/>.</summary>
    private LoopbackDnsServer Start() => Started(() => new LoopbackDnsServer(Handler(), _log, port: 0));

    /// <summary>The same, tolerating the given silence instead of the service's five seconds.</summary>
    private LoopbackDnsServer Start(TimeSpan readTimeout) => Started(() => new LoopbackDnsServer(Handler(), _log, 0, readTimeout));

    /// <summary>
    /// Port 0 gets a free UDP port, and TCP is then taken on the same number; under a parallel suite
    /// another socket can already hold that TCP port. Another number is tried then. Port 53 never meets this.
    /// </summary>
    private static LoopbackDnsServer Started(Func<LoopbackDnsServer> create)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var server = create();
            if (server.TryStart(out _))
            {
                return server;
            }
        }

        Assert.Fail("No free UDP and TCP port pair on loopback in ten attempts.");
        return null!;
    }

    private async Task EnteredAsync(int count)
    {
        for (var query = 0; query < count; query++)
        {
            Assert.True(await _forwarder.Entered.WaitAsync(Patience));
        }
    }

    /// <summary>Whether the server logged a match within <see cref="Patience"/>. Polled because a log entry has no event.</summary>
    private async Task<bool> ReportedAsync(Func<(LogLevel Level, string Message), bool> matches)
    {
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < Patience)
        {
            if (_log.Entries.Any(matches))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        return false;
    }

    private static byte Rcode(byte[] message) => (byte)(message[3] & 0x0F);

    private static void DisposeAll<T>(List<T> clients)
        where T : IDisposable
    {
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    private static UdpClient Speaker() => new(new IPEndPoint(IPAddress.Loopback, 0));

    private static async Task<byte[]> AskOverUdp(int port, byte[] query)
    {
        using var speaker = Speaker();
        await SendAsync(speaker, port, query);

        return await ReceiveAsync(speaker);
    }

    private static async Task SendOverUdp(int port, byte[] datagram)
    {
        using var speaker = Speaker();
        await SendAsync(speaker, port, datagram);
    }

    /// <summary>A client that has asked and has not been answered yet, for the caller to hold.</summary>
    private static async Task<UdpClient> AskingSpeaker(int port, byte[] query)
    {
        var speaker = Speaker();
        await SendAsync(speaker, port, query);

        return speaker;
    }

    private static async Task SendAsync(UdpClient speaker, int port, byte[] datagram) =>
        await speaker.SendAsync(datagram, datagram.Length, new IPEndPoint(IPAddress.Loopback, port));

    private static async Task<byte[]> ReceiveAsync(UdpClient speaker)
    {
        using var cts = new CancellationTokenSource(Patience);
        var received = await speaker.ReceiveAsync(cts.Token);

        return received.Buffer;
    }

    private static async Task<byte[]?> ReceiveWithinAsync(UdpClient speaker, TimeSpan within)
    {
        using var cts = new CancellationTokenSource(within);

        try
        {
            var received = await speaker.ReceiveAsync(cts.Token);

            return received.Buffer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();

        try
        {
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>A connection the server has certainly accepted; the answer to the query on it is the proof.</summary>
    private async Task<TcpClient> ServedConnection(int port)
    {
        var client = await ConnectAsync(port);

        try
        {
            var stream = client.GetStream();
            await WriteFramedAsync(stream, Query(Blocked));

            Assert.Equal(RcodeNameError, Rcode(await ReadFramedAsync(stream)));

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> AskOverTcp(int port, byte[] query)
    {
        using var client = await ConnectAsync(port);
        var stream = client.GetStream();

        await WriteFramedAsync(stream, query);

        return await ReadFramedAsync(stream);
    }

    private static async Task WriteFramedAsync(NetworkStream stream, byte[] message)
    {
        var prefix = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)message.Length);

        using var cts = new CancellationTokenSource(Patience);
        await stream.WriteAsync(prefix, cts.Token);
        await stream.WriteAsync(message, cts.Token);
    }

    private static async Task<byte[]> ReadFramedAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(Patience);

        var prefix = new byte[2];
        await stream.ReadExactlyAsync(prefix, cts.Token);

        var message = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)];
        await stream.ReadExactlyAsync(message, cts.Token);

        return message;
    }

    private static async Task<byte[]?> ReadFramedWithinAsync(NetworkStream stream, TimeSpan within)
    {
        using var cts = new CancellationTokenSource(within);

        try
        {
            var prefix = new byte[2];
            await stream.ReadExactlyAsync(prefix, cts.Token);

            var message = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)];
            await stream.ReadExactlyAsync(message, cts.Token);

            return message;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Whether the other end closed in time; a reset counts as a close.</summary>
    private static async Task<bool> ClosedWithinAsync(NetworkStream stream, TimeSpan within)
    {
        using var cts = new CancellationTokenSource(within);

        try
        {
            return await stream.ReadAsync(new byte[1], cts.Token) == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static byte[] Query(string name, ushort id = 1)
    {
        var labels = new List<byte>();

        foreach (var label in name.Split('.'))
        {
            labels.Add((byte)label.Length);
            labels.AddRange(Encoding.ASCII.GetBytes(label));
        }

        labels.Add(0);

        var query = new byte[DnsQuery.HeaderLength + labels.Count + 4];
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(0, 2), id);
        query[2] = RecursionDesired;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), 1); // QDCOUNT
        labels.CopyTo(query, DnsQuery.HeaderLength);

        var tail = DnsQuery.HeaderLength + labels.Count;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(tail, 2), TypeA);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(tail + 2, 2), ClassInternet);

        return query;
    }

    /// <summary>The forwarder's reply: no records, so the cache holds nothing. Padded to <paramref name="length"/> when given.</summary>
    private static byte[] Answer(string name, int length = 0)
    {
        var answer = Query(name);
        answer[2] |= Qr;

        if (length <= answer.Length)
        {
            return answer;
        }

        var padded = new byte[length];
        answer.CopyTo(padded, 0);
        Array.Fill(padded, (byte)0xAB, answer.Length, length - answer.Length);

        return padded;
    }

    /// <summary>The upstream fake; <see cref="Entered"/> is released per query so tests wait on the query, not the clock.</summary>
    private sealed class TestForwarder : IDnsForwarder
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SemaphoreSlim Entered { get; } = new(0);

        public ConcurrentQueue<(byte[] Query, bool OverTcp)> Calls { get; } = new();

        public byte[]? Answer { get; set; }

        public bool Blocks { get; set; }

        /// <summary>Whether a held query also ignores its token.</summary>
        public bool IgnoresCancellation { get; set; }

        public bool Cancelled { get; private set; }

        /// <summary>How long a query takes, ignoring cancellation: a slow upstream, not a dead one.</summary>
        public TimeSpan Holds { get; set; }

        public void Unblock() => _held.TrySetResult();

        public async Task<byte[]?> ForwardAsync(byte[] query, bool overTcp, CancellationToken ct)
        {
            Calls.Enqueue(([.. query], overTcp));
            Entered.Release();

            if (Holds > TimeSpan.Zero)
            {
                await Task.Delay(Holds, CancellationToken.None).ConfigureAwait(false);
            }

            if (Blocks && IgnoresCancellation)
            {
                await _held.Task.ConfigureAwait(false);
            }
            else if (Blocks)
            {
                try
                {
                    await _held.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    throw;
                }
            }

            // A copy, as an answer off a socket is a fresh array; handing out the field would let a test compare against an array the server could edit.
            byte[]? answer = Answer is { } held ? [.. held] : null;

            return answer;
        }
    }
}
