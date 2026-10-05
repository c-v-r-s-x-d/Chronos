using System.Net;
using Chronos.Service.Wfp;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Dns;

/// <summary>
/// Passes on a query this resolver does not answer itself, to the servers the interface carried
/// before Chronos took it over.
/// </summary>
public interface IDnsForwarder
{
    /// <summary>Sends the query on and returns the answer verbatim; null when no server answered.</summary>
    Task<byte[]?> ForwardAsync(byte[] query, bool overTcp, CancellationToken ct);
}

/// <summary>
/// Forwards over <see cref="IDnsTransport"/>, which bypasses this machine's DNS client (where this
/// resolver itself sits). Query and answer pass through unchanged; only the identifier and RCODE
/// are read, to pick the next server. A truncated answer is not retried over TCP: the client
/// sees the TC bit and asks again itself. Servers are tried in order; null when none answers.
/// </summary>
public sealed class DnsForwarder : IDnsForwarder
{
    /// <summary>How long one server is given to answer, matching L3's pre-resolver.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How many queries may be out with an upstream server at once. Limited here, not in the
    /// receive loop, so a slow external server never delays a name the plan answers locally.
    /// </summary>
    public const int MaxConcurrentForwards = 64;

    // RCODE lives in the low four bits of the second flag octet (RFC 1035 4.1.1).
    private const int RcodeOctet = 3;

    // Never disposed: a late query still releases its slot, and releasing a disposed semaphore would fault it.
    private readonly SemaphoreSlim _slots = new(MaxConcurrentForwards, MaxConcurrentForwards);

    private readonly IDnsTransport _transport;
    private readonly IReadOnlyList<IPAddress> _upstream;
    private readonly TimeSpan _timeout;
    private readonly ILogger<DnsForwarder> _logger;

    public DnsForwarder(
        IDnsTransport transport,
        IReadOnlyList<IPAddress> upstream,
        TimeSpan timeout,
        ILogger<DnsForwarder> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(logger);

        // Zero would cancel every send; a negative value throws out of CancelAfter in the transport.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        // Copied first, so a caller's list cannot gain a server after the check below.
        var servers = upstream.ToArray();

        if (servers.Length == 0)
        {
            throw new ArgumentException("At least one upstream DNS server is required.", nameof(upstream));
        }

        foreach (var server in servers)
        {
            // Forwarding to 127.0.0.1 would loop every unblocked query back into our own socket.
            if (LocalAddress.ReachesThisMachine(server))
            {
                throw new ArgumentException(
                    $"'{server}' is a loopback or unspecified address; the forwarder must never send a query back to this resolver.",
                    nameof(upstream));
            }
        }

        _transport = transport;
        _upstream = servers;
        _timeout = timeout;
        _logger = logger;
    }

    public async Task<byte[]?> ForwardAsync(byte[] query, bool overTcp, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Waiting for a slot uses the same budget as one server, so queueing never outlasts an answer.
        if (!await _slots.WaitAsync(_timeout, ct).ConfigureAwait(false))
        {
            // No domain in the message.
            _logger.LogDebug(
                "A query waited longer than {Timeout} for one of the {Ceiling} forwarding slots and was not sent.",
                _timeout,
                MaxConcurrentForwards);

            return null;
        }

        try
        {
            return await SendAsync(query, overTcp, ct).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<byte[]?> SendAsync(byte[] query, bool overTcp, CancellationToken ct)
    {
        byte[]? refusal = null;

        foreach (var server in _upstream)
        {
            ct.ThrowIfCancellationRequested();

            // Same transport as the client used.
            var answer = overTcp
                ? await _transport.SendTcpAsync(server, query, _timeout, ct).ConfigureAwait(false)
                : await _transport.SendUdpAsync(server, query, _timeout, ct).ConfigureAwait(false);

            // Shorter than a header is not an answer.
            if (answer is not { Length: >= DnsHeader.Length })
            {
                continue;
            }

            // A different identifier is a stray or off-path reply (RFC 5452) and counts as silence.
            if (!AnswersThisQuery(query, answer))
            {
                continue;
            }

            // SERVFAIL means this server declined; try the next, but keep it in case all refuse.
            if ((answer[RcodeOctet] & DnsHeader.RcodeMask) == DnsHeader.RcodeServerFailure)
            {
                refusal = answer;
                continue;
            }

            return answer;
        }

        if (refusal is not null)
        {
            return refusal;
        }

        // Debug only: fires per unanswered query, and must not name the domain.
        _logger.LogDebug("No upstream DNS server answered; {ServerCount} tried.", _upstream.Count);
        return null;
    }

    // The identifier is the first two octets of both messages.
    private static bool AnswersThisQuery(byte[] query, byte[] answer) =>
        query.Length >= 2 && answer[0] == query[0] && answer[1] == query[1];
}
