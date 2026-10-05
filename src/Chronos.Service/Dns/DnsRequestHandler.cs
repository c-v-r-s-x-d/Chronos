using System.Buffers.Binary;
using Chronos.Core.Rules;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Dns;

/// <summary>
/// Which of the two sockets a query arrived on. Decides truncation and how the query is forwarded.
/// </summary>
public enum DnsTransportKind
{
    Udp,
    Tcp,
}

/// <summary>
/// Where the plan, the cache and the forwarder meet: bytes in, bytes out. A blocked name gets
/// NXDOMAIN without asking anyone; the rest is served from the cache or forwarded and cached.
/// Nothing here logs above <see cref="LogLevel.Debug"/>, since every message names a visited domain.
/// </summary>
public sealed class DnsRequestHandler
{
    private readonly IDnsForwarder _forwarder;
    private readonly DnsResponseCache _cache;
    private readonly Action<string> _onBlockedAttempt;
    private readonly ILogger<DnsRequestHandler> _logger;

    // Replaced whole on a plan change and never edited after, so volatile is enough.
    private volatile SiteRule[] _rules = [];

    public DnsRequestHandler(
        IDnsForwarder forwarder,
        DnsResponseCache cache,
        // Called with the matched rule's domain for every query turned into NXDOMAIN. Runs on the
        // answer path before the reply is built, so it must neither block nor throw.
        Action<string> onBlockedAttempt,
        ILogger<DnsRequestHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(forwarder);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(onBlockedAttempt);
        ArgumentNullException.ThrowIfNull(logger);

        _forwarder = forwarder;
        _cache = cache;
        _onBlockedAttempt = onBlockedAttempt;
        _logger = logger;
    }

    /// <summary>Replaces the rules in force. The collection is copied.</summary>
    public void UseRules(IReadOnlyCollection<SiteRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        _rules = [.. rules];
    }

    /// <summary>The answer to send back, or null to say nothing (not a query, or too short to hold an id).</summary>
    public async Task<byte[]?> AnswerAsync(byte[] query, DnsTransportKind transport, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parsed = DnsQuery.Parse(query);

        if (parsed.Status != DnsQueryStatus.Ok)
        {
            // BadVersion is refused before the plan is consulted, so no blocked attempt is reported
            // for it. The client retries at version 0 and that query is blocked and reported.
            return Refuse(query, parsed);
        }

        if (BlockedBy(parsed.Name) is { } rule)
        {
            // Every attempt is reported; throttling is done by the listener. The rule's domain, not
            // the name asked for: one page load resolves many subdomains of one site.
            _onBlockedAttempt(rule.Domain);

            _logger.LogDebug(
                "DNS query for {Domain} matched rule {Rule}; answering NXDOMAIN.", parsed.Name, rule.Domain);

            // Not stored. The cache would refuse it anyway (non-zero RCODE), which is what makes a
            // lifted rule answerable again.
            return DnsReply.NameDoesNotExist(query, parsed.QuestionEnd, parsed.HasEdns);
        }

        var cached = _cache.Take(parsed.Name, parsed.Type, parsed.Class, parsed.Id);

        if (cached is not null && TryEchoQuestion(cached, query, parsed))
        {
            _logger.LogDebug("DNS query for {Domain} answered from the cache.", parsed.Name);
            return FitToClient(cached, query, parsed, transport);
        }

        var answer = await _forwarder
            .ForwardAsync(query, transport == DnsTransportKind.Tcp, ct)
            .ConfigureAwait(false);

        if (answer is null)
        {
            _logger.LogDebug("No upstream answer for {Domain}; answering SERVFAIL.", parsed.Name);
            return DnsReply.ServerFailure(query, parsed.QuestionEnd, parsed.HasEdns);
        }

        // Offered whole, before truncation; the cache refuses truncated answers itself.
        _cache.Store(parsed.Name, parsed.Type, parsed.Class, answer);

        _logger.LogDebug("DNS query for {Domain} forwarded.", parsed.Name);
        return FitToClient(answer, query, parsed, transport);
    }

    /// <summary>The reply for a query that did not parse: silence, NOTIMP, BADVERS or FORMERR by status.</summary>
    private static byte[]? Refuse(ReadOnlySpan<byte> query, in DnsQueryInfo parsed) => parsed.Status switch
    {
        // Never reply to an answer: that would make this resolver a reflector.
        DnsQueryStatus.NotAQuery => null,
        DnsQueryStatus.NotImplemented => DnsReply.NotImplemented(query),
        DnsQueryStatus.BadVersion => DnsReply.BadVersion(query, parsed.QuestionEnd),

        // Malformed; also the fallback for any status added later.
        _ => DnsReply.FormatError(query),
    };

    private SiteRule? BlockedBy(string name)
    {
        // DomainMatcher is shared by all layers and decides subdomains, case and trailing dots.
        foreach (var rule in _rules)
        {
            if (DomainMatcher.Matches(rule, name))
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>
    /// Writes this client's question octets into a cached answer, in place. False when the answer
    /// does not echo this question; the caller then asks upstream.
    /// </summary>
    /// <remarks>
    /// The length checks cannot fire through <see cref="DnsResponseCache"/>, which validates whole
    /// messages on store; they guard the buffer bounds.
    /// </remarks>
    private static bool TryEchoQuestion(byte[] answer, byte[] query, in DnsQueryInfo parsed)
    {
        // The cache key is lower-cased but the held octets keep the first asker's spelling. A
        // DNS-0x20 client compares the echoed question octet for octet, so it must be this client's.
        if (answer.Length < DnsQuery.HeaderLength ||
            BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(4, 2)) != 1)
        {
            return false;
        }

        var pos = DnsQuery.HeaderLength;

        // A compressed question name is never legal here.
        if (!DnsName.TryRead(answer, ref pos, out var echoed, out var compressed) || compressed)
        {
            return false;
        }

        // The echoed question came from the upstream server; one quoting a different name is not this client's.
        if (!string.Equals(echoed, parsed.Name, StringComparison.Ordinal))
        {
            return false;
        }

        // Equal uncompressed names occupy the same number of octets, so this answer's question
        // ends where the client's does.
        if (answer.Length < parsed.QuestionEnd)
        {
            return false;
        }

        // A different QTYPE or QCLASS answers another question; overwriting it would hide that.
        if (BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(pos, 2)) != parsed.Type ||
            BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(pos + 2, 2)) != parsed.Class)
        {
            return false;
        }

        // Copied whole, including the tail just compared, so the echo is exactly the client's question.
        query.AsSpan(DnsQuery.HeaderLength..parsed.QuestionEnd).CopyTo(answer.AsSpan(DnsQuery.HeaderLength));
        return true;
    }

    /// <summary>The answer whole, or the truncated reply when it exceeds the client's UDP buffer.</summary>
    private static byte[] FitToClient(
        byte[] answer,
        byte[] query,
        in DnsQueryInfo parsed,
        DnsTransportKind transport)
    {
        // Only UDP has a datagram limit; TCP carries its own length (RFC 1035 4.2.2).
        // An answer exactly the advertised size fits.
        if (transport != DnsTransportKind.Udp || answer.Length <= parsed.UdpPayloadSize)
        {
            return answer;
        }

        // Built from the query, so the echoed question is this client's.
        return DnsReply.Truncated(query, parsed.QuestionEnd, parsed.HasEdns);
    }
}
