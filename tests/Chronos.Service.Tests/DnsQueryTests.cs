using System.Buffers.Binary;
using Chronos.Service.Dns;

namespace Chronos.Service.Tests;

public sealed class DnsQueryTests
{
    private const byte RecursionDesired = 0x01;
    private const ushort OptType = 41;

    // Spelled out once so the buffers below are built from literals, not the constants they pin.
    [Fact]
    public void TheConstantsAreTheOnesTheRfcsGive()
    {
        Assert.Equal(12, DnsQuery.HeaderLength);
        Assert.Equal(512, DnsQuery.DefaultUdpPayloadSize);
        Assert.Equal(1, DnsQuery.ClassInternet);
    }

    [Fact]
    public void Parse_ReadsTheQuestionOfAnOrdinaryQuery()
    {
        var query = Query("example.com", type: 1, id: 0x1234);

        var parsed = DnsQuery.Parse(query);

        Assert.Equal(DnsQueryStatus.Ok, parsed.Status);
        Assert.Equal(0x1234, parsed.Id);
        Assert.Equal("example.com", parsed.Name);
        Assert.Equal(1, parsed.Type);
        Assert.Equal(DnsQuery.ClassInternet, parsed.Class);
        Assert.Equal(query.Length, parsed.QuestionEnd);
        Assert.Equal(DnsQuery.DefaultUdpPayloadSize, parsed.UdpPayloadSize);
    }

    [Fact]
    public void Parse_LowerCasesTheNameItReads()
    {
        Assert.Equal("www.example.com", DnsQuery.Parse(Query("WWW.Example.COM")).Name);
    }

    [Fact]
    public void Parse_ReadsAQuestionForTheRootAsAnEmptyName()
    {
        var parsed = DnsQuery.Parse(RootQuery(type: 2, qclass: 1));

        Assert.Equal(DnsQueryStatus.Ok, parsed.Status);
        Assert.Equal(string.Empty, parsed.Name);
        Assert.Equal(17, parsed.QuestionEnd); // 12 header + 1 root + 2 QTYPE + 2 QCLASS
    }

    // Neither type nor class is judged here: a question with no rule is forwarded, and the upstream server decides.
    [Theory]
    [InlineData(1, 1)]        // A IN
    [InlineData(28, 1)]       // AAAA IN
    [InlineData(255, 1)]      // ANY IN
    [InlineData(16, 3)]       // TXT CH: not the internet class at all
    [InlineData(65535, 255)]
    public void Parse_ReadsAnyTypeAndClassTheQuestionCarries(int type, int qclass)
    {
        var parsed = DnsQuery.Parse(Query("example.com", type: (ushort)type, qclass: (ushort)qclass));

        Assert.Equal(DnsQueryStatus.Ok, parsed.Status);
        Assert.Equal(type, parsed.Type);
        Assert.Equal(qclass, parsed.Class);
    }

    // QuestionEnd is the end of the question, not of the message: a query with an OPT record ends well past it.
    [Fact]
    public void Parse_EndsTheQuestionWhereItEndsAndNotWhereTheMessageDoes()
    {
        var plain = Query("example.com");
        var withOpt = QueryWithOpt("example.com", payloadSize: 4096);

        Assert.True(withOpt.Length > plain.Length);
        Assert.Equal(plain.Length, DnsQuery.Parse(withOpt).QuestionEnd);
    }

    [Fact]
    public void Parse_ReadsTheAdvertisedBufferFromAnEdnsOptRecord()
    {
        var query = QueryWithOpt("example.com", payloadSize: 4096);

        Assert.Equal(4096, DnsQuery.Parse(query).UdpPayloadSize);
    }

    // RFC 6891 6.2.3: a requestor payload size below 512 is treated as 512. Both sides of the floor, and the first value above it.
    [Theory]
    [InlineData(0, 512)]
    [InlineData(511, 512)]
    [InlineData(512, 512)]
    [InlineData(513, 513)]
    [InlineData(65535, 65535)]
    public void Parse_TreatsAnAdvertisedBufferBelowTheFloorAsTheFloor(int advertised, int expected)
    {
        var query = QueryWithOpt("example.com", payloadSize: (ushort)advertised);

        Assert.Equal(expected, DnsQuery.Parse(query).UdpPayloadSize);
    }

    // The OPT record carries options in its RDATA; a walk that ignored RDLENGTH would land in the middle of them.
    [Fact]
    public void Parse_ReadsAnOptRecordThatCarriesOptions()
    {
        var query = WithRecords(
            Query("example.com"),
            answers: 0,
            authorities: 0,
            additionals: 2,
            Opt(4096, 0x00, 0x0A, 0x00, 0x02, 0xAB, 0xCD), // one option, two bytes of it
            Record(1, 1, 300, 10, 0, 0, 1));

        Assert.Equal(4096, DnsQuery.Parse(query).UdpPayloadSize);
    }

    // The OPT record may sit anywhere in the additional section, so it is found by walking.
    [Fact]
    public void Parse_FindsAnOptRecordThatIsNotTheFirstAdditionalOne()
    {
        var query = WithRecords(
            Query("example.com"),
            answers: 0,
            authorities: 0,
            additionals: 2,
            Record(1, 1, 300, 10, 0, 0, 1), // an A record, four bytes of RDATA
            Opt(1232));

        Assert.Equal(1232, DnsQuery.Parse(query).UdpPayloadSize);
    }

    // A query may carry records in the answer and authority sections too; the additional section starts after them.
    [Fact]
    public void Parse_WalksTheAnswerAndAuthoritySectionsBeforeTheAdditionalOnes()
    {
        var query = WithRecords(
            Query("example.com"),
            answers: 1,
            authorities: 1,
            additionals: 1,
            Record(1, 1, 300, 10, 0, 0, 1),
            Record(2, 1, 300, 0),
            Opt(2048));

        Assert.Equal(2048, DnsQuery.Parse(query).UdpPayloadSize);
    }

    // RFC 6891 6.1.1: at most one OPT record, and a second is a format error.
    [Fact]
    public void Parse_RefusesASecondOptRecord()
    {
        var query = WithRecords(
            Query("example.com"),
            answers: 0,
            authorities: 0,
            additionals: 2,
            Opt(4096),
            Opt(1232));

        Assert.Equal(DnsQueryStatus.Malformed, DnsQuery.Parse(query).Status);
    }

    // The question read fine; the section after it did not. Answering with a buffer size from a message that failed to read would be wrong.
    [Fact]
    public void Parse_RefusesAnAdditionalSectionItCannotWalk()
    {
        var query = QueryWithOpt("example.com", payloadSize: 4096);

        Assert.Equal(DnsQueryStatus.Malformed, DnsQuery.Parse(query.AsSpan(0, query.Length - 1)).Status);
    }

    // A record whose RDLENGTH claims more bytes than the message holds; nothing else is wrong with it.
    [Fact]
    public void Parse_RefusesARecordWhoseDataRunsPastTheEndOfTheMessage()
    {
        var query = WithRecords(Query("example.com"), 0, 0, 1, Opt(4096, 1, 2, 3));
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(query.Length - 5, 2), 4); // RDLENGTH, one too many

        Assert.Equal(DnsQueryStatus.Malformed, DnsQuery.Parse(query).Status);
    }

    [Fact]
    public void Parse_RefusesARecordCountItCannotSatisfy()
    {
        var query = WithRecords(Query("example.com"), 0, 0, 2, Opt(4096));

        Assert.Equal(DnsQueryStatus.Malformed, DnsQuery.Parse(query).Status);
    }

    [Fact]
    public void Parse_ReportsNoEdnsForAQueryThatCarriesNoOptRecord()
    {
        Assert.False(DnsQuery.Parse(Query("example.com")).HasEdns);
    }

    [Fact]
    public void Parse_ReportsEdnsForAQueryThatCarriesOne()
    {
        Assert.True(DnsQuery.Parse(QueryWithOpt("example.com", payloadSize: 4096)).HasEdns);
    }

    // Same buffer size, different queries: one asked in EDNS and is owed an OPT record back (RFC 6891
    // 6.1.1), the other never mentioned EDNS. A parse that carried only the size could not tell them apart.
    [Fact]
    public void Parse_TellsAnOptAskingForTheFloorApartFromNoOptAtAll()
    {
        var withOpt = DnsQuery.Parse(QueryWithOpt("example.com", payloadSize: 512));
        var without = DnsQuery.Parse(Query("example.com"));

        Assert.Equal(without.UdpPayloadSize, withOpt.UdpPayloadSize);
        Assert.True(withOpt.HasEdns);
        Assert.False(without.HasEdns);
    }

    // RFC 6891 6.1.1 puts the OPT record in the additional section. The earlier sections are still
    // walked, but an OPT found in one of them is a record this parser has no reading for.
    [Theory]
    [InlineData(1, 0)] // in the answer section
    [InlineData(0, 1)] // in the authority section
    public void Parse_RefusesAnOptRecordOutsideTheAdditionalSection(int answers, int authorities)
    {
        var query = WithRecords(Query("example.com"), (ushort)answers, (ushort)authorities, 0, Opt(4096));

        Assert.Equal(DnsQueryStatus.Malformed, DnsQuery.Parse(query).Status);
    }

    // The same record one section along is the ordinary case, so the refusal is about where the record sits.
    [Fact]
    public void Parse_TakesTheSameOptRecordInTheAdditionalSection()
    {
        var query = WithRecords(Query("example.com"), 0, 0, 1, Opt(4096));

        Assert.Equal(DnsQueryStatus.Ok, DnsQuery.Parse(query).Status);
        Assert.Equal(4096, DnsQuery.Parse(query).UdpPayloadSize);
    }

    // RFC 6891 6.1.2: the NAME of an OPT record must be the root; anything else is shaped like an OPT but unreadable.
    [Fact]
    public void Parse_RefusesAnOptRecordThatIsNotOwnedByTheRoot()
    {
        var query = WithRecords(Query("example.com"), 0, 0, 1, OptOwnedBy("example.com", 4096));

        Assert.Equal(DnsQueryStatus.Malformed, DnsQuery.Parse(query).Status);
    }

    // RFC 6891 6.1.3: a version this responder does not speak is answered with BADVERS; it speaks 0 only.
    [Theory]
    [InlineData(0, DnsQueryStatus.Ok)]
    [InlineData(1, DnsQueryStatus.BadVersion)]
    [InlineData(255, DnsQueryStatus.BadVersion)]
    public void Parse_SpeaksEdnsVersionZeroAndNoOther(int version, DnsQueryStatus expected)
    {
        var query = WithRecords(Query("example.com"), 0, 0, 1, OptWithFlags(4096, version: (byte)version));

        Assert.Equal(expected, DnsQuery.Parse(query).Status);
    }

    // The version is one octet of four. The extended response code and DO bit are not this server's
    // business, and reading the wrong octet would call this query's version wrong.
    [Fact]
    public void Parse_ReadsTheVersionAndNotTheRestOfTheOptRecordsTtlField()
    {
        var query = WithRecords(
            Query("example.com"),
            0,
            0,
            1,
            OptWithFlags(4096, version: 0, extendedRcode: 0xFF, dnssecOk: true));

        Assert.Equal(DnsQueryStatus.Ok, DnsQuery.Parse(query).Status);
    }

    // BADVERS is answered with a reply that echoes the question, so unlike other refusals it must carry the question it read.
    [Fact]
    public void Parse_CarriesTheQuestionOfAQueryWhoseEdnsVersionIsWrong()
    {
        var query = WithRecords(Query("example.com", id: 0x0BAD), 0, 0, 1, OptWithFlags(4096, version: 1));

        var parsed = DnsQuery.Parse(query);

        Assert.Equal(DnsQueryStatus.BadVersion, parsed.Status);
        Assert.Equal(0x0BAD, parsed.Id);
        Assert.Equal("example.com", parsed.Name);
        Assert.Equal(Query("example.com").Length, parsed.QuestionEnd);
        Assert.True(parsed.HasEdns);
    }

    // A message that does not read at all is malformed first: a version from a section that failed to walk is a number from nowhere.
    [Fact]
    public void Parse_CallsAMessageItCannotWalkMalformedRatherThanBadVersion()
    {
        var query = WithRecords(Query("example.com"), 0, 0, 2, OptWithFlags(4096, version: 1));

        Assert.Equal(DnsQueryStatus.Malformed, DnsQuery.Parse(query).Status);
    }

    [Fact]
    public void Parse_RefusesAMessageThatIsAnAnswer()
    {
        var query = Query("example.com");
        query[2] |= 0x80; // QR

        Assert.Equal(DnsQueryStatus.NotAQuery, DnsQuery.Parse(query).Status);
    }

    // NOTIMP, not FORMERR: an UPDATE reads fine, it just asks for something this server does not do.
    [Fact]
    public void Parse_RefusesAnOpcodeItDoesNotServe()
    {
        var query = Query("example.com");
        query[2] |= 0x28; // OPCODE 5 (UPDATE)

        Assert.Equal(DnsQueryStatus.NotImplemented, DnsQuery.Parse(query).Status);
    }

    // Both sides of the one opcode this server answers. IQUERY (1) was obsoleted by RFC 3425 and is refused.
    [Theory]
    [InlineData(0, DnsQueryStatus.Ok)]
    [InlineData(1, DnsQueryStatus.NotImplemented)]
    [InlineData(2, DnsQueryStatus.NotImplemented)]
    [InlineData(15, DnsQueryStatus.NotImplemented)]
    public void Parse_ServesTheQueryOpcodeAndNoOther(int opcode, DnsQueryStatus expected)
    {
        var query = Query("example.com");
        query[2] = (byte)((query[2] & ~0x78) | (opcode << 3));

        Assert.Equal(expected, DnsQuery.Parse(query).Status);
    }

    // One question, no more and no fewer: neither zero nor two can be matched against a plan. Malformed
    // rather than NotImplemented, since a QUERY with two questions does not add up.
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(65535)]
    public void Parse_RefusesAQueryThatDoesNotCarryExactlyOneQuestion(int questions)
    {
        var query = Query("example.com");
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), (ushort)questions);

        Assert.Equal(DnsQueryStatus.Malformed, DnsQuery.Parse(query).Status);
    }

    // A pointer names a prior occurrence (RFC 1035 4.1.4), and before the first question there is only
    // the header to point at. Read, it gives a name built from the header's octets, and a reply echoing
    // it would answer a question nobody asked.
    [Fact]
    public void Parse_RefusesAQuestionNameThatIsNothingButACompressionPointer()
    {
        var query = new byte[12 + 2 + 4];
        query[2] = RecursionDesired;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), 1);
        query[12] = 0xC0; // a pointer...
        query[13] = 0x00; // ...back to the first octet of the header, which parses as the root

        Assert.Equal(DnsQueryStatus.Malformed, DnsQuery.Parse(query).Status);
    }

    // The same refusal for a name that is half real: only the pointer itself is wrong with it.
    [Fact]
    public void Parse_RefusesAQuestionNameThatEndsInACompressionPointer()
    {
        var query = new byte[12 + 4 + 2 + 4];
        query[2] = RecursionDesired;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), 1);
        query[12] = 3;
        query[13] = (byte)'w';
        query[14] = (byte)'w';
        query[15] = (byte)'w';
        query[16] = 0xC0;
        query[17] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(18, 2), 1); // QTYPE
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(20, 2), 1); // QCLASS

        Assert.Equal(DnsQueryStatus.Malformed, DnsQuery.Parse(query).Status);
    }

    // The refusal is of the pointer, not of every name: an ordinary question is still read.
    [Fact]
    public void Parse_TakesAQuestionNameThatCarriesNoPointer()
    {
        Assert.Equal(DnsQueryStatus.Ok, DnsQuery.Parse(Query("www.example.com")).Status);
    }

    [Fact]
    public void Parse_RefusesAQuestionCutShort()
    {
        var query = Query("example.com");

        Assert.Equal(DnsQueryStatus.Malformed, DnsQuery.Parse(query.AsSpan(0, query.Length - 2)).Status);
    }

    [Fact]
    public void Parse_RefusesAQuestionWithNoTypeOrClassAtAll()
    {
        var query = Query("example.com");

        Assert.Equal(DnsQueryStatus.Malformed, DnsQuery.Parse(query.AsSpan(0, query.Length - 4)).Status);
    }

    // The name itself is untrusted. A forward pointer is the codec's refusal and the only thing wrong
    // here: the four bytes after the name are all there, so nothing is refused for running off the end.
    [Fact]
    public void Parse_RefusesAQuestionWhoseNameDoesNotParse()
    {
        var query = new byte[12 + 2 + 4];
        query[2] = RecursionDesired;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), 1);
        query[12] = 0xC0; // a pointer...
        query[13] = 0x20; // ...to offset 32, which is forward and past the end

        Assert.Equal(DnsQueryStatus.Malformed, DnsQuery.Parse(query).Status);
    }

    // Both sides of the header length. Twelve bytes are enough to see this is an answer; eleven are
    // not enough to see anything, and reading flags from them would read whatever follows the buffer.
    [Fact]
    public void Parse_RefusesAMessageShorterThanAHeaderBeforeReadingAnythingInIt()
    {
        var header = new byte[12];
        header[2] = 0x80; // QR

        Assert.Equal(DnsQueryStatus.NotAQuery, DnsQuery.Parse(header).Status);
        Assert.Equal(DnsQueryStatus.Malformed, DnsQuery.Parse(header.AsSpan(0, 11)).Status);
    }

    [Fact]
    public void Parse_RefusesAnEmptyMessage()
    {
        Assert.Equal(DnsQueryStatus.Malformed, DnsQuery.Parse([]).Status);
    }

    // The id of a query that will not be served still has to come back on the refusal.
    [Fact]
    public void Parse_CarriesTheIdOfAQueryItRefuses()
    {
        var query = Query("example.com", id: 0xBEEF);
        query[2] |= 0x80; // QR

        Assert.Equal(0xBEEF, DnsQuery.Parse(query).Id);
    }

    [Fact]
    public void Parse_ReportsTheDefaultBufferAndNoNameOnAQueryItCannotRead()
    {
        var parsed = DnsQuery.Parse(Query("example.com").AsSpan(0, 14));

        Assert.Equal(DnsQueryStatus.Malformed, parsed.Status);
        Assert.Equal(string.Empty, parsed.Name);
        Assert.Equal(0, parsed.QuestionEnd);
        Assert.Equal(DnsQuery.DefaultUdpPayloadSize, parsed.UdpPayloadSize);
    }

    internal static byte[] Query(
        string name,
        ushort type = 1,
        ushort id = 1,
        ushort qclass = 1,
        byte flags1 = RecursionDesired,
        byte flags2 = 0)
    {
        var encoded = DnsName.Encode(name);
        var query = new byte[12 + encoded.Length + 4];

        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(0, 2), id);
        query[2] = flags1;
        query[3] = flags2;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), 1); // QDCOUNT

        encoded.CopyTo(query.AsSpan(12));
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(12 + encoded.Length, 2), type);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(14 + encoded.Length, 2), qclass);

        return query;
    }

    // A question for the root, which DnsName.Encode cannot produce: it has no labels to encode.
    internal static byte[] RootQuery(ushort type = 1, ushort qclass = 1, ushort id = 1)
    {
        var query = new byte[12 + 1 + 4];

        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(0, 2), id);
        query[2] = RecursionDesired;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), 1); // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(13, 2), type);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(15, 2), qclass);

        return query;
    }

    internal static byte[] QueryWithOpt(string name, ushort payloadSize, ushort type = 1, ushort id = 1) =>
        WithRecords(Query(name, type, id), 0, 0, 1, Opt(payloadSize));

    internal static byte[] WithRecords(
        byte[] query,
        ushort answers,
        ushort authorities,
        ushort additionals,
        params byte[][] records)
    {
        var message = new List<byte>(query);
        foreach (var record in records)
        {
            message.AddRange(record);
        }

        var bytes = message.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6, 2), answers);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8, 2), authorities);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10, 2), additionals);
        return bytes;
    }

    // A resource record owned by the root, which every walk must step over by its RDLENGTH whatever the type.
    internal static byte[] Record(ushort type, ushort qclass, uint ttl, params byte[] rdata)
    {
        var record = new byte[1 + 10 + rdata.Length];

        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(1, 2), type);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(3, 2), qclass);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(5, 4), ttl);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(9, 2), (ushort)rdata.Length);
        rdata.CopyTo(record.AsSpan(11));

        return record;
    }

    // In an OPT record the class field is the requestor's UDP buffer size (RFC 6891 6.1.2).
    private static byte[] Opt(ushort payloadSize, params byte[] rdata) =>
        Record(OptType, payloadSize, 0, rdata);

    // The same record with the four octets in the TTL position spelled out: not a lifetime but
    // EXTENDED-RCODE, VERSION, DO and fifteen reserved bits (RFC 6891 6.1.3).
    private static byte[] OptWithFlags(
        ushort payloadSize,
        byte version,
        byte extendedRcode = 0,
        bool dnssecOk = false) =>
        Record(
            OptType,
            payloadSize,
            ((uint)extendedRcode << 24) | ((uint)version << 16) | (dnssecOk ? 0x8000u : 0u));

    // An OPT record owned by a name rather than by the root, which RFC 6891 6.1.2 does not allow.
    private static byte[] OptOwnedBy(string owner, ushort payloadSize)
    {
        var name = DnsName.Encode(owner);
        var record = new byte[name.Length + 10];

        name.CopyTo(record, 0);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(name.Length, 2), OptType);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(name.Length + 2, 2), payloadSize);
        // TTL and RDLENGTH stay zero: nothing but the owner is wrong with this record.

        return record;
    }
}
