using System.Buffers.Binary;

namespace Chronos.Service.Dns;

/// <summary>
/// The replies this resolver writes itself: a header, the question, and an OPT record of its own
/// when the query carried one (RFC 6891 6.1.1). The caller (DnsRequestHandler) chooses which reply.
/// </summary>
public static class DnsReply
{
    /// <summary>What this server advertises room for in its own OPT record.</summary>
    public const int UdpPayloadSize = 4096;

    // Shortest question: the root name (one octet), QTYPE and QCLASS.
    private const int MinimumQuestionLength = 5;

    private const ushort OptRecordType = 41;

    // Root NAME (one octet) + TYPE + CLASS + "TTL" + RDLENGTH, with no options behind it.
    private const int OptRecordLength = 11;

    // RFC 6891 6.1.3. Twelve bits wide; the top eight live in the OPT record, so the reply must carry one.
    private const int ExtendedRcodeBadVersion = 16;

    /// <summary>NXDOMAIN for a name the plan blocks.</summary>
    public static byte[] NameDoesNotExist(ReadOnlySpan<byte> query, int questionEnd, bool hasEdns) =>
        Build(query, questionEnd, DnsHeader.RcodeNameError, truncated: false, hasEdns);

    /// <summary>SERVFAIL for a query no external server answered.</summary>
    public static byte[] ServerFailure(ReadOnlySpan<byte> query, int questionEnd, bool hasEdns) =>
        Build(query, questionEnd, DnsHeader.RcodeServerFailure, truncated: false, hasEdns);

    /// <summary>The truncated reply: header and question, no answer records, TC set.</summary>
    public static byte[] Truncated(ReadOnlySpan<byte> query, int questionEnd, bool hasEdns) =>
        Build(query, questionEnd, DnsHeader.RcodeNoError, truncated: true, hasEdns);

    /// <summary>
    /// BADVERS (RFC 6891 6.1.3). The extended code is 16: the top eight bits go in the OPT record,
    /// which this reply always carries, and the header bits are zero.
    /// </summary>
    public static byte[] BadVersion(ReadOnlySpan<byte> query, int questionEnd) =>
        Build(
            query,
            questionEnd,
            (byte)(ExtendedRcodeBadVersion & DnsHeader.RcodeMask),
            truncated: false,
            hasEdns: true,
            extendedRcode: (byte)(ExtendedRcodeBadVersion >> 4));

    /// <summary>
    /// FORMERR for an unreadable query. The question is not echoed (QDCOUNT is 0). Null when the
    /// datagram is too short to hold an id.
    /// </summary>
    public static byte[]? FormatError(ReadOnlySpan<byte> query) =>
        HeaderOnly(query, DnsHeader.RcodeFormatError);

    /// <summary>
    /// NOTIMP for an unsupported opcode. Header only, no OPT: the body of an UPDATE or NOTIFY is
    /// laid out by that opcode and has not been read. Null when too short to hold an id.
    /// </summary>
    public static byte[]? NotImplemented(ReadOnlySpan<byte> query) =>
        HeaderOnly(query, DnsHeader.RcodeNotImplemented);

    private static byte[]? HeaderOnly(ReadOnlySpan<byte> query, byte rcode)
    {
        if (query.Length < DnsHeader.Length)
        {
            return null;
        }

        var reply = new byte[DnsHeader.Length];
        query[..DnsHeader.Length].CopyTo(reply);
        WriteHeader(reply, query[2], rcode, truncated: false, questions: 0, additionals: 0);
        return reply;
    }

    private static byte[] Build(
        ReadOnlySpan<byte> query,
        int questionEnd,
        byte rcode,
        bool truncated,
        bool hasEdns,
        byte extendedRcode = 0)
    {
        // A caller with no question uses FormatError; a bad length here means parse and reply disagree.
        ArgumentOutOfRangeException.ThrowIfLessThan(questionEnd, DnsHeader.Length + MinimumQuestionLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(questionEnd, query.Length);

        var reply = new byte[questionEnd + (hasEdns ? OptRecordLength : 0)];
        query[..questionEnd].CopyTo(reply);
        WriteHeader(reply, query[2], rcode, truncated, questions: 1, additionals: hasEdns ? (ushort)1 : (ushort)0);

        if (hasEdns)
        {
            WriteOptRecord(reply.AsSpan(questionEnd), extendedRcode);
        }

        return reply;
    }

    /// <summary>
    /// This server's own OPT record. Required in the reply to any query that carried one (RFC 6891
    /// 6.1.1); without it a client may treat this resolver as plain DNS and keep to 512 bytes.
    /// </summary>
    private static void WriteOptRecord(Span<byte> opt, byte extendedRcode)
    {
        opt[0] = 0; // NAME: the root (RFC 6891 6.1.2)
        BinaryPrimitives.WriteUInt16BigEndian(opt.Slice(1, 2), OptRecordType);

        // CLASS is the responder's payload size (RFC 6891 6.2.4).
        BinaryPrimitives.WriteUInt16BigEndian(opt.Slice(3, 2), UdpPayloadSize);

        // The "TTL" is EXTENDED-RCODE, VERSION, DO and Z (RFC 6891 6.1.3). Version 0, DO clear: no DNSSEC.
        opt[5] = extendedRcode;
        opt[6] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(opt.Slice(7, 2), 0);

        BinaryPrimitives.WriteUInt16BigEndian(opt.Slice(9, 2), 0); // RDLENGTH: no options
    }

    private static void WriteHeader(
        Span<byte> reply,
        byte queryFlags,
        byte rcode,
        bool truncated,
        ushort questions,
        ushort additionals)
    {
        // The id is already in place. OPCODE and RD are echoed from the query; AA is cleared (this
        // resolver is authoritative for nothing); TC is set by us whatever the query carried.
        reply[2] = (byte)(
            DnsHeader.Qr |
            (queryFlags & DnsHeader.OpcodeMask) |
            (queryFlags & DnsHeader.RecursionDesired) |
            (truncated ? DnsHeader.Truncated : 0));

        // RA always. Z, AD and CD stay clear: AD would claim a validation nobody did.
        reply[3] = (byte)(DnsHeader.RecursionAvailable | rcode);

        BinaryPrimitives.WriteUInt16BigEndian(reply.Slice(4, 2), questions);    // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(reply.Slice(6, 2), 0);            // ANCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(reply.Slice(8, 2), 0);            // NSCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(reply.Slice(10, 2), additionals); // ARCOUNT
    }
}
