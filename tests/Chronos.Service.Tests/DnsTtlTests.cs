using System.Buffers.Binary;
using Chronos.Service.Dns;

namespace Chronos.Service.Tests;

public sealed class DnsTtlTests
{
    internal const string Question = "example.com";
    internal const ushort TypeA = 1;
    internal const ushort ClassInternet = 1;

    private const ushort OptType = 41;
    private const ushort DefaultId = 0x1234;

    [Fact]
    public void Smallest_FindsTheLowestTtlAcrossSections()
    {
        var response = Response([(Question, 300u)], [("ns.example.com", 60u)], [("cdn.example.com", 120u)]);

        Assert.Equal(60u, DnsTtl.Smallest(response));
    }

    // Each of the three counts in turn holds the record that expires first; a walk stopping one section early answers with a TTL the response lacks.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Smallest_ReadsEveryOneOfTheThreeSections(int section)
    {
        (string Owner, uint Ttl)[] Records(int index) => [(Question, index == section ? 60u : 300u)];

        Assert.Equal(60u, DnsTtl.Smallest(Response(Records(0), Records(1), Records(2))));
    }

    [Fact]
    public void Smallest_IsNullForAResponseWithNoRecords()
    {
        Assert.Null(DnsTtl.Smallest(Response()));
    }

    // Zero is a TTL like any other: "do not cache this" is something the response said, while null means it said nothing. Only the cache tells them apart.
    [Fact]
    public void Smallest_ReadsATtlOfZeroRatherThanCallingItAbsent()
    {
        Assert.Equal(0u, DnsTtl.Smallest(Response([(Question, 0u)])));
    }

    [Fact]
    public void Smallest_ReadsTheWholeThirtyTwoBitField()
    {
        Assert.Equal(uint.MaxValue, DnsTtl.Smallest(Response([(Question, uint.MaxValue)])));
    }

    // RFC 6891 6.1.3: those four octets are EXTENDED-RCODE, VERSION, DO and Z. Read as a lifetime they are the smallest number in most responses.
    [Fact]
    public void Smallest_IgnoresTheOptRecord()
    {
        var response = Response([(Question, 300u)], optFlags: 0);

        Assert.Equal(300u, DnsTtl.Smallest(response));
    }

    [Fact]
    public void Smallest_IsNullWhenTheOnlyRecordIsAnOpt()
    {
        Assert.Null(DnsTtl.Smallest(Response(optFlags: 0)));
    }

    // How a real answer is written: the owner is a pointer back to the question's name.
    [Fact]
    public void Smallest_ReadsARecordOwnedByACompressionPointer()
    {
        Assert.Equal(90u, DnsTtl.Smallest(PointerOwnedResponse(90)));
    }

    [Fact]
    public void Smallest_ReadsARecordTypeItDoesNotKnow()
    {
        var response = Message(1, 0, 0, [Record(Question, type: 99, ClassInternet, 45, 1, 2, 3, 4, 5, 6, 7)]);

        Assert.Equal(45u, DnsTtl.Smallest(response));
    }

    // A response with no question echoed. The section is walked QDCOUNT times and not once.
    [Fact]
    public void Smallest_ReadsARecordThatFollowsAQuestionlessHeader()
    {
        var response = Message(1, 0, 0, [Record(Question, TypeA, ClassInternet, 75, 10, 0, 0, 1)], question: null);

        Assert.Equal(75u, DnsTtl.Smallest(response));
    }

    [Fact]
    public void Smallest_IsNullForAMessageTooShortForAHeader()
    {
        Assert.Null(DnsTtl.Smallest(new byte[DnsQuery.HeaderLength - 1]));
    }

    [Fact]
    public void Smallest_IsNullForARecordCountItCannotSatisfy()
    {
        var response = Message(2, 0, 0, [Record(Question, TypeA, ClassInternet, 300, 10, 0, 0, 1)]);

        Assert.Null(DnsTtl.Smallest(response));
    }

    // The owner name reads; five of the ten octets of TYPE, CLASS, TTL and RDLENGTH behind it are missing. Nothing may be read from an incomplete header.
    [Fact]
    public void Smallest_IsNullForARecordHeaderCutShort()
    {
        var response = Response([(Question, 300u)]);

        Assert.Null(DnsTtl.Smallest(response.AsSpan(0, response.Length - 9)));
    }

    [Fact]
    public void Smallest_IsNullForARecordWhoseDataRunsPastTheEnd()
    {
        var response = Response([(Question, 300u)]);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(response.Length - 6, 2), 5); // RDLENGTH, one too many

        Assert.Null(DnsTtl.Smallest(response));
    }

    // The message carries no records on purpose: with one, the record walk would fail on the same
    // missing octets and say nothing about the question section. Null is also the answer for a response
    // with no records, so the TryAge twin below tells a refused walk from one that finished empty.
    [Fact]
    public void Smallest_IsNullForAQuestionWithNoTypeAndClassBehindIt()
    {
        var response = Message(0, 0, 0, []);
        var throughTheQuestionName = DnsQuery.HeaderLength + DnsName.Encode(Question).Length;

        Assert.Null(DnsTtl.Smallest(response.AsSpan(0, throughTheQuestionName)));
    }

    [Fact]
    public void TryAge_SubtractsTheHeldSecondsFromEveryRecord()
    {
        var response = Response([(Question, 300u)], [("ns.example.com", 60u)], [("cdn.example.com", 120u)]);

        Assert.True(DnsTtl.TryAge(response, 30));

        Assert.Equal<uint>([270, 30, 90], TtlsOf(response));
    }

    // The floor from both sides: a TTL with a second left keeps it, and one that would reach zero or pass it is written as one.
    [Theory]
    [InlineData(31u, 29u, 2u)]
    [InlineData(31u, 30u, 1u)]
    [InlineData(31u, 31u, 1u)]
    [InlineData(31u, 600u, 1u)]
    [InlineData(1u, 0u, 1u)]
    public void TryAge_NeverWritesATtlOfZero(uint ttl, uint seconds, uint aged)
    {
        var response = Response([(Question, ttl)]);

        Assert.True(DnsTtl.TryAge(response, seconds));

        Assert.Equal<uint>([aged], TtlsOf(response));
    }

    // The one TTL that is not a duration to shorten: zero says the record may serve this transaction
    // only (RFC 1035 4.1.3). Ageing it to one would turn a refusal to cache into a record with a second left.
    [Theory]
    [InlineData(0u)]
    [InlineData(5u)]
    public void TryAge_LeavesATtlOfZeroAlone(uint seconds)
    {
        var response = Response([(Question, 0u)]);

        Assert.True(DnsTtl.TryAge(response, seconds));

        Assert.Equal<uint>([0], TtlsOf(response));
    }

    // RFC 6891 6.1.3 from the writing side: subtracting from an OPT record's flags would answer with an extended response code and DO bit no server set.
    [Fact]
    public void TryAge_LeavesTheOptRecordsFlagsAlone()
    {
        const uint Flags = 0x10008000; // EXTENDED-RCODE 16, VERSION 0, DO set
        var response = Response([(Question, 300u)], optFlags: Flags);

        Assert.True(DnsTtl.TryAge(response, 30));

        Assert.Equal(Flags, BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(response.Length - 6, 4)));
        Assert.Equal<uint>([270], TtlsOf(response));
    }

    [Fact]
    public void TryAge_AgesARecordOwnedByACompressionPointer()
    {
        var response = PointerOwnedResponse(90);

        Assert.True(DnsTtl.TryAge(response, 30));

        Assert.Equal<uint>([60], TtlsOf(response));
    }

    [Fact]
    public void TryAge_StepsOverARecordTypeItDoesNotKnowByItsLength()
    {
        var response = Message(
            2,
            0,
            0,
            [
                Record(Question, type: 99, ClassInternet, 300, 1, 2, 3, 4, 5, 6, 7),
                Record(Question, TypeA, ClassInternet, 200, 10, 0, 0, 1),
            ]);

        Assert.True(DnsTtl.TryAge(response, 30));

        Assert.Equal<uint>([270, 170], TtlsOf(response));
    }

    [Fact]
    public void TryAge_RefusesAResponseItCannotWalk()
    {
        var response = Response([(Question, 10u)]);

        Assert.False(DnsTtl.TryAge(response.AsSpan(0, response.Length - 3), 1));
    }

    [Fact]
    public void TryAge_RefusesARecordHeaderCutShort()
    {
        var response = Response([(Question, 300u)]);

        Assert.False(DnsTtl.TryAge(response.AsSpan(0, response.Length - 9), 1));
    }

    [Fact]
    public void TryAge_RefusesAMessageTooShortForAHeader()
    {
        Assert.False(DnsTtl.TryAge(new byte[DnsQuery.HeaderLength - 1], 1));
    }

    // The message carries no records on purpose: with one, the record walk would fail on the same missing octets and say nothing about the question section.
    [Fact]
    public void TryAge_RefusesAQuestionWithNoTypeAndClassBehindIt()
    {
        var response = Message(0, 0, 0, []);
        var throughTheQuestionName = DnsQuery.HeaderLength + DnsName.Encode(Question).Length;

        Assert.False(DnsTtl.TryAge(response.AsSpan(0, throughTheQuestionName), 1));
    }

    [Fact]
    public void TryAge_LeavesTheRestOfTheMessageAsItFoundIt()
    {
        var response = Response([(Question, 300u)]);
        var before = (byte[])response.Clone();

        Assert.True(DnsTtl.TryAge(response, 30));

        // Only the four octets of the one TTL differ; everything else is what the upstream server wrote.
        var ttlAt = response.Length - 10;
        Assert.Equal(before.AsSpan(0, ttlAt).ToArray(), response.AsSpan(0, ttlAt).ToArray());
        Assert.Equal(before.AsSpan(ttlAt + 4).ToArray(), response.AsSpan(ttlAt + 4).ToArray());
    }

    // BADVERS, the first code that does not fit the header: 16 is 1 in the OPT record's EXTENDED-RCODE and 0 in the header's four bits.
    [Fact]
    public void ExtendedRcode_ReadsTheTopEightBitsOutOfTheOptRecord()
    {
        Assert.Equal(1, DnsTtl.ExtendedRcode(Response([(Question, 300u)], optFlags: 0x01000000)));
    }

    [Fact]
    public void ExtendedRcode_ReadsTheWholeOctet()
    {
        Assert.Equal(255, DnsTtl.ExtendedRcode(Response([(Question, 300u)], optFlags: 0xFF000000)));
    }

    // Only the first of the four octets is the code; VERSION and DO fill the rest, and reading the field as one number would call every EDNS answer a failure.
    [Fact]
    public void ExtendedRcode_IsZeroWhenOnlyTheVersionAndTheFlagsAreSet()
    {
        Assert.Equal(0, DnsTtl.ExtendedRcode(Response([(Question, 300u)], optFlags: 0x00FF8000)));
    }

    // The OPT record is the last of four, one section past the two the walk starts in.
    [Fact]
    public void ExtendedRcode_FindsAnOptBehindTheRecordsOfEveryOtherSection()
    {
        var response = Response(
            [(Question, 300u)],
            [("ns.example.com", 60u)],
            [("cdn.example.com", 120u)],
            optFlags: 0x01000000);

        Assert.Equal(1, DnsTtl.ExtendedRcode(response));
    }

    // No OPT record, so the header's four bits are the whole code and there is nothing above them.
    [Fact]
    public void ExtendedRcode_IsZeroForAResponseWithNoOptRecord()
    {
        Assert.Equal(0, DnsTtl.ExtendedRcode(Response([(Question, 300u)])));
    }

    // A code read from a record that does not read is not a code; zero leaves the verdict to the header.
    [Fact]
    public void ExtendedRcode_IsZeroForAResponseItCannotWalk()
    {
        var response = Response([(Question, 300u)], optFlags: 0x01000000);

        Assert.Equal(0, DnsTtl.ExtendedRcode(response.AsSpan(0, response.Length - 3)));
    }

    [Fact]
    public void ExtendedRcode_IsZeroForAMessageTooShortForAHeader()
    {
        Assert.Equal(0, DnsTtl.ExtendedRcode(new byte[DnsQuery.HeaderLength - 1]));
    }

    /// <summary>A response to a question for <see cref="Question"/>, with one A record per entry in each named section, and an OPT record in the additional section when asked for.</summary>
    internal static byte[] Response(
        (string Owner, uint Ttl)[]? answers = null,
        (string Owner, uint Ttl)[]? authorities = null,
        (string Owner, uint Ttl)[]? additionals = null,
        ushort id = DefaultId,
        byte rcode = 0,
        uint? optFlags = null)
    {
        var an = answers ?? [];
        var ns = authorities ?? [];
        var ar = additionals ?? [];

        var records = new List<byte[]>();
        foreach (var (owner, ttl) in an.Concat(ns).Concat(ar))
        {
            records.Add(Record(owner, TypeA, ClassInternet, ttl, 10, 0, 0, 1));
        }

        if (optFlags is { } flags)
        {
            // The OPT record is owned by the root and its class is the buffer size (RFC 6891 6.1.2).
            records.Add(Record(string.Empty, OptType, 4096, flags));
        }

        return Message(
            (ushort)an.Length,
            (ushort)ns.Length,
            (ushort)(ar.Length + (optFlags is null ? 0 : 1)),
            [.. records],
            id,
            rcode);
    }

    /// <summary>The same message with the three counts said out loud, so a test can promise records the bytes do not hold, and with the question left out.</summary>
    internal static byte[] Message(
        ushort answers,
        ushort authorities,
        ushort additionals,
        byte[][] records,
        ushort id = DefaultId,
        byte rcode = 0,
        string? question = Question)
    {
        var message = new List<byte>(new byte[DnsQuery.HeaderLength]);

        if (question is not null)
        {
            message.AddRange(DnsName.Encode(question));
            message.AddRange([0, 1, 0, 1]); // QTYPE A, QCLASS IN
        }

        foreach (var record in records)
        {
            message.AddRange(record);
        }

        var bytes = message.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0, 2), id);
        bytes[2] = 0x80;                        // QR: this is an answer
        bytes[3] = (byte)(0x80 | rcode);        // RA, then the response code
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), question is null ? (ushort)0 : (ushort)1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6, 2), answers);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8, 2), authorities);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10, 2), additionals);

        return bytes;
    }

    /// <summary>One resource record. An empty owner is the root, which cannot be encoded.</summary>
    internal static byte[] Record(string owner, ushort type, ushort qclass, uint ttl, params byte[] rdata)
    {
        byte[] name = owner.Length == 0 ? [0] : DnsName.Encode(owner);
        var record = new byte[name.Length + 10 + rdata.Length];

        name.CopyTo(record, 0);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(name.Length, 2), type);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(name.Length + 2, 2), qclass);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(name.Length + 4, 4), ttl);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(name.Length + 8, 2), (ushort)rdata.Length);
        rdata.CopyTo(record, name.Length + 10);

        return record;
    }

    /// <summary>One answer whose owner is a pointer to the question's name, as a real server writes it.</summary>
    private static byte[] PointerOwnedResponse(uint ttl)
    {
        var record = new byte[2 + 10 + 4];

        record[0] = 0xC0;                     // a pointer, and
        record[1] = DnsQuery.HeaderLength;    // the question's name begins right after the header
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(2, 2), TypeA);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(4, 2), ClassInternet);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(6, 4), ttl);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(10, 2), 4);

        return Message(1, 0, 0, [record]);
    }

    /// <summary>Every TTL in a response, in wire order, OPT records left out. Walked by this file's own arithmetic, since a walk checked against itself agrees with itself.</summary>
    internal static uint[] TtlsOf(ReadOnlySpan<byte> response)
    {
        var ttls = new List<uint>();
        var questions = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(4, 2));
        var records =
            BinaryPrimitives.ReadUInt16BigEndian(response.Slice(6, 2)) +
            BinaryPrimitives.ReadUInt16BigEndian(response.Slice(8, 2)) +
            BinaryPrimitives.ReadUInt16BigEndian(response.Slice(10, 2));

        var pos = DnsQuery.HeaderLength;

        for (var question = 0; question < questions; question++)
        {
            pos = PastName(response, pos) + 4;
        }

        for (var record = 0; record < records; record++)
        {
            pos = PastName(response, pos);

            if (BinaryPrimitives.ReadUInt16BigEndian(response.Slice(pos, 2)) != OptType)
            {
                ttls.Add(BinaryPrimitives.ReadUInt32BigEndian(response.Slice(pos + 4, 4)));
            }

            pos += 10 + BinaryPrimitives.ReadUInt16BigEndian(response.Slice(pos + 8, 2));
        }

        return [.. ttls];
    }

    private static int PastName(ReadOnlySpan<byte> message, int pos)
    {
        while (message[pos] != 0)
        {
            if ((message[pos] & 0xC0) == 0xC0)
            {
                return pos + 2;
            }

            pos += 1 + message[pos];
        }

        return pos + 1;
    }
}
