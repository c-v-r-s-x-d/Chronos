using System.Net;
using Chronos.Service.Dns;
using Chronos.Service.Wfp;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Tests;

public sealed class DnsForwarderTests
{
    private static readonly IPAddress Server1 = IPAddress.Parse("8.8.8.8");
    private static readonly IPAddress Server2 = IPAddress.Parse("9.9.9.9");
    private static readonly IPAddress Server3 = IPAddress.Parse("1.1.1.1");

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(750);

    private readonly CapturingLogger<DnsForwarder> _logger = new();

    [Fact]
    public void TheDefaultTimeoutIsTheOneThePlanGives()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), DnsForwarder.DefaultTimeout);
    }

    [Fact]
    public void ATransportIsRequired()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DnsForwarder(null!, [Server1], DnsForwarder.DefaultTimeout, _logger));
    }

    // The ParamName, not only the type: without the explicit ThrowIfNull, upstream.ToArray() throws an
    // ArgumentNullException naming "source", which a type-only check would call a pass.
    [Fact]
    public void AServerListIsRequired()
    {
        var failure = Assert.Throws<ArgumentNullException>(
            () => new DnsForwarder(new FakeTransport(), null!, DnsForwarder.DefaultTimeout, _logger));

        Assert.Equal("upstream", failure.ParamName);
    }

    [Fact]
    public void ALoggerIsRequired()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DnsForwarder(new FakeTransport(), [Server1], DnsForwarder.DefaultTimeout, null!));
    }

    [Fact]
    public void AnEmptyServerListIsRefused()
    {
        var failure = Assert.Throws<ArgumentException>(
            () => new DnsForwarder(new FakeTransport(), [], DnsForwarder.DefaultTimeout, _logger));

        Assert.Equal("upstream", failure.ParamName);
    }

    // One server is the ordinary case (one DHCP resolver) and the boundary next to the empty-list refusal.
    [Fact]
    public void ASingleServerIsAllowed()
    {
        _ = new DnsForwarder(new FakeTransport(), [Server1], DnsForwarder.DefaultTimeout, _logger);
    }

    [Fact]
    public void ALoopbackServerIsRefused()
    {
        var failure = Assert.Throws<ArgumentException>(
            () => new DnsForwarder(new FakeTransport(), [IPAddress.Loopback], DnsForwarder.DefaultTimeout, _logger));

        Assert.Contains("loopback", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("upstream", failure.ParamName);
    }

    // The whole 127.0.0.0/8 block and ::1 reach the same sockets as 127.0.0.1, and a datagram to
    // 0.0.0.0 or :: goes to the local host. IPAddress.IsLoopback catches neither unspecified address,
    // and an equality check against 127.0.0.1 passes only the first case.
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void EveryAddressThatComesBackToThisMachineIsRefused(string address)
    {
        var failure = Assert.Throws<ArgumentException>(
            () => new DnsForwarder(
                new FakeTransport(), [IPAddress.Parse(address)], DnsForwarder.DefaultTimeout, _logger));

        Assert.Equal("upstream", failure.ParamName);
    }

    // Zero would switch forwarding off, and a negative value throws out of CancelAfter inside the
    // transport on every query.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ATimeoutThatIsNotPositiveIsRefused(int seconds)
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DnsForwarder(
                new FakeTransport(), [Server1], TimeSpan.FromSeconds(seconds), _logger));

        Assert.Equal("timeout", failure.ParamName);
    }

    // The refusal is about the whole list: a saved interface with a real primary and a 127.0.0.1
    // secondary (what a half-applied run leaves) must not build a forwarder either.
    [Fact]
    public void ALoopbackServerAnywhereInTheListIsRefused()
    {
        Assert.Throws<ArgumentException>(
            () => new DnsForwarder(
                new FakeTransport(), [Server1, IPAddress.Loopback], DnsForwarder.DefaultTimeout, _logger));
    }

    // The list is checked once, at construction. Keeping the caller's own collection would make that
    // check say nothing about the servers asked later.
    [Fact]
    public async Task TheServerListIsCopiedRatherThanBorrowed()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = Answer(0xA1);

        var servers = new List<IPAddress> { Server1 };
        var forwarder = new DnsForwarder(transport, servers, Timeout, _logger);

        servers.Clear();
        servers.Add(IPAddress.Loopback);

        Assert.NotNull(await forwarder.ForwardAsync(Query(), overTcp: false, CancellationToken.None));
        Assert.Equal([Server1.ToString()], transport.Asked);
    }

    [Fact]
    public async Task AQueryIsRequired()
    {
        var forwarder = Make(new FakeTransport(), Server1);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => forwarder.ForwardAsync(null!, overTcp: false, CancellationToken.None));
    }

    // Compared against a freshly built query, not the array passed in: the transport gets the caller's
    // array, so comparing it with itself would pass a forwarder that rewrote the bytes in place.
    [Fact]
    public async Task ForwardAsync_SendsTheQueryItWasGivenByteForByte()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = Answer(0xA1);
        var forwarder = Make(transport, Server1);

        await forwarder.ForwardAsync(Query(), overTcp: false, CancellationToken.None);

        Assert.Equal(Query(), transport.Calls[0].Query);
    }

    // The same query to the second server, the one the walk exists for. A fake that picks its answer
    // by server address never looks at the bytes, so only this assertion catches a forwarder that
    // sends the query verbatim only once.
    [Fact]
    public async Task ForwardAsync_SendsTheSameQueryToEveryServerItAsks()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server3.ToString()] = Answer(0xC3);

        await Make(transport, Server1, Server2, Server3)
            .ForwardAsync(Query(), overTcp: false, CancellationToken.None);

        Assert.Equal(3, transport.Calls.Count);
        Assert.All(transport.Calls, call => Assert.Equal(Query(), call.Query));
    }

    // The caller's buffer is the receive buffer in the layer above, and the cache name is read from
    // it after this call. Editing it in place would silently corrupt that, so nothing may write to it.
    [Fact]
    public async Task ForwardAsync_LeavesTheCallersBufferAlone()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server2.ToString()] = Answer(0xB2);
        var query = Query();
        var untouched = (byte[])query.Clone();

        await Make(transport, Server1, Server2).ForwardAsync(query, overTcp: false, CancellationToken.None);

        Assert.Equal(untouched, query);
    }

    [Fact]
    public async Task ForwardAsync_ReturnsTheAnswerUnchanged()
    {
        var answer = Answer(0xA1);
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = answer;

        var forwarded = await Make(transport, Server1)
            .ForwardAsync(Query(), overTcp: false, CancellationToken.None);

        Assert.Equal(answer, forwarded);
    }

    [Fact]
    public async Task ForwardAsync_AsksTheOneServerItHas()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = Answer(0xA1);

        Assert.NotNull(await Make(transport, Server1).ForwardAsync(Query(), false, CancellationToken.None));
        Assert.Equal([Server1.ToString()], transport.Asked);
    }

    [Fact]
    public async Task ForwardAsync_AnswersNullWhenTheOnlyServerSaysNothing()
    {
        var transport = new FakeTransport();

        Assert.Null(await Make(transport, Server1).ForwardAsync(Query(), false, CancellationToken.None));
        Assert.Equal([Server1.ToString()], transport.Asked);
    }

    [Fact]
    public async Task ForwardAsync_TriesTheNextServerWhenTheFirstSaysNothing()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server2.ToString()] = Answer(0xB2);

        var answer = await Make(transport, Server1, Server2).ForwardAsync(Query(), false, CancellationToken.None);

        Assert.Equal(Answer(0xB2), answer);
        Assert.Equal([Server1.ToString(), Server2.ToString()], transport.Asked);
    }

    // Once somebody has answered, nobody else is asked; a forwarder that asked them all would still
    // return the right bytes.
    [Fact]
    public async Task ForwardAsync_StopsAtTheFirstServerThatAnswers()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = Answer(0xA1);
        transport.UdpAnswers[Server2.ToString()] = Answer(0xB2);

        var answer = await Make(transport, Server1, Server2).ForwardAsync(Query(), false, CancellationToken.None);

        Assert.Equal(Answer(0xA1), answer);
        Assert.Equal([Server1.ToString()], transport.Asked);
    }

    [Fact]
    public async Task ForwardAsync_ReachesTheLastServerOfThree()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server3.ToString()] = Answer(0xC3);

        var answer = await Make(transport, Server1, Server2, Server3)
            .ForwardAsync(Query(), false, CancellationToken.None);

        Assert.Equal(Answer(0xC3), answer);
        Assert.Equal([Server1.ToString(), Server2.ToString(), Server3.ToString()], transport.Asked);
    }

    // The order is the interface's. Ordering or reversing the list would demote the resolver the network intends first.
    [Fact]
    public async Task ForwardAsync_AsksTheServersInTheOrderItWasGiven()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = Answer(0xA1);

        await Make(transport, Server3, Server2, Server1).ForwardAsync(Query(), false, CancellationToken.None);

        Assert.Equal([Server3.ToString(), Server2.ToString(), Server1.ToString()], transport.Asked);
    }

    [Fact]
    public async Task ForwardAsync_AnswersNullWhenNoServerAnswered()
    {
        var transport = new FakeTransport();

        var answer = await Make(transport, Server1, Server2).ForwardAsync(Query(), false, CancellationToken.None);

        // Null, not an empty array: the layer above turns "nobody answered" into SERVFAIL, and zero bytes
        // is a message it would try to send.
        Assert.Null(answer);
        Assert.Equal([Server1.ToString(), Server2.ToString()], transport.Asked);
    }

    // A socket can hand back no bytes or a runt. The smallest DNS message is a twelve-octet header
    // (RFC 1035 4.1.1), so anything shorter counts as this server saying nothing. Eleven and twelve
    // are both sides of the boundary.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(11)]
    public async Task ForwardAsync_TreatsABufferShorterThanAHeaderAsNothingAndMovesOn(int length)
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = Runt(length);
        transport.UdpAnswers[Server2.ToString()] = Answer(0xB2);

        var answer = await Make(transport, Server1, Server2).ForwardAsync(Query(), false, CancellationToken.None);

        Assert.Equal(Answer(0xB2), answer);
        Assert.Equal([Server1.ToString(), Server2.ToString()], transport.Asked);
    }

    // Twelve octets is a message: a header alone is what a server sends when it has nothing to say
    // about a name. It goes to the client as it is.
    [Fact]
    public async Task ForwardAsync_PassesOnAHeaderWithNothingAfterIt()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = HeaderOnlyAnswer();

        var answer = await Make(transport, Server1, Server2).ForwardAsync(Query(), false, CancellationToken.None);

        Assert.Equal(HeaderOnlyAnswer(), answer);
        Assert.Equal([Server1.ToString()], transport.Asked);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task ForwardAsync_AnswersNullWhenTheOnlyBufferWasShorterThanAHeader(int length)
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = Runt(length);

        Assert.Null(await Make(transport, Server1).ForwardAsync(Query(), false, CancellationToken.None));
    }

    // The identifier the client chose comes back in the first two octets. A different one answers
    // somebody else's question (a stray reply or an off-path guess, RFC 5452), so it counts as silence
    // and the walk goes on. The pre-resolver on L3 makes the same check.
    [Fact]
    public async Task ForwardAsync_TreatsAnAnswerWithAForeignIdentifierAsNothingAndMovesOn()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = AnswerWithId(0x5678, 0xA1);
        transport.UdpAnswers[Server2.ToString()] = Answer(0xB2);

        var answer = await Make(transport, Server1, Server2).ForwardAsync(Query(), false, CancellationToken.None);

        Assert.Equal(Answer(0xB2), answer);
        Assert.Equal([Server1.ToString(), Server2.ToString()], transport.Asked);
    }

    // Both octets, not just one: an identifier differing only in its low half is still not ours.
    [Theory]
    [InlineData(0x1200)]
    [InlineData(0x0034)]
    public async Task ForwardAsync_AnswersNullWhenTheOnlyAnswerCarriedAForeignIdentifier(int id)
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = AnswerWithId((ushort)id, 0xA1);

        Assert.Null(await Make(transport, Server1).ForwardAsync(Query(), false, CancellationToken.None));
    }

    [Fact]
    public async Task ForwardAsync_UsesUdpWhenTheClientCameOverUdp()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = Answer(0xA1);

        var answer = await Make(transport, Server1).ForwardAsync(Query(), overTcp: false, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Single(transport.UdpCalls);
        Assert.Empty(transport.TcpCalls);
    }

    [Fact]
    public async Task ForwardAsync_UsesTcpWhenTheClientCameOverTcp()
    {
        var transport = new FakeTransport();
        transport.TcpAnswers[Server1.ToString()] = Answer(0xA1);

        var answer = await Make(transport, Server1).ForwardAsync(Query(), overTcp: true, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Empty(transport.UdpCalls);
        Assert.Single(transport.TcpCalls);
    }

    // The transport choice belongs to every attempt: reading the flag once and falling back to UDP
    // would silently downgrade the second server.
    [Fact]
    public async Task ForwardAsync_StaysOnTcpWhileItWalksTheServers()
    {
        var transport = new FakeTransport();
        transport.TcpAnswers[Server2.ToString()] = Answer(0xB2);
        transport.UdpAnswers[Server2.ToString()] = Answer(0xEE);

        var answer = await Make(transport, Server1, Server2).ForwardAsync(Query(), overTcp: true, CancellationToken.None);

        Assert.Equal(Answer(0xB2), answer);
        Assert.Empty(transport.UdpCalls);
        Assert.Equal(2, transport.TcpCalls.Count);
    }

    // A truncated answer is the client's business: it must ask again over TCP, so the answer carries
    // the TC bit. L3's pre-resolver does retry, since this product wants those addresses itself.
    [Fact]
    public async Task ForwardAsync_DoesNotRetryOverTcpForAUdpClient()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = TruncatedAnswer();
        transport.TcpAnswers[Server1.ToString()] = Answer(0xFF);

        var answer = await Make(transport, Server1).ForwardAsync(Query(), overTcp: false, CancellationToken.None);

        Assert.Equal(TruncatedAnswer(), answer);
        Assert.Empty(transport.TcpCalls);
    }

    // SERVFAIL is a server declining, not an answer to carry back. The second server is there for
    // the case where the first fails, so the walk moves on, as L3's pre-resolver does.
    [Fact]
    public async Task ForwardAsync_AsksTheNextServerWhenOneRefusesWithServerFailure()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = ServerFailureAnswer(0xD1);
        transport.UdpAnswers[Server2.ToString()] = Answer(0xB2);

        var answer = await Make(transport, Server1, Server2).ForwardAsync(Query(), false, CancellationToken.None);

        Assert.Equal(Answer(0xB2), answer);
        Assert.Equal([Server1.ToString(), Server2.ToString()], transport.Asked);
    }

    // When every server refuses, the refusal is what the client gets: it is a real answer and says more
    // than a SERVFAIL of our own making. The last one, so the bytes came from the last server asked.
    [Fact]
    public async Task ForwardAsync_PassesOnTheLastServerFailureWhenEveryServerRefused()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = ServerFailureAnswer(0xD1);
        transport.UdpAnswers[Server2.ToString()] = ServerFailureAnswer(0xD2);

        var answer = await Make(transport, Server1, Server2).ForwardAsync(Query(), false, CancellationToken.None);

        Assert.Equal(ServerFailureAnswer(0xD2), answer);
        Assert.Equal([Server1.ToString(), Server2.ToString()], transport.Asked);
    }

    // A refusal already in hand neither stops the walk from producing a real answer nor outranks one.
    [Fact]
    public async Task ForwardAsync_PrefersAnAnswerFromALaterServerOverAnEarlierRefusal()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = ServerFailureAnswer(0xD1);
        transport.UdpAnswers[Server3.ToString()] = Answer(0xC3);

        var answer = await Make(transport, Server1, Server2, Server3)
            .ForwardAsync(Query(), false, CancellationToken.None);

        Assert.Equal(Answer(0xC3), answer);
    }

    // Only SERVFAIL is a refusal to walk past. NXDOMAIN is the real answer for a name that does not
    // exist; asking the next server would turn every wrong guess a browser makes into a full round.
    [Fact]
    public async Task ForwardAsync_PassesOnANameErrorRatherThanAskingTheNextServer()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = NameErrorAnswer();
        transport.UdpAnswers[Server2.ToString()] = Answer(0xB2);

        var answer = await Make(transport, Server1, Server2).ForwardAsync(Query(), false, CancellationToken.None);

        Assert.Equal(NameErrorAnswer(), answer);
        Assert.Equal([Server1.ToString()], transport.Asked);
    }

    // The refusal has to be this query's: a SERVFAIL with somebody else's identifier is not an answer.
    [Fact]
    public async Task ForwardAsync_AnswersNullWhenTheOnlyRefusalCarriedAForeignIdentifier()
    {
        var transport = new FakeTransport();
        var strayRefusal = ServerFailureAnswer(0xD1);
        strayRefusal[0] = 0x56;
        strayRefusal[1] = 0x78;
        transport.UdpAnswers[Server1.ToString()] = strayRefusal;

        Assert.Null(await Make(transport, Server1).ForwardAsync(Query(), false, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForwardAsync_GivesTheTransportTheTimeoutItWasBuiltWith(bool overTcp)
    {
        var transport = new FakeTransport();

        await Make(transport, Server1, Server2).ForwardAsync(Query(), overTcp, CancellationToken.None);

        Assert.Equal([Timeout, Timeout], transport.Calls.Select(call => call.Timeout));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForwardAsync_HandsTheCallersTokenToTheTransport(bool overTcp)
    {
        using var source = new CancellationTokenSource();
        var transport = new FakeTransport();

        await Make(transport, Server1).ForwardAsync(Query(), overTcp, source.Token);

        Assert.Equal(source.Token, transport.Calls[0].Token);
    }

    [Fact]
    public async Task ForwardAsync_StopsWhenTheCallerCancels()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // The server would answer and the fake ignores the token, so only the forwarder checking for itself can end this call early.
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = Answer(0xA1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Make(transport, Server1).ForwardAsync(Query(), false, cancelled.Token));

        Assert.Empty(transport.Calls);
    }

    // Cancelling must be noticed between servers too: a long list and a dead network is where a stopping service waits longest.
    [Fact]
    public async Task ForwardAsync_StopsBetweenServersWhenTheCallerCancels()
    {
        using var source = new CancellationTokenSource();
        var transport = new FakeTransport { WhenAsked = source.Cancel };
        transport.UdpAnswers[Server2.ToString()] = Answer(0xB2);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Make(transport, Server1, Server2).ForwardAsync(Query(), false, source.Token));

        Assert.Equal([Server1.ToString()], transport.Asked);
    }

    // The two tests above never get past the check at the top of an iteration. UdpDnsTransport returns
    // null for a timeout but rethrows when the caller cancelled; a forwarder that swallowed exceptions
    // around the send would work through the whole list before answering null.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForwardAsync_LetsACancellationFromTheTransportOut(bool overTcp)
    {
        var transport = new CancellingTransport();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Make(transport, Server1, Server2).ForwardAsync(Query(), overTcp, CancellationToken.None));

        Assert.Equal([Server1.ToString()], transport.Asked);
    }

    // The ceiling lives here, where a query costs a socket and a wait on someone else's network, not
    // in the server's receive loop, where it would also cost every locally answered query.
    [Fact]
    public void TheCeilingOnWhatIsOutAtOnceIsTheOneThePlanGives()
    {
        Assert.Equal(64, DnsForwarder.MaxConcurrentForwards);
    }

    [Fact]
    public async Task ForwardAsync_SendsNoMoreAtOnceThanItsCeilingAndThenTheNextOne()
    {
        var transport = new HeldTransport();
        var forwarder = new DnsForwarder(transport, [Server1], TimeSpan.FromSeconds(30), _logger);
        var sent = new List<Task<byte[]?>>();

        try
        {
            for (var query = 0; query < DnsForwarder.MaxConcurrentForwards; query++)
            {
                sent.Add(forwarder.ForwardAsync(Query(), false, CancellationToken.None));
            }

            await transport.EnteredAsync(DnsForwarder.MaxConcurrentForwards);

            var waiting = forwarder.ForwardAsync(Query(), false, CancellationToken.None);
            sent.Add(waiting);

            // The ceiling: nothing else reaches the transport while every slot is held.
            Assert.False(await transport.Entered.WaitAsync(TimeSpan.FromMilliseconds(750)));

            // And a ceiling is not a refusal: the moment a slot comes free, the query goes out.
            transport.Release();

            Assert.True(await transport.Entered.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.NotNull(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            transport.Release();
            await Task.WhenAll(sent);
        }
    }

    // A query that cannot be sent within one server's time is not queued behind the rest: the client
    // is owed the SERVFAIL that null turns into, not an answer long after it gave up. Debug and
    // nothing above, because a query names a domain.
    [Fact]
    public async Task ForwardAsync_AnswersNothingWhenNoSlotComesFreeWithinTheTimeout()
    {
        var transport = new HeldTransport();
        var forwarder = Make(transport, Server1);
        var sent = new List<Task<byte[]?>>();

        try
        {
            for (var query = 0; query < DnsForwarder.MaxConcurrentForwards; query++)
            {
                sent.Add(forwarder.ForwardAsync(Query(), false, CancellationToken.None));
            }

            await transport.EnteredAsync(DnsForwarder.MaxConcurrentForwards);

            // Bounded, so a forwarder without a ceiling fails here rather than waiting on a server that never answers.
            Assert.Null(
                await forwarder.ForwardAsync(Query(), false, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(DnsForwarder.MaxConcurrentForwards, transport.Calls);
            Assert.DoesNotContain(_logger.Entries, entry => entry.Level > LogLevel.Debug);
            Assert.Contains(
                _logger.Entries,
                entry => entry.Message.Contains("forwarding slots", StringComparison.Ordinal));
        }
        finally
        {
            transport.Release();
            await Task.WhenAll(sent);
        }
    }

    // The query bytes carry a domain, so nothing about a forwarded query goes above Debug. The one
    // line this class writes says only how many servers were silent.
    [Fact]
    public async Task ForwardAsync_SaysNothingAboveDebugAboutAQueryItCouldNotGetAnswered()
    {
        await Make(new FakeTransport(), Server1, Server2).ForwardAsync(Query(), false, CancellationToken.None);

        Assert.DoesNotContain(_logger.Entries, entry => entry.Level > LogLevel.Debug);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains('2'));
    }

    [Fact]
    public async Task ForwardAsync_SaysNothingAboveDebugAboutAQueryThatWasAnswered()
    {
        var transport = new FakeTransport();
        transport.UdpAnswers[Server1.ToString()] = Answer(0xA1);

        await Make(transport, Server1).ForwardAsync(Query(), false, CancellationToken.None);

        Assert.DoesNotContain(_logger.Entries, entry => entry.Level > LogLevel.Debug);
    }

    private DnsForwarder Make(IDnsTransport transport, params IPAddress[] servers) =>
        new(transport, servers, Timeout, _logger);

    private static byte[] Query() => DnsMessage.BuildQuery("example.com", DnsRecordType.A, 0x1234);

    /// <summary>A response whose body the forwarder never reads: a header with the query's identifier and one byte that tells this answer from the next.</summary>
    private static byte[] Answer(byte marker) => AnswerWithId(0x1234, marker);

    private static byte[] AnswerWithId(ushort id, byte marker)
    {
        var answer = new byte[13];
        answer[0] = (byte)(id >> 8);
        answer[1] = (byte)(id & 0xFF);
        answer[2] = 0x81; // QR, RD
        answer[3] = 0x80; // RA, NOERROR
        answer[12] = marker;
        return answer;
    }

    /// <summary>The twelve-octet header alone, the shortest thing that is still a message.</summary>
    private static byte[] HeaderOnlyAnswer() => Answer(0).AsSpan(0, 12).ToArray();

    /// <summary>Fewer octets than a header.</summary>
    private static byte[] Runt(int length) => Answer(0).AsSpan(0, length).ToArray();

    private static byte[] TruncatedAnswer()
    {
        var answer = Answer(0xCC);
        answer[2] |= 0x02; // TC
        return answer;
    }

    private static byte[] ServerFailureAnswer(byte marker)
    {
        var answer = Answer(marker);
        answer[3] = 0x82; // RA, SERVFAIL
        return answer;
    }

    private static byte[] NameErrorAnswer()
    {
        var answer = Answer(0xEE);
        answer[3] = 0x83; // RA, NXDOMAIN
        return answer;
    }

    private sealed class FakeTransport : IDnsTransport
    {
        public List<Call> Calls { get; } = [];

        public Dictionary<string, byte[]> UdpAnswers { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, byte[]> TcpAnswers { get; } = new(StringComparer.Ordinal);

        /// <summary>Run as a server is asked, before it answers.</summary>
        public Action? WhenAsked { get; set; }

        public IReadOnlyList<string> Asked => [.. Calls.Select(call => call.Server.ToString())];

        public IReadOnlyList<Call> UdpCalls => [.. Calls.Where(call => !call.OverTcp)];

        public IReadOnlyList<Call> TcpCalls => [.. Calls.Where(call => call.OverTcp)];

        public Task<byte[]?> SendUdpAsync(IPAddress server, byte[] query, TimeSpan timeout, CancellationToken ct) =>
            Respond(server, query, timeout, ct, overTcp: false, UdpAnswers);

        public Task<byte[]?> SendTcpAsync(IPAddress server, byte[] query, TimeSpan timeout, CancellationToken ct) =>
            Respond(server, query, timeout, ct, overTcp: true, TcpAnswers);

        // Deliberately ignores the token: the real transport reports "no answer" rather than throwing,
        // so an OperationCanceledException here came from the forwarder.
        private Task<byte[]?> Respond(
            IPAddress server,
            byte[] query,
            TimeSpan timeout,
            CancellationToken ct,
            bool overTcp,
            Dictionary<string, byte[]> answers)
        {
            Calls.Add(new Call(server, overTcp, query, timeout, ct));
            WhenAsked?.Invoke();

            return Task.FromResult(answers.TryGetValue(server.ToString(), out var answer) ? answer : null);
        }
    }

    /// <summary>
    /// An upstream server that answers nobody until it is let go. <see cref="Entered"/> is released as
    /// each query arrives, so a test waits on the query rather than the clock; the count is
    /// interlocked because the queries are in flight together.
    /// </summary>
    private sealed class HeldTransport : IDnsTransport
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _calls;

        public SemaphoreSlim Entered { get; } = new(0);

        public int Calls => Volatile.Read(ref _calls);

        public void Release() => _held.TrySetResult();

        public async Task EnteredAsync(int count)
        {
            for (var query = 0; query < count; query++)
            {
                Assert.True(await Entered.WaitAsync(TimeSpan.FromSeconds(10)));
            }
        }

        public Task<byte[]?> SendUdpAsync(IPAddress server, byte[] query, TimeSpan timeout, CancellationToken ct) =>
            Hold();

        public Task<byte[]?> SendTcpAsync(IPAddress server, byte[] query, TimeSpan timeout, CancellationToken ct) =>
            Hold();

        private async Task<byte[]?> Hold()
        {
            Interlocked.Increment(ref _calls);
            Entered.Release();

            await _held.Task.ConfigureAwait(false);

            return Answer(0xC3);
        }
    }

    /// <summary>What <see cref="UdpDnsTransport"/> does when the caller cancels mid-send: the exception comes out of the send itself, faulted after a yield like a real async send.</summary>
    private sealed class CancellingTransport : IDnsTransport
    {
        private readonly List<IPAddress> _asked = [];

        public IReadOnlyList<string> Asked => [.. _asked.Select(server => server.ToString())];

        public Task<byte[]?> SendUdpAsync(IPAddress server, byte[] query, TimeSpan timeout, CancellationToken ct) =>
            Cancel(server);

        public Task<byte[]?> SendTcpAsync(IPAddress server, byte[] query, TimeSpan timeout, CancellationToken ct) =>
            Cancel(server);

        private async Task<byte[]?> Cancel(IPAddress server)
        {
            _asked.Add(server);
            await Task.Yield();
            throw new OperationCanceledException();
        }
    }

    private readonly record struct Call(
        IPAddress Server, bool OverTcp, byte[] Query, TimeSpan Timeout, CancellationToken Token);
}
