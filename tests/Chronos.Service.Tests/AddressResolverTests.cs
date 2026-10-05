using System.Buffers.Binary;
using System.Net;
using Chronos.Core.Rules;
using Chronos.Service.Wfp;

namespace Chronos.Service.Tests;

public sealed class AddressResolverTests
{
    private static readonly IPAddress Server1 = IPAddress.Parse("1.1.1.1");
    private static readonly IPAddress Server2 = IPAddress.Parse("9.9.9.9");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    // Compares everything but the ID bytes, which the resolver picks per query.
    private static bool Matches(byte[] query, string domain, DnsRecordType type)
    {
        var probe = DnsMessage.BuildQuery(domain, type, 0);
        return query.Length == probe.Length && query.AsSpan(2).SequenceEqual(probe.AsSpan(2));
    }

    private static ushort IdOf(byte[] query) => BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(0, 2));

    private static byte[] BuildResponse(
        ushort id,
        int rcode = 0,
        bool tc = false,
        bool qr = true,
        params (DnsRecordType Type, IPAddress Address)[] answers)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), id);
        header[2] = (byte)((qr ? 0x80 : 0) | (tc ? 0x02 : 0));
        header[3] = (byte)(rcode & 0x0F);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), (ushort)answers.Length); // ANCOUNT

        using var buffer = new MemoryStream();
        buffer.Write(header);
        foreach (var (type, address) in answers)
        {
            buffer.WriteByte(0x00); // root owner name
            var typeClassTtl = new byte[8];
            BinaryPrimitives.WriteUInt16BigEndian(typeClassTtl.AsSpan(0, 2), (ushort)type);
            BinaryPrimitives.WriteUInt16BigEndian(typeClassTtl.AsSpan(2, 2), 1); // CLASS IN
            buffer.Write(typeClassTtl);

            var addressBytes = address.GetAddressBytes();
            var rdLength = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(rdLength, (ushort)addressBytes.Length);
            buffer.Write(rdLength);
            buffer.Write(addressBytes);
        }

        return buffer.ToArray();
    }

    private sealed class FakeDnsTransport : IDnsTransport
    {
        public List<(IPAddress Server, string Protocol, byte[] Query)> Calls { get; } = [];

        public Func<IPAddress, string, byte[], byte[]?> Respond { get; set; } = (_, _, _) => null;

        public Task<byte[]?> SendUdpAsync(IPAddress server, byte[] query, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((server, "udp", query));
            return Task.FromResult(Respond(server, "udp", query));
        }

        public Task<byte[]?> SendTcpAsync(IPAddress server, byte[] query, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((server, "tcp", query));
            return Task.FromResult(Respond(server, "tcp", query));
        }
    }

    /// <summary>Records the peak number of sockets requested at once.</summary>
    private sealed class ConcurrencyTrackingTransport : IDnsTransport
    {
        private int _current;
        private int _peak;

        public int Peak => Volatile.Read(ref _peak);

        public async Task<byte[]?> SendUdpAsync(IPAddress server, byte[] query, TimeSpan timeout, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _current);

            int seen;
            while (now > (seen = Volatile.Read(ref _peak))
                && Interlocked.CompareExchange(ref _peak, now, seen) != seen)
            {
                // another leg raised the peak between the read and the exchange; look again
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
                return null;
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }

        public Task<byte[]?> SendTcpAsync(IPAddress server, byte[] query, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);
    }

    // The bound is on domains in flight; each costs two sockets because the A and AAAA legs run together.
    [Fact]
    public async Task ResolveAsync_BoundsHowManyDomainsAreInFlightAtOnce()
    {
        var transport = new ConcurrencyTrackingTransport();
        var resolver = new ExternalAddressResolver(transport, [Server1], Timeout);

        await resolver.ResolveAsync(
            [.. Enumerable.Range(0, 300).Select(i => new SiteRule($"d{i}.example", false))],
            CancellationToken.None);

        Assert.True(
            transport.Peak <= ExternalAddressResolver.MaxConcurrentDomains * 2,
            $"{transport.Peak} sockets were open at once, above the {ExternalAddressResolver.MaxConcurrentDomains * 2} the bound allows");

        // Otherwise a resolver that had gone fully sequential would pass the assertion above.
        Assert.True(transport.Peak > 2, $"only {transport.Peak} sockets ever overlapped, so the bound was never exercised");
    }

    [Fact]
    public void Constructor_RejectsIPv4Loopback()
    {
        var transport = new FakeDnsTransport();

        Assert.Throws<ArgumentException>(() =>
            new ExternalAddressResolver(transport, [IPAddress.Parse("127.0.0.1")], Timeout));
    }

    [Fact]
    public void Constructor_RejectsIPv6Loopback()
    {
        var transport = new FakeDnsTransport();

        Assert.Throws<ArgumentException>(() =>
            new ExternalAddressResolver(transport, [IPAddress.Parse("::1")], Timeout));
    }

    // The unspecified addresses reach this machine like 127.0.0.1 does (a datagram to 0.0.0.0 goes to the
    // local host), so a resolver built on one would ask L2's own resolver. IPAddress.IsLoopback is false for
    // both, so the loopback check above does not cover them.
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void Constructor_RejectsTheUnspecifiedAddress(string address)
    {
        var transport = new FakeDnsTransport();

        var failure = Assert.Throws<ArgumentException>(() =>
            new ExternalAddressResolver(transport, [IPAddress.Parse(address)], Timeout));

        Assert.Equal("servers", failure.ParamName);
    }

    // The list is checked once, in the constructor. Keeping the caller's own collection would let a
    // server added afterwards (a loopback one, say) be queried without the check.
    [Fact]
    public async Task Constructor_CopiesTheServerListRatherThanBorrowingIt()
    {
        var transport = new FakeDnsTransport();
        transport.Respond = (_, _, query) =>
            BuildResponse(IdOf(query), answers: [(DnsRecordType.A, IPAddress.Parse("93.184.216.34"))]);

        var servers = new List<IPAddress> { Server1 };
        var resolver = new ExternalAddressResolver(transport, servers, Timeout);

        servers.Clear();
        servers.Add(IPAddress.Loopback);

        var result = await resolver.ResolveAsync([new SiteRule("example.com", false)], CancellationToken.None);

        Assert.True(result.ContainsKey("example.com"));
        Assert.NotEmpty(transport.Calls);
        Assert.All(transport.Calls, call => Assert.Equal(Server1, call.Server));
    }

    [Fact]
    public async Task ResolveAsync_IssuesBothAAndAaaaQueries()
    {
        var transport = new FakeDnsTransport();
        transport.Respond = (_, _, query) =>
        {
            var id = IdOf(query);
            if (Matches(query, "example.com", DnsRecordType.A))
            {
                return BuildResponse(id, answers: [(DnsRecordType.A, IPAddress.Parse("93.184.216.34"))]);
            }

            if (Matches(query, "example.com", DnsRecordType.Aaaa))
            {
                return BuildResponse(
                    id, answers: [(DnsRecordType.Aaaa, IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946"))]);
            }

            return null;
        };

        var resolver = new ExternalAddressResolver(transport, [Server1], Timeout);
        var result = await resolver.ResolveAsync([new SiteRule("example.com", false)], CancellationToken.None);

        Assert.Contains(transport.Calls, c => Matches(c.Query, "example.com", DnsRecordType.A));
        Assert.Contains(transport.Calls, c => Matches(c.Query, "example.com", DnsRecordType.Aaaa));
        Assert.Equal(2, result["example.com"].Count);
    }

    [Fact]
    public async Task ResolveAsync_AAndAaaaLegsForOneDomain_DoNotShareAQueryId()
    {
        // NextId_IsNotASequentialCounter guards the generator; this guards the caller using it. Matches()
        // skips the ID bytes, so nothing else here would catch a caller hardcoding every query's ID.
        var transport = new FakeDnsTransport();
        transport.Respond = (_, _, query) =>
            BuildResponse(IdOf(query), answers: [(DnsRecordType.A, IPAddress.Parse("1.2.3.4"))]);

        var resolver = new ExternalAddressResolver(transport, [Server1], Timeout);
        await resolver.ResolveAsync([new SiteRule("example.com", false)], CancellationToken.None);

        var aId = transport.Calls.Single(c => Matches(c.Query, "example.com", DnsRecordType.A)).Query;
        var aaaaId = transport.Calls.Single(c => Matches(c.Query, "example.com", DnsRecordType.Aaaa)).Query;

        Assert.NotEqual(IdOf(aId), IdOf(aaaaId));
    }

    [Fact]
    public async Task ResolveAsync_DomainWithOnlyARecords_GivesOnlyAAddressesAndIsNotAFailure()
    {
        var transport = new FakeDnsTransport();
        transport.Respond = (_, _, query) =>
        {
            var id = IdOf(query);
            if (Matches(query, "example.com", DnsRecordType.A))
            {
                return BuildResponse(id, answers: [(DnsRecordType.A, IPAddress.Parse("93.184.216.34"))]);
            }

            if (Matches(query, "example.com", DnsRecordType.Aaaa))
            {
                return BuildResponse(id, rcode: 3); // NXDOMAIN for the AAAA leg
            }

            return null;
        };

        var resolver = new ExternalAddressResolver(transport, [Server1], Timeout);
        var result = await resolver.ResolveAsync([new SiteRule("example.com", false)], CancellationToken.None);

        Assert.True(result.ContainsKey("example.com"));
        Assert.Equal([IPAddress.Parse("93.184.216.34")], result["example.com"]);
    }

    [Fact]
    public async Task ResolveAsync_FirstServerNoResponse_TriesSecondServer()
    {
        var transport = new FakeDnsTransport();
        transport.Respond = (server, _, query) =>
        {
            if (server.Equals(Server1))
            {
                return null;
            }

            var id = IdOf(query);
            return BuildResponse(id, answers: [(DnsRecordType.A, IPAddress.Parse("1.2.3.4"))]);
        };

        var resolver = new ExternalAddressResolver(transport, [Server1, Server2], Timeout);
        var result = await resolver.ResolveAsync([new SiteRule("example.com", false)], CancellationToken.None);

        Assert.Contains(transport.Calls, c => c.Server.Equals(Server2) && Matches(c.Query, "example.com", DnsRecordType.A));
        Assert.Contains(IPAddress.Parse("1.2.3.4"), result["example.com"]);
    }

    [Fact]
    public async Task ResolveAsync_BothServersNoResponse_OmitsDomainKeyEntirely()
    {
        var transport = new FakeDnsTransport(); // Respond always returns null by default

        var resolver = new ExternalAddressResolver(transport, [Server1, Server2], Timeout);
        var result = await resolver.ResolveAsync([new SiteRule("example.com", false)], CancellationToken.None);

        Assert.False(result.ContainsKey("example.com"));
    }

    [Fact]
    public async Task ResolveAsync_TruncatedUdpResponse_RetriesOverTcpToSameServer()
    {
        var transport = new FakeDnsTransport();
        transport.Respond = (server, protocol, query) =>
        {
            var id = IdOf(query);
            if (!Matches(query, "example.com", DnsRecordType.A))
            {
                return BuildResponse(id, rcode: 3); // starve the AAAA leg with NXDOMAIN
            }

            return protocol == "udp"
                ? BuildResponse(id, tc: true) // truncated, no usable answers over UDP
                : BuildResponse(id, answers: [(DnsRecordType.A, IPAddress.Parse("8.8.8.8"))]);
        };

        var resolver = new ExternalAddressResolver(transport, [Server1], Timeout);
        var result = await resolver.ResolveAsync([new SiteRule("example.com", false)], CancellationToken.None);

        Assert.Contains(transport.Calls, c => c.Protocol == "tcp" && c.Server.Equals(Server1));
        Assert.Equal([IPAddress.Parse("8.8.8.8")], result["example.com"]);
    }

    [Fact]
    public async Task ResolveAsync_TruncatedButNotAnAnswer_DoesNotRetryOverTcp()
    {
        // TC is read from the header independently of QR. A message with TC set but QR clear is not an
        // answer, so retrying it over TCP would be a wasted round trip.
        var transport = new FakeDnsTransport();
        transport.Respond = (_, _, query) => BuildResponse(IdOf(query), tc: true, qr: false);

        var resolver = new ExternalAddressResolver(transport, [Server1], Timeout);
        await resolver.ResolveAsync([new SiteRule("example.com", false)], CancellationToken.None);

        Assert.DoesNotContain(transport.Calls, c => c.Protocol == "tcp");
    }

    [Fact]
    public async Task ResolveAsync_NameNotFound_DoesNotRetryToSecondServerOrOverTcp()
    {
        var transport = new FakeDnsTransport();
        transport.Respond = (_, _, query) => BuildResponse(IdOf(query), rcode: 3);

        var resolver = new ExternalAddressResolver(transport, [Server1, Server2], Timeout);
        await resolver.ResolveAsync([new SiteRule("example.com", false)], CancellationToken.None);

        Assert.DoesNotContain(transport.Calls, c => c.Server.Equals(Server2));
        Assert.DoesNotContain(transport.Calls, c => c.Protocol == "tcp");
    }

    [Fact]
    public async Task ResolveAsync_MalformedResponseFromFirstServer_TriesSecondServer()
    {
        var transport = new FakeDnsTransport();
        transport.Respond = (server, _, query) =>
        {
            if (server.Equals(Server1))
            {
                return [0x00, 0x01, 0x02]; // shorter than a DNS header: Malformed
            }

            var id = IdOf(query);
            return BuildResponse(id, answers: [(DnsRecordType.A, IPAddress.Parse("4.3.2.1"))]);
        };

        var resolver = new ExternalAddressResolver(transport, [Server1, Server2], Timeout);
        var result = await resolver.ResolveAsync([new SiteRule("example.com", false)], CancellationToken.None);

        Assert.Contains(transport.Calls, c => c.Server.Equals(Server2));
        Assert.Contains(IPAddress.Parse("4.3.2.1"), result["example.com"]);
    }

    [Fact]
    public async Task ResolveAsync_TwoDomains_OneFailingDoesNotAffectTheOther()
    {
        var transport = new FakeDnsTransport();
        transport.Respond = (_, _, query) =>
        {
            var id = IdOf(query);
            if (Matches(query, "good.example", DnsRecordType.A))
            {
                return BuildResponse(id, answers: [(DnsRecordType.A, IPAddress.Parse("1.1.2.2"))]);
            }

            if (Matches(query, "good.example", DnsRecordType.Aaaa))
            {
                return BuildResponse(id, rcode: 3);
            }

            return null; // bad.example never answers
        };

        var resolver = new ExternalAddressResolver(transport, [Server1], Timeout);
        var result = await resolver.ResolveAsync(
            [new SiteRule("bad.example", false), new SiteRule("good.example", false)], CancellationToken.None);

        Assert.False(result.ContainsKey("bad.example"));
        Assert.Equal([IPAddress.Parse("1.1.2.2")], result["good.example"]);
    }

    // SiteRule only checks that a domain is not blank, so a name DnsName.Encode rejects (empty label,
    // label over 63 bytes, name over 255) reaches the resolver from user configuration. It must not
    // throw out of Task.WhenAll and discard the answers already received for other domains.
    [Theory]
    [InlineData("bad..example")] // empty label
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.example")] // 65-byte label
    public async Task ResolveAsync_ADomainThatCannotBeEncoded_CostsThatDomainAndNothingElse(string bad)
    {
        var transport = new FakeDnsTransport();
        transport.Respond = (_, _, query) => Matches(query, "good.example", DnsRecordType.A)
            ? BuildResponse(IdOf(query), answers: [(DnsRecordType.A, IPAddress.Parse("1.1.2.2"))])
            : null;

        var resolver = new ExternalAddressResolver(transport, [Server1], Timeout);
        var result = await resolver.ResolveAsync(
            [new SiteRule(bad, false), new SiteRule("good.example", false)], CancellationToken.None);

        Assert.False(result.ContainsKey(bad));
        Assert.Equal([IPAddress.Parse("1.1.2.2")], result["good.example"]);
    }

    [Fact]
    public async Task ResolveAsync_Cancellation_PropagatesAndDoesNotHang()
    {
        using var cts = new CancellationTokenSource();
        var transport = new FakeDnsTransport();
        transport.Respond = (_, _, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        var resolver = new ExternalAddressResolver(transport, [Server1], Timeout);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync([new SiteRule("example.com", false)], cts.Token));
    }

    [Fact]
    public void NextId_IsNotASequentialCounter()
    {
        // Checks the shape of the stream, not just that two IDs differ: a counter gives id[i+1] == id[i]+1
        // on nearly every adjacent pair (999 of 1000), a uniform 16-bit generator on about 1 in 65536.
        // 50 is far from both, so the test is neither flaky nor blind to a counter.
        var resolver = new ExternalAddressResolver(new FakeDnsTransport(), [Server1], Timeout);

        const int sampleSize = 1000;
        var ids = new ushort[sampleSize];
        for (var i = 0; i < sampleSize; i++)
        {
            ids[i] = resolver.NextId();
        }

        var sequentialAdjacentPairs = 0;
        for (var i = 0; i < ids.Length - 1; i++)
        {
            if (ids[i + 1] == (ushort)(ids[i] + 1))
            {
                sequentialAdjacentPairs++;
            }
        }

        Assert.True(
            sequentialAdjacentPairs < 50,
            $"{sequentialAdjacentPairs} of {sampleSize - 1} adjacent pairs were id+1 - this looks sequential, not random.");

        Assert.True(ids.Distinct().Count() > 1, "every generated ID was identical");
    }

    // UdpDnsTransport: the one test that touches a real socket.

    [RequiresNetwork]
    public async Task UdpDnsTransport_ResolvesExampleComToAtLeastOneAddress()
    {
        var resolver = new ExternalAddressResolver(
            new UdpDnsTransport(), ExternalAddressResolver.DefaultServers, ExternalAddressResolver.DefaultTimeout);

        var result = await resolver.ResolveAsync([new SiteRule("example.com", false)], CancellationToken.None);

        Assert.True(result.ContainsKey("example.com"));
        Assert.NotEmpty(result["example.com"]);
    }
}
