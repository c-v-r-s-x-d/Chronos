using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Chronos.Core.Rules;

namespace Chronos.Service.Wfp;

/// <summary>
/// Sends one DNS query and waits for one answer. <c>null</c> means no answer (timeout, refused
/// connection, anything short of a response), never an exception.
/// </summary>
public interface IDnsTransport
{
    Task<byte[]?> SendUdpAsync(IPAddress server, byte[] query, TimeSpan timeout, CancellationToken ct);

    Task<byte[]?> SendTcpAsync(IPAddress server, byte[] query, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Which server addresses come back to this machine. Shared by the pre-resolver and
/// <c>DnsForwarder</c>, which must never query L2's own resolver.
/// </summary>
internal static class LocalAddress
{
    /// <summary>
    /// Loopback plus the unspecified addresses <c>0.0.0.0</c> and <c>::</c>, which a saved
    /// NameServer can hold and which reach the local host. <see cref="IPAddress.IsLoopback"/> does
    /// not cover them.
    /// </summary>
    public static bool ReachesThisMachine(IPAddress address) =>
        IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
}

/// <summary>
/// Resolves the domains of a set of site rules against a server outside this machine's own DNS
/// path. A domain that does not resolve is missing from the result, so the caller can keep the
/// addresses it already knows; accumulation across passes is the caller's job.
/// </summary>
public interface IAddressResolver
{
    Task<IReadOnlyDictionary<string, IReadOnlyList<IPAddress>>> ResolveAsync(
        IEnumerable<SiteRule> sites, CancellationToken ct);
}

/// <summary>
/// Resolves against a fixed list of external servers (Cloudflare then Quad9 by default) only
/// through <see cref="IDnsTransport"/>, never the system resolver, where L2 would answer blocked
/// domains with <c>NXDOMAIN</c>.
///
/// Per domain, A and AAAA are queried independently, trying servers in order until one gives a
/// usable answer. A truncated UDP answer is retried over TCP on the same server. <c>NameNotFound</c>
/// is authoritative and not retried. Anything else (malformed, ID mismatch, SERVFAIL) moves to the
/// next server. The result is the union of A and AAAA; a domain with no addresses is omitted.
/// </summary>
public sealed class ExternalAddressResolver : IAddressResolver
{
    public static IReadOnlyList<IPAddress> DefaultServers { get; } =
        [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("9.9.9.9")];

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How many domains may be in flight at once; each uses two sockets (A and AAAA). 128 keeps the
    /// worst case, where no server answers, inside the 15-second reconcile cycle.
    /// </summary>
    internal const int MaxConcurrentDomains = 128;

    private readonly IDnsTransport _transport;
    private readonly IReadOnlyList<IPAddress> _servers;
    private readonly TimeSpan _timeout;

    public ExternalAddressResolver(IDnsTransport transport, IReadOnlyList<IPAddress> servers, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(servers);

        // Copied first, so a caller's list cannot gain a server after the check below.
        var checkedServers = servers.ToArray();

        if (checkedServers.Length == 0)
        {
            throw new ArgumentException("At least one DNS server is required.", nameof(servers));
        }

        foreach (var server in checkedServers)
        {
            // A loopback or unspecified server would route back into L2's own resolver.
            if (LocalAddress.ReachesThisMachine(server))
            {
                throw new ArgumentException(
                    $"'{server}' is a loopback or unspecified address; the external resolver must never query L2's own resolver.",
                    nameof(servers));
            }
        }

        _transport = transport;
        _servers = checkedServers;
        _timeout = timeout;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<IPAddress>>> ResolveAsync(
        IEnumerable<SiteRule> sites, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sites);

        var domains = sites.Select(site => site.Domain).Distinct(StringComparer.Ordinal).ToList();

        using var inFlight = new SemaphoreSlim(MaxConcurrentDomains, MaxConcurrentDomains);

        var perDomain = await Task.WhenAll(domains.Select(async domain =>
        {
            await inFlight.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var addresses = await ResolveDomainAsync(domain, ct).ConfigureAwait(false);
                return (Domain: domain, Addresses: addresses);
            }
            finally
            {
                inFlight.Release();
            }
        })).ConfigureAwait(false);

        var result = new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.Ordinal);
        foreach (var (domain, addresses) in perDomain)
        {
            if (addresses.Count > 0)
            {
                result[domain] = addresses;
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<IPAddress>> ResolveDomainAsync(string domain, CancellationToken ct)
    {
        try
        {
            var aTask = QueryTypeAsync(domain, DnsRecordType.A, ct);
            var aaaaTask = QueryTypeAsync(domain, DnsRecordType.Aaaa, ct);
            await Task.WhenAll(aTask, aaaaTask).ConfigureAwait(false);

            var combined = new List<IPAddress>(aTask.Result);
            combined.AddRange(aaaaTask.Result);
            return combined;
        }
        catch (ArgumentException)
        {
            // A name that cannot go on the wire (empty label, label over 63 bytes, name over 255);
            // SiteRule only checks for non-blank. Caught per domain, since escaping through
            // Task.WhenAll would discard every other domain's answers.
            return [];
        }
    }

    private async Task<IReadOnlyList<IPAddress>> QueryTypeAsync(string domain, DnsRecordType type, CancellationToken ct)
    {
        foreach (var server in _servers)
        {
            ct.ThrowIfCancellationRequested();

            var id = NextId();
            var query = DnsMessage.BuildQuery(domain, type, id);

            var response = await _transport.SendUdpAsync(server, query, _timeout, ct).ConfigureAwait(false);
            if (response is null)
            {
                continue; // no answer from this server; try the next one
            }

            var result = DnsMessage.ParseResponse(response, id);

            // Truncated is computed independently of Status, so check Status first to avoid a
            // pointless TCP retry for a message that is not an answer.
            if (result.Status == DnsParseStatus.Ok && result.Truncated)
            {
                var tcpResponse = await _transport.SendTcpAsync(server, query, _timeout, ct).ConfigureAwait(false);
                if (tcpResponse is null)
                {
                    continue;
                }

                result = DnsMessage.ParseResponse(tcpResponse, id);
            }

            switch (result.Status)
            {
                case DnsParseStatus.Ok:
                    return result.Addresses;
                case DnsParseStatus.NameNotFound:
                    return []; // authoritative: do not retry over TCP or against another server
                default:
                    continue; // malformed / ID mismatch / not-an-answer / SERVFAIL: try next server
            }
        }

        return [];
    }

    // Internal so tests can check the ID stream directly; query order under concurrency is not deterministic.
    // RFC 5452 anti-spoofing needs an unpredictable ID as well as source port. RandomNumberGenerator,
    // not Random.Shared, whose output an observer could predict.
    internal ushort NextId() => (ushort)RandomNumberGenerator.GetInt32(0, 65536);
}

/// <summary>
/// <see cref="IDnsTransport"/> over real UDP and TCP sockets, port 53.
/// </summary>
public sealed class UdpDnsTransport : IDnsTransport
{
    private const int DnsPort = 53;

    public async Task<byte[]?> SendUdpAsync(IPAddress server, byte[] query, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(query);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var client = new UdpClient(server.AddressFamily);
            client.Client.Connect(server, DnsPort);
            await client.SendAsync(query, timeoutCts.Token).ConfigureAwait(false);
            var result = await client.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            return result.Buffer;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // the request timed out, not the caller cancelling: "no answer"
        }
        catch (SocketException)
        {
            return null; // e.g. ICMP port-unreachable surfacing on the next socket call
        }
    }

    public async Task<byte[]?> SendTcpAsync(IPAddress server, byte[] query, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(query);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var client = new TcpClient(server.AddressFamily);
            await client.ConnectAsync(server, DnsPort, timeoutCts.Token).ConfigureAwait(false);

            var stream = client.GetStream();

            // Each message is framed by a 2-byte big-endian length (RFC 1035 4.2.2).
            var lengthPrefix = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(lengthPrefix, (ushort)query.Length);
            await stream.WriteAsync(lengthPrefix, timeoutCts.Token).ConfigureAwait(false);
            await stream.WriteAsync(query, timeoutCts.Token).ConfigureAwait(false);

            var responseLengthBuffer = new byte[2];
            await ReadExactAsync(stream, responseLengthBuffer, timeoutCts.Token).ConfigureAwait(false);
            var responseLength = BinaryPrimitives.ReadUInt16BigEndian(responseLengthBuffer);

            var response = new byte[responseLength];
            await ReadExactAsync(stream, response, timeoutCts.Token).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (EndOfStreamException)
        {
            return null; // the peer closed the connection before a full message arrived
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }
}
