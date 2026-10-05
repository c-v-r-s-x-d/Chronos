using System.Buffers.Binary;

namespace Chronos.Service.Dns;

/// <summary>Why a query cannot be served as asked.</summary>
public enum DnsQueryStatus
{
    Ok,
    Malformed,       // too short, bad name, bad question count, compressed question name, misplaced OPT
    NotAQuery,       // QR set
    NotImplemented,  // an OPCODE other than QUERY
    BadVersion,      // an EDNS version other than 0 (RFC 6891 6.1.3)
}

/// <summary>
/// One parsed query. <see cref="QuestionEnd"/> is where the question section ends; a reply echoes
/// the header and question only. <see cref="UdpPayloadSize"/> is the EDNS(0) buffer, or 512 without
/// an OPT record. On Malformed, NotAQuery and NotImplemented only <see cref="Id"/> is set (0 when
/// the message was too short); BadVersion also carries the question.
/// </summary>
public readonly record struct DnsQueryInfo(
    DnsQueryStatus Status,
    ushort Id,
    string Name,
    ushort Type,
    ushort Class,
    int QuestionEnd,
    int UdpPayloadSize,
    // Separate from UdpPayloadSize: 512 means both "no OPT" and "OPT asking for 512", which get different replies.
    bool HasEdns);

/// <summary>
/// Reads a client's query off the wire. Every refusal is a status value, never an exception.
/// </summary>
public static class DnsQuery
{
    public const int HeaderLength = DnsHeader.Length;
    public const int DefaultUdpPayloadSize = 512;
    public const ushort ClassInternet = 1;

    private const ushort OptRecordType = 41;

    private const int QuestionTailLength = 4;  // QTYPE + QCLASS
    private const int RecordHeaderLength = 10; // TYPE + CLASS + TTL + RDLENGTH

    public static DnsQueryInfo Parse(ReadOnlySpan<byte> query)
    {
        if (query.Length < HeaderLength)
        {
            return Refused(DnsQueryStatus.Malformed, 0);
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(query[..2]);
        var flags = query[2];

        if ((flags & DnsHeader.Qr) != 0)
        {
            return Refused(DnsQueryStatus.NotAQuery, id);
        }

        // Readable but unsupported (UPDATE, NOTIFY) is NOTIMP; FORMERR is for unreadable bytes.
        if ((flags & DnsHeader.OpcodeMask) != DnsHeader.OpcodeQuery)
        {
            return Refused(DnsQueryStatus.NotImplemented, id);
        }

        // Exactly one question.
        if (BinaryPrimitives.ReadUInt16BigEndian(query.Slice(4, 2)) != 1)
        {
            return Refused(DnsQueryStatus.Malformed, id);
        }

        var pos = HeaderLength;
        if (!DnsName.TryRead(query, ref pos, out var name, out var compressed) ||
            pos + QuestionTailLength > query.Length)
        {
            return Refused(DnsQueryStatus.Malformed, id);
        }

        // Nothing precedes the first question but the header, so a pointer here is never legal (RFC 1035 4.1.4).
        if (compressed)
        {
            return Refused(DnsQueryStatus.Malformed, id);
        }

        var type = BinaryPrimitives.ReadUInt16BigEndian(query.Slice(pos, 2));
        var qclass = BinaryPrimitives.ReadUInt16BigEndian(query.Slice(pos + 2, 2));
        var questionEnd = pos + QuestionTailLength;

        // Type and class are not judged; the upstream server decides.
        var edns = ReadEdns(query, questionEnd, out var udpPayloadSize, out var hasEdns);

        if (edns == DnsQueryStatus.Malformed)
        {
            return Refused(DnsQueryStatus.Malformed, id);
        }

        return new DnsQueryInfo(edns, id, name, type, qclass, questionEnd, udpPayloadSize, hasEdns);
    }

    private static DnsQueryInfo Refused(DnsQueryStatus status, ushort id) =>
        new(status, id, string.Empty, 0, 0, 0, DefaultUdpPayloadSize, HasEdns: false);

    /// <summary>
    /// Walks all records after the question (the additional section starts where the answer and
    /// authority sections end) looking for the OPT record. Malformed for unreadable records or a
    /// misplaced OPT, BadVersion for an EDNS version other than 0, otherwise Ok.
    /// </summary>
    private static DnsQueryStatus ReadEdns(
        ReadOnlySpan<byte> query,
        int questionEnd,
        out int udpPayloadSize,
        out bool hasEdns)
    {
        udpPayloadSize = DefaultUdpPayloadSize;
        hasEdns = false;

        var answers = BinaryPrimitives.ReadUInt16BigEndian(query.Slice(6, 2));      // ANCOUNT
        var authorities = BinaryPrimitives.ReadUInt16BigEndian(query.Slice(8, 2));  // NSCOUNT
        var additionals = BinaryPrimitives.ReadUInt16BigEndian(query.Slice(10, 2)); // ARCOUNT
        var records = answers + authorities + additionals;

        var advertised = DefaultUdpPayloadSize;
        var seenOpt = false;
        var badVersion = false;
        var pos = questionEnd;

        for (var record = 0; record < records; record++)
        {
            var ownerStart = pos;

            if (!DnsName.TrySkip(query, ref pos) || pos + RecordHeaderLength > query.Length)
            {
                return DnsQueryStatus.Malformed;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(query.Slice(pos, 2));
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(query.Slice(pos + 8, 2));

            if (type == OptRecordType)
            {
                // RFC 6891 6.1.1: at most one OPT, in the additional section.
                if (seenOpt || record < answers + authorities)
                {
                    return DnsQueryStatus.Malformed;
                }

                // RFC 6891 6.1.2: NAME must be the root, one zero octet, not a pointer to one.
                if (query[ownerStart] != 0)
                {
                    return DnsQueryStatus.Malformed;
                }

                seenOpt = true;

                // In an OPT record CLASS is the requestor's buffer size and the "TTL" holds
                // EXTENDED-RCODE, VERSION, DO and Z (RFC 6891 6.1.2, 6.1.3). Below 512 counts as 512 (6.2.3).
                int requested = BinaryPrimitives.ReadUInt16BigEndian(query.Slice(pos + 2, 2));
                advertised = Math.Max(DefaultUdpPayloadSize, requested);

                // VERSION is the second octet of the "TTL". The verdict waits for the walk to finish,
                // since a message that does not read is Malformed first.
                badVersion = query[pos + 5] != 0;
            }

            pos += RecordHeaderLength;

            if (pos + rdLength > query.Length)
            {
                return DnsQueryStatus.Malformed;
            }

            pos += rdLength;
        }

        udpPayloadSize = advertised;
        hasEdns = seenOpt;
        return badVersion ? DnsQueryStatus.BadVersion : DnsQueryStatus.Ok;
    }
}
