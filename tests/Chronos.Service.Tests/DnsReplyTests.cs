using System.Buffers.Binary;
using Chronos.Service.Dns;

namespace Chronos.Service.Tests;

public sealed class DnsReplyTests
{
    // The four builders that echo the question, driven through one parameter so each header rule is stated once.
    public enum ReplyKind
    {
        NameDoesNotExist,
        ServerFailure,
        Truncated,
        BadVersion,
    }

    // The two that answer with a header and nothing else.
    public enum HeaderOnlyKind
    {
        FormatError,
        NotImplemented,
    }

    private const int OptRecordLength = 11; // root NAME + TYPE + CLASS + "TTL" + RDLENGTH
    private const ushort OptType = 41;

    [Fact]
    public void TheAdvertisedBufferIsTheOneThisServerHasRoomFor()
    {
        Assert.Equal(4096, DnsReply.UdpPayloadSize);
    }

    [Fact]
    public void NameDoesNotExist_EchoesTheQuestionAndAnswersNxdomain()
    {
        var query = DnsQueryTests.Query("example.com", id: 0xBEEF);
        var parsed = DnsQuery.Parse(query);

        var reply = DnsReply.NameDoesNotExist(query, parsed.QuestionEnd, parsed.HasEdns);

        Assert.Equal(0xBEEF, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(0, 2)));
        Assert.True((reply[2] & 0x80) != 0);              // QR
        Assert.True((reply[3] & 0x80) != 0);              // RA: this server does recurse
        Assert.Equal(3, reply[3] & 0x0F);                 // NXDOMAIN
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(4, 2)));  // QDCOUNT
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6, 2)));  // ANCOUNT
        Assert.Equal(
            query.AsSpan(DnsQuery.HeaderLength, parsed.QuestionEnd - DnsQuery.HeaderLength).ToArray(),
            reply.AsSpan(DnsQuery.HeaderLength).ToArray());
    }

    // Each builder writes one response code and no other. BADVERS is 16, which does not fit the four
    // header bits: the header holds zero and the upper eight bits travel in the OPT record.
    [Theory]
    [InlineData(ReplyKind.NameDoesNotExist, 3)]
    [InlineData(ReplyKind.ServerFailure, 2)]
    [InlineData(ReplyKind.Truncated, 0)]
    [InlineData(ReplyKind.BadVersion, 0)]
    public void Reply_WritesTheResponseCodeItIsNamedAfter(ReplyKind kind, int rcode)
    {
        var query = DnsQueryTests.Query("example.com");

        Assert.Equal(rcode, Reply(kind, query)[3] & 0x0F);
    }

    [Fact]
    public void Truncated_SetsTheTcBitAndCarriesNoRecords()
    {
        var query = DnsQueryTests.Query("example.com");

        var reply = DnsReply.Truncated(query, DnsQuery.Parse(query).QuestionEnd, hasEdns: false);

        Assert.True((reply[2] & 0x02) != 0);
        Assert.Equal(0, reply[3] & 0x0F);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6, 2)));
    }

    // Only the truncation builder truncates; a TC bit on an NXDOMAIN would send the client to retry over TCP for the same answer.
    [Theory]
    [InlineData(ReplyKind.NameDoesNotExist)]
    [InlineData(ReplyKind.ServerFailure)]
    [InlineData(ReplyKind.BadVersion)]
    public void Reply_LeavesTheTruncationBitClearEvenWhenTheQueryCarriedIt(ReplyKind kind)
    {
        var query = DnsQueryTests.Query("example.com", flags1: 0x03); // RD and TC

        Assert.Equal(0, Reply(kind, query)[2] & 0x02);
    }

    [Theory]
    [InlineData(ReplyKind.NameDoesNotExist)]
    [InlineData(ReplyKind.ServerFailure)]
    [InlineData(ReplyKind.Truncated)]
    public void Reply_IsTheHeaderAndTheQuestionAndNothingElse(ReplyKind kind)
    {
        var query = DnsQueryTests.Query("example.com", id: 0x0102);
        var questionEnd = DnsQuery.Parse(query).QuestionEnd;

        var reply = Reply(kind, query);

        Assert.Equal(questionEnd, reply.Length);
        Assert.Equal(0x0102, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(0, 2)));
        Assert.Equal(
            query.AsSpan(DnsQuery.HeaderLength, questionEnd - DnsQuery.HeaderLength).ToArray(),
            reply.AsSpan(DnsQuery.HeaderLength).ToArray());
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(4, 2)));   // QDCOUNT
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6, 2)));   // ANCOUNT
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(8, 2)));   // NSCOUNT
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(10, 2)));  // ARCOUNT
    }

    // The query's counts are copied into the reply with the rest of the header, so the two a reply can
    // never honour must be overwritten. A query with answer and authority records is not hypothetical
    // (Parse takes one), and a reply promising records it lacks cannot be read to the end.
    [Theory]
    [InlineData(ReplyKind.NameDoesNotExist)]
    [InlineData(ReplyKind.ServerFailure)]
    [InlineData(ReplyKind.Truncated)]
    [InlineData(ReplyKind.BadVersion)]
    public void Reply_ZeroesTheAnswerAndAuthorityCountsTheQueryCarried(ReplyKind kind)
    {
        var query = QueryCarryingRecords();
        var parsed = DnsQuery.Parse(query);

        Assert.Equal(DnsQueryStatus.Ok, parsed.Status);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(6, 2)));   // ANCOUNT of the query
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(8, 2)));   // NSCOUNT of the query

        var reply = Reply(kind, query, parsed.QuestionEnd, hasEdns: false);

        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6, 2)));   // ANCOUNT
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(8, 2)));   // NSCOUNT
    }

    [Theory]
    [InlineData(HeaderOnlyKind.FormatError)]
    [InlineData(HeaderOnlyKind.NotImplemented)]
    public void HeaderOnlyReply_ZeroesTheAnswerAndAuthorityCountsTheQueryCarried(HeaderOnlyKind kind)
    {
        var reply = HeaderOnlyReply(kind, QueryCarryingRecords())!;

        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6, 2)));   // ANCOUNT
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(8, 2)));   // NSCOUNT
    }

    [Fact]
    public void NameDoesNotExist_KeepsTheRecursionDesiredBitTheClientSent()
    {
        var query = DnsQueryTests.Query("example.com"); // the builder sets RD

        var reply = DnsReply.NameDoesNotExist(query, DnsQuery.Parse(query).QuestionEnd, hasEdns: false);

        Assert.True((reply[2] & 0x01) != 0);
    }

    // Copied from the query, not invented: RD is the one bit of the query's flags a reply repeats.
    [Theory]
    [InlineData(ReplyKind.NameDoesNotExist, 0x01, 0x01)]
    [InlineData(ReplyKind.NameDoesNotExist, 0x00, 0x00)]
    [InlineData(ReplyKind.ServerFailure, 0x01, 0x01)]
    [InlineData(ReplyKind.ServerFailure, 0x00, 0x00)]
    [InlineData(ReplyKind.Truncated, 0x01, 0x01)]
    [InlineData(ReplyKind.Truncated, 0x00, 0x00)]
    [InlineData(ReplyKind.BadVersion, 0x01, 0x01)]
    [InlineData(ReplyKind.BadVersion, 0x00, 0x00)]
    public void Reply_CopiesTheRecursionDesiredBitOfTheQuery(ReplyKind kind, int flags1, int expected)
    {
        var query = DnsQueryTests.Query("example.com", flags1: (byte)flags1);

        Assert.Equal(expected, Reply(kind, query)[2] & 0x01);
    }

    // A reply repeats the opcode of its query (RFC 1035 4.1.1). The question end is named outright
    // because Parse refuses this opcode and a builder judges nothing.
    [Theory]
    [InlineData(ReplyKind.NameDoesNotExist)]
    [InlineData(ReplyKind.ServerFailure)]
    [InlineData(ReplyKind.Truncated)]
    [InlineData(ReplyKind.BadVersion)]
    public void Reply_EchoesTheOpcodeOfTheQuery(ReplyKind kind)
    {
        var query = DnsQueryTests.Query("example.com", flags1: 0x11); // OPCODE 2 (STATUS), RD

        Assert.Equal(0x10, Reply(kind, query, query.Length)[2] & 0x78);
    }

    // This resolver forwards and is authoritative for nothing, so AA is cleared however the query arrived.
    [Theory]
    [InlineData(ReplyKind.NameDoesNotExist)]
    [InlineData(ReplyKind.ServerFailure)]
    [InlineData(ReplyKind.Truncated)]
    [InlineData(ReplyKind.BadVersion)]
    public void Reply_ClearsTheAuthoritativeAnswerBit(ReplyKind kind)
    {
        var query = DnsQueryTests.Query("example.com", flags1: 0x05); // AA and RD

        Assert.Equal(0, Reply(kind, query)[2] & 0x04);
    }

    // The second flag byte is RA, three zero bits, AD and CD. DNSSEC is not served, so AD ("validated")
    // is a claim this resolver cannot make whatever the query's bits say.
    [Theory]
    [InlineData(ReplyKind.NameDoesNotExist, 3)]
    [InlineData(ReplyKind.ServerFailure, 2)]
    [InlineData(ReplyKind.Truncated, 0)]
    [InlineData(ReplyKind.BadVersion, 0)]
    public void Reply_SetsRecursionAvailableAndNothingElseBesideTheResponseCode(ReplyKind kind, int rcode)
    {
        var query = DnsQueryTests.Query("example.com", flags2: 0xFF);

        Assert.Equal(0x80 | rcode, Reply(kind, query)[3]);
    }

    // RFC 6891 6.1.1: responders MUST include an OPT record. It acknowledges that EDNS was understood;
    // without it a client may treat this server as a plain resolver and keep to 512 bytes.
    [Theory]
    [InlineData(ReplyKind.NameDoesNotExist)]
    [InlineData(ReplyKind.ServerFailure)]
    [InlineData(ReplyKind.Truncated)]
    public void Reply_CarriesAnOptRecordWhenTheQueryDid(ReplyKind kind)
    {
        var query = DnsQueryTests.QueryWithOpt("example.com", payloadSize: 1232);
        var parsed = DnsQuery.Parse(query);

        Assert.True(parsed.HasEdns);

        var reply = Reply(kind, query, parsed.QuestionEnd, parsed.HasEdns);

        AssertOptRecord(reply, parsed.QuestionEnd, extendedRcode: 0);
    }

    // And none when the query had none: an unrequested OPT advertises an extension the client did not ask about.
    [Theory]
    [InlineData(ReplyKind.NameDoesNotExist)]
    [InlineData(ReplyKind.ServerFailure)]
    [InlineData(ReplyKind.Truncated)]
    public void Reply_CarriesNoOptRecordWhenTheQueryDidNot(ReplyKind kind)
    {
        var query = DnsQueryTests.Query("example.com");
        var parsed = DnsQuery.Parse(query);

        Assert.False(parsed.HasEdns);

        var reply = Reply(kind, query, parsed.QuestionEnd, parsed.HasEdns);

        Assert.Equal(parsed.QuestionEnd, reply.Length);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(10, 2))); // ARCOUNT
    }

    // The OPT record sits after the question, so the question is still echoed byte for byte.
    [Fact]
    public void Reply_PutsItsOptRecordAfterTheQuestionItEchoes()
    {
        var query = DnsQueryTests.QueryWithOpt("example.com", payloadSize: 4096);
        var parsed = DnsQuery.Parse(query);

        var reply = DnsReply.NameDoesNotExist(query, parsed.QuestionEnd, parsed.HasEdns);

        Assert.Equal(
            query.AsSpan(DnsQuery.HeaderLength, parsed.QuestionEnd - DnsQuery.HeaderLength).ToArray(),
            reply.AsSpan(DnsQuery.HeaderLength, parsed.QuestionEnd - DnsQuery.HeaderLength).ToArray());
    }

    // The buffer in a reply's OPT record is the responder's own (RFC 6891 6.2.4), not an echo of the client's.
    [Theory]
    [InlineData(512)]
    [InlineData(1232)]
    [InlineData(65535)]
    public void Reply_AdvertisesItsOwnBufferAndNotTheOneTheQueryAsked(int asked)
    {
        var query = DnsQueryTests.QueryWithOpt("example.com", payloadSize: (ushort)asked);
        var parsed = DnsQuery.Parse(query);

        var reply = DnsReply.NameDoesNotExist(query, parsed.QuestionEnd, parsed.HasEdns);

        Assert.Equal(
            DnsReply.UdpPayloadSize,
            BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(parsed.QuestionEnd + 3, 2)));
    }

    // RFC 6891 6.1.3: the response code is 16 and twelve bits wide. The low four stay in the header
    // (zero) and the top eight are the first octet of the OPT record's TTL, so BADVERS needs an OPT record.
    [Fact]
    public void BadVersion_PutsTheTopOfTheExtendedCodeInTheOptRecordAndTheRestInTheHeader()
    {
        var query = DnsQueryTests.QueryWithOpt("example.com", payloadSize: 4096, id: 0x0BAD);
        var parsed = DnsQuery.Parse(query);

        var reply = DnsReply.BadVersion(query, parsed.QuestionEnd);

        Assert.Equal(0x0BAD, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(0, 2)));
        Assert.Equal(0, reply[3] & 0x0F);
        AssertOptRecord(reply, parsed.QuestionEnd, extendedRcode: 1);
    }

    [Fact]
    public void BadVersion_EchoesTheQuestionItRefuses()
    {
        var query = DnsQueryTests.QueryWithOpt("example.com", payloadSize: 4096);
        var parsed = DnsQuery.Parse(query);

        var reply = DnsReply.BadVersion(query, parsed.QuestionEnd);

        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(4, 2))); // QDCOUNT
        Assert.Equal(
            query.AsSpan(DnsQuery.HeaderLength, parsed.QuestionEnd - DnsQuery.HeaderLength).ToArray(),
            reply.AsSpan(DnsQuery.HeaderLength, parsed.QuestionEnd - DnsQuery.HeaderLength).ToArray());
    }

    // The shortest question: the root name, a type and a class. Both sides, since QDCOUNT 1 with less than a question behind it is malformed.
    [Theory]
    [InlineData(ReplyKind.NameDoesNotExist)]
    [InlineData(ReplyKind.ServerFailure)]
    [InlineData(ReplyKind.Truncated)]
    [InlineData(ReplyKind.BadVersion)]
    public void Reply_TakesTheShortestQuestionThereIsAndNothingShorter(ReplyKind kind)
    {
        var query = DnsQueryTests.RootQuery();

        Assert.Equal(17, Reply(kind, query, 17).Length - OptRecordLengthOf(kind));

        var refusal = Assert.Throws<ArgumentOutOfRangeException>(() => Reply(kind, query, 16));

        Assert.Equal("questionEnd", refusal.ParamName);
    }

    [Theory]
    [InlineData(ReplyKind.NameDoesNotExist)]
    [InlineData(ReplyKind.ServerFailure)]
    [InlineData(ReplyKind.Truncated)]
    [InlineData(ReplyKind.BadVersion)]
    public void Reply_RefusesAQuestionThatEndsPastTheEndOfTheQuery(ReplyKind kind)
    {
        var query = DnsQueryTests.Query("example.com");

        Assert.Equal(query.Length, Reply(kind, query, query.Length).Length - OptRecordLengthOf(kind));

        // The parameter name is the point: slicing the query would throw this same type anyway, so the
        // assertion would pass with no check there at all.
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(() => Reply(kind, query, query.Length + 1));

        Assert.Equal("questionEnd", refusal.ParamName);
    }

    [Fact]
    public void FormatError_AnswersEvenWhenOnlyTheHeaderCanBeRead()
    {
        var query = new byte[DnsQuery.HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(0, 2), 0x0A0B);

        var reply = DnsReply.FormatError(query);

        Assert.NotNull(reply);
        Assert.Equal(0x0A0B, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(0, 2)));
        Assert.Equal(1, reply[3] & 0x0F);
    }

    // An opcode this server does not serve is NOTIMP (RCODE 4): the message read fine but asked for something unsupported.
    [Fact]
    public void NotImplemented_AnswersTheOpcodeItDoesNotServe()
    {
        var query = DnsQueryTests.Query("example.com", flags1: 0x29, id: 0x0A0B); // OPCODE 5 (UPDATE), RD

        var reply = DnsReply.NotImplemented(query);

        Assert.NotNull(reply);
        Assert.Equal(0x0A0B, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(0, 2)));
        Assert.Equal(4, reply[3] & 0x0F);
        Assert.Equal(0x28, reply[2] & 0x78); // the opcode comes back on the reply
    }

    // The two codes are not interchangeable: swapping them would misreport which failure happened.
    [Fact]
    public void FormatErrorAndNotImplemented_WriteDifferentResponseCodes()
    {
        var query = DnsQueryTests.Query("example.com");

        Assert.Equal(1, DnsReply.FormatError(query)![3] & 0x0F);
        Assert.Equal(4, DnsReply.NotImplemented(query)![3] & 0x0F);
    }

    // The question could not be read, or is not ours to answer: it is not echoed and QDCOUNT says so.
    // Neither reply carries an OPT record, since an additional section this server never walked
    // cannot be said to have held one.
    [Theory]
    [InlineData(HeaderOnlyKind.FormatError)]
    [InlineData(HeaderOnlyKind.NotImplemented)]
    public void HeaderOnlyReply_EchoesNoQuestionEvenWhenTheQueryHadOne(HeaderOnlyKind kind)
    {
        var query = DnsQueryTests.QueryWithOpt("example.com", payloadSize: 4096);

        var reply = HeaderOnlyReply(kind, query);

        Assert.Equal(DnsQuery.HeaderLength, reply!.Length);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(4, 2)));   // QDCOUNT
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6, 2)));   // ANCOUNT
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(8, 2)));   // NSCOUNT
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(10, 2)));  // ARCOUNT
    }

    [Fact]
    public void FormatError_SetsTheResponseBitAndAvailableRecursionAndNothingElse()
    {
        var query = DnsQueryTests.Query("example.com", flags1: 0x07, flags2: 0xFF); // AA, TC, RD

        var reply = DnsReply.FormatError(query);

        Assert.Equal(0x81, reply![2]); // QR and RD, with AA and TC dropped
        Assert.Equal(0x81, reply[3]);  // RA and FORMERR, with AD and CD dropped
    }

    // A reply repeats the opcode of its query (RFC 1035 4.1.1), header-only replies included.
    [Theory]
    [InlineData(HeaderOnlyKind.FormatError)]
    [InlineData(HeaderOnlyKind.NotImplemented)]
    public void HeaderOnlyReply_EchoesTheOpcodeOfTheQuery(HeaderOnlyKind kind)
    {
        var query = DnsQueryTests.Query("example.com", flags1: 0x29); // OPCODE 5 (UPDATE), RD

        Assert.Equal(0x28, HeaderOnlyReply(kind, query)![2] & 0x78);
    }

    // Both sides of the header length. A datagram with no header holds no id, and a reply with an
    // invented id answers nobody; the only answer is silence, which null says.
    [Theory]
    [InlineData(HeaderOnlyKind.FormatError)]
    [InlineData(HeaderOnlyKind.NotImplemented)]
    public void HeaderOnlyReply_SaysNothingToADatagramTooShortToHoldAHeader(HeaderOnlyKind kind)
    {
        var header = new byte[DnsQuery.HeaderLength];

        Assert.NotNull(HeaderOnlyReply(kind, header));
        Assert.Null(HeaderOnlyReply(kind, header.AsSpan(0, DnsQuery.HeaderLength - 1).ToArray()));
        Assert.Null(HeaderOnlyReply(kind, []));
    }

    private static void AssertOptRecord(byte[] reply, int questionEnd, byte extendedRcode)
    {
        Assert.Equal(questionEnd + OptRecordLength, reply.Length);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(10, 2))); // ARCOUNT

        var opt = reply.AsSpan(questionEnd);

        Assert.Equal(0, opt[0]);                                                        // NAME: the root
        Assert.Equal(OptType, BinaryPrimitives.ReadUInt16BigEndian(opt.Slice(1, 2)));   // TYPE
        Assert.Equal(DnsReply.UdpPayloadSize, BinaryPrimitives.ReadUInt16BigEndian(opt.Slice(3, 2)));
        Assert.Equal(extendedRcode, opt[5]);                                            // EXTENDED-RCODE
        Assert.Equal(0, opt[6]);                                                        // VERSION
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(opt.Slice(7, 2)));         // DO and Z
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(opt.Slice(9, 2)));         // RDLENGTH
    }

    // A query with one record in the answer and one in the authority section, which Parse accepts.
    private static byte[] QueryCarryingRecords() =>
        DnsQueryTests.WithRecords(
            DnsQueryTests.Query("example.com"),
            answers: 1,
            authorities: 1,
            additionals: 0,
            DnsQueryTests.Record(1, 1, 300, 10, 0, 0, 1),
            DnsQueryTests.Record(2, 1, 300, 0));

    // BadVersion always carries an OPT record and the others only when asked, so a stated length must allow for it.
    private static int OptRecordLengthOf(ReplyKind kind) =>
        kind == ReplyKind.BadVersion ? OptRecordLength : 0;

    private static byte[] Reply(ReplyKind kind, byte[] query) =>
        Reply(kind, query, DnsQuery.Parse(query).QuestionEnd);

    private static byte[] Reply(ReplyKind kind, byte[] query, int questionEnd, bool hasEdns = false) => kind switch
    {
        ReplyKind.NameDoesNotExist => DnsReply.NameDoesNotExist(query, questionEnd, hasEdns),
        ReplyKind.ServerFailure => DnsReply.ServerFailure(query, questionEnd, hasEdns),
        ReplyKind.Truncated => DnsReply.Truncated(query, questionEnd, hasEdns),
        ReplyKind.BadVersion => DnsReply.BadVersion(query, questionEnd),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static byte[]? HeaderOnlyReply(HeaderOnlyKind kind, byte[] query) => kind switch
    {
        HeaderOnlyKind.FormatError => DnsReply.FormatError(query),
        HeaderOnlyKind.NotImplemented => DnsReply.NotImplemented(query),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
