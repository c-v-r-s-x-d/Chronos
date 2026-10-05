using System.Net;
using Chronos.Service.Wfp;

namespace Chronos.Service.Tests;

public sealed class DnsMessageTests
{

    [Fact]
    public void BuildQuery_EncodesHeaderAndQuestion()
    {
        byte[] expected =
        [
            0x12, 0x34, // ID
            0x01, 0x00, // Flags: RD set, everything else clear
            0x00, 0x01, // QDCOUNT
            0x00, 0x00, // ANCOUNT
            0x00, 0x00, // NSCOUNT
            0x00, 0x00, // ARCOUNT
            0x07, 0x65, 0x78, 0x61, 0x6d, 0x70, 0x6c, 0x65, // "example"
            0x03, 0x63, 0x6f, 0x6d, // "com"
            0x00, // root
            0x00, 0x01, // QTYPE A
            0x00, 0x01, // QCLASS IN
        ];

        var actual = DnsMessage.BuildQuery("example.com", DnsRecordType.A, 0x1234);

        Assert.Equal(29, actual.Length);
        Assert.Equal(expected, actual);
    }

    // Encoding.ASCII turned a Cyrillic name into question marks: no exception, and a query no server
    // can answer. Non-ASCII names are ordinary in this product; they go on the wire as punycode or not at all.
    [Fact]
    public void BuildQuery_EncodesANonAsciiNameAsPunycode()
    {
        var actual = DnsMessage.BuildQuery("вконтакте.рф", DnsRecordType.A, 1);
        var expected = DnsMessage.BuildQuery("xn--80adksbqg7ac.xn--p1ai", DnsRecordType.A, 1);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain((byte)'?', actual);
    }

    // Rejected rather than mangled: a query built from nonsense cannot be answered, and sending it silently loses the domain with no way to say why.
    [Fact]
    public void BuildQuery_RejectsANameThatCannotBeConvertedToPunycode()
    {
        Assert.Throws<ArgumentException>(
            () => DnsMessage.BuildQuery("😀\0.example", DnsRecordType.A, 1));
    }

    [Fact]
    public void BuildQuery_RejectsLabelLongerThan63Bytes()
    {
        var name = new string('a', 64) + ".com";

        Assert.Throws<ArgumentException>(() => DnsMessage.BuildQuery(name, DnsRecordType.A, 1));
    }

    [Fact]
    public void BuildQuery_RejectsNameWhoseWireEncodingExceeds255Bytes()
    {
        // Four 63-byte labels: wire length (1 + 63) * 4 + 1 (root) = 257 > 255.
        var label = new string('a', 63);
        var name = string.Join(".", Enumerable.Repeat(label, 4));

        Assert.Throws<ArgumentException>(() => DnsMessage.BuildQuery(name, DnsRecordType.A, 1));
    }

    [Fact]
    public void ParseResponse_TwoARecords_GivesBothAddressesInOrder()
    {
        byte[] response =
        [
            0x12, 0x34, 0x81, 0x80, // ID, flags: QR+RD+RA, RCODE 0
            0x00, 0x00, // QDCOUNT
            0x00, 0x02, // ANCOUNT
            0x00, 0x00, 0x00, 0x00, // NSCOUNT, ARCOUNT

            // Answer 1: root name, A, IN, TTL, RDLENGTH 4, 1.2.3.4
            0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3c, 0x00, 0x04, 1, 2, 3, 4,

            // Answer 2: root name, A, IN, TTL, RDLENGTH 4, 5.6.7.8
            0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3c, 0x00, 0x04, 5, 6, 7, 8,
        ];

        var result = DnsMessage.ParseResponse(response, 0x1234);

        Assert.Equal(DnsParseStatus.Ok, result.Status);
        Assert.Equal([IPAddress.Parse("1.2.3.4"), IPAddress.Parse("5.6.7.8")], result.Addresses);
    }

    [Fact]
    public void ParseResponse_AaaaRecord_GivesSixteenByteAddress()
    {
        byte[] response =
        [
            0x00, 0x01, 0x81, 0x80,
            0x00, 0x00,
            0x00, 0x01,
            0x00, 0x00, 0x00, 0x00,

            // Answer: root name, AAAA (28), IN, TTL, RDLENGTH 16, 2001:db8::1
            0x00, 0x00, 0x1c, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3c, 0x00, 0x10,
            0x20, 0x01, 0x0d, 0xb8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
        ];

        var result = DnsMessage.ParseResponse(response, 0x0001);

        Assert.Equal(DnsParseStatus.Ok, result.Status);
        Assert.Equal([IPAddress.Parse("2001:db8::1")], result.Addresses);
    }

    [Fact]
    public void ParseResponse_CompressedOwnerName_ParsesCorrectly()
    {
        byte[] response =
        [
            0x00, 0x02, 0x81, 0x80,
            0x00, 0x01, // QDCOUNT
            0x00, 0x01, // ANCOUNT
            0x00, 0x00, 0x00, 0x00,

            // Question, offset 12: "example.com" A IN
            0x07, 0x65, 0x78, 0x61, 0x6d, 0x70, 0x6c, 0x65,
            0x03, 0x63, 0x6f, 0x6d, 0x00,
            0x00, 0x01, 0x00, 0x01,

            // Answer, offset 29: NAME = pointer to offset 12, A, IN, TTL, RDLENGTH 4, 9.9.9.9
            0xc0, 0x0c, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3c, 0x00, 0x04, 9, 9, 9, 9,
        ];

        var result = DnsMessage.ParseResponse(response, 0x0002);

        Assert.Equal(DnsParseStatus.Ok, result.Status);
        Assert.Equal([IPAddress.Parse("9.9.9.9")], result.Addresses);
    }

    [Fact]
    public void ParseResponse_CnameThenA_SkipsCnameAndGivesOneAddress()
    {
        byte[] response =
        [
            0x00, 0x03, 0x81, 0x80,
            0x00, 0x00,
            0x00, 0x02, // ANCOUNT
            0x00, 0x00, 0x00, 0x00,

            // Answer 1: root name, CNAME (5), IN, TTL, RDLENGTH 4, arbitrary payload (skipped raw)
            0x00, 0x00, 0x05, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3c, 0x00, 0x04, 0xaa, 0xbb, 0xcc, 0xdd,

            // Answer 2: root name, A, IN, TTL, RDLENGTH 4, 10.0.0.1
            0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3c, 0x00, 0x04, 10, 0, 0, 1,
        ];

        var result = DnsMessage.ParseResponse(response, 0x0003);

        Assert.Equal(DnsParseStatus.Ok, result.Status);
        Assert.Equal([IPAddress.Parse("10.0.0.1")], result.Addresses);
    }

    [Fact]
    public void ParseResponse_IdMismatch_GivesIdMismatchAndNoAddresses()
    {
        byte[] response =
        [
            0x00, 0x04, 0x81, 0x80,
            0x00, 0x00,
            0x00, 0x01,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3c, 0x00, 0x04, 1, 1, 1, 1,
        ];

        var result = DnsMessage.ParseResponse(response, 0x00ff);

        Assert.Equal(DnsParseStatus.IdMismatch, result.Status);
        Assert.Empty(result.Addresses);
    }

    [Fact]
    public void ParseResponse_Rcode3_GivesNameNotFoundWithoutThrowing()
    {
        byte[] response =
        [
            0x00, 0x05, 0x81, 0x83, // RCODE 3 (NXDOMAIN)
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];

        var result = DnsMessage.ParseResponse(response, 0x0005);

        Assert.Equal(DnsParseStatus.NameNotFound, result.Status);
        Assert.Empty(result.Addresses);
    }

    [Fact]
    public void ParseResponse_Rcode2_GivesServerFailure()
    {
        byte[] response =
        [
            0x00, 0x06, 0x81, 0x82, // RCODE 2 (SERVFAIL)
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];

        var result = DnsMessage.ParseResponse(response, 0x0006);

        Assert.Equal(DnsParseStatus.ServerFailure, result.Status);
    }

    [Fact(Timeout = 5000)]
    public async Task ParseResponse_SelfReferencingPointer_GivesMalformedInsteadOfLooping()
    {
        byte[] response =
        [
            0x00, 0x07, 0x81, 0x80,
            0x00, 0x00,
            0x00, 0x01, // ANCOUNT
            0x00, 0x00, 0x00, 0x00,

            // Answer at offset 12: NAME = pointer to offset 12 (itself), never terminates naively.
            0xc0, 0x0c, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3c, 0x00, 0x04, 1, 2, 3, 4,
        ];

        // xUnit's Timeout only applies to async tests (it needs a Task to race), so a synchronous hang in ParseResponse would never yield to it.
        var result = await Task.Run(() => DnsMessage.ParseResponse(response, 0x0007));

        Assert.Equal(DnsParseStatus.Malformed, result.Status);
    }

    // The codec promises never to throw on malformed or hostile input, since a datagram off the wire
    // reaches it directly. A message that stops on the first byte of a compression pointer is one
    // byte from being read past its end.
    [Fact]
    public void ParseResponse_TruncatedInsideACompressionPointer_GivesMalformed()
    {
        byte[] response =
        [
            0x00, 0x0e, 0x81, 0x80,
            0x00, 0x01, // QDCOUNT
            0x00, 0x00, // ANCOUNT
            0x00, 0x00, 0x00, 0x00,

            0xc0, // question name: the first byte of a pointer, and then the message ends
        ];

        var result = DnsMessage.ParseResponse(response, 0x000e);

        Assert.Equal(DnsParseStatus.Malformed, result.Status);
    }

    // A message whose last four bytes are the question's QTYPE and QCLASS is legal and complete: the
    // bound is "four bytes must still fit", not "with room to spare". An unanswered query's response looks like this.
    [Fact]
    public void ParseResponse_QuestionEndingFlushWithTheMessage_IsNotMalformed()
    {
        byte[] response =
        [
            0x00, 0x0f, 0x81, 0x80,
            0x00, 0x01, // QDCOUNT
            0x00, 0x00, // ANCOUNT
            0x00, 0x00, 0x00, 0x00,

            0x00,       // question name: root
            0x00, 0x01, // QTYPE A
            0x00, 0x01, // QCLASS IN
        ];

        var result = DnsMessage.ParseResponse(response, 0x000f);

        Assert.Equal(DnsParseStatus.Ok, result.Status);
        Assert.Empty(result.Addresses);
    }

    [Fact]
    public void ParseResponse_RdlengthPastEndOfBuffer_GivesMalformed()
    {
        byte[] response =
        [
            0x00, 0x08, 0x81, 0x80,
            0x00, 0x00,
            0x00, 0x01,
            0x00, 0x00, 0x00, 0x00,

            // Answer: root name, A, IN, TTL, RDLENGTH claims 4 bytes but only 2 remain.
            0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3c, 0x00, 0x04, 1, 2,
        ];

        var result = DnsMessage.ParseResponse(response, 0x0008);

        Assert.Equal(DnsParseStatus.Malformed, result.Status);
    }

    [Fact]
    public void ParseResponse_ARecordWithWrongRdlength_GivesMalformed()
    {
        byte[] response =
        [
            0x00, 0x0c, 0x81, 0x80,
            0x00, 0x00,
            0x00, 0x01,
            0x00, 0x00, 0x00, 0x00,

            // Answer: root name, A, IN, TTL, RDLENGTH 0 - RDATA for A is always 4 octets (RFC 1035).
            0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3c, 0x00, 0x00,
        ];

        var result = DnsMessage.ParseResponse(response, 0x000c);

        Assert.Equal(DnsParseStatus.Malformed, result.Status);
        Assert.Empty(result.Addresses);
    }

    [Fact]
    public void ParseResponse_AaaaRecordWithWrongRdlength_GivesMalformed()
    {
        byte[] response =
        [
            0x00, 0x0d, 0x81, 0x80,
            0x00, 0x00,
            0x00, 0x01,
            0x00, 0x00, 0x00, 0x00,

            // Answer: root name, AAAA, IN, TTL, RDLENGTH 4 - RDATA for AAAA is always 16 octets (RFC 3596).
            0x00, 0x00, 0x1c, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3c, 0x00, 0x04, 1, 2, 3, 4,
        ];

        var result = DnsMessage.ParseResponse(response, 0x000d);

        Assert.Equal(DnsParseStatus.Malformed, result.Status);
        Assert.Empty(result.Addresses);
    }

    [Fact]
    public void ParseResponse_ShorterThanHeader_GivesMalformed()
    {
        byte[] response = [0x00, 0x09, 0x81, 0x80, 0x00];

        var result = DnsMessage.ParseResponse(response, 0x0009);

        Assert.Equal(DnsParseStatus.Malformed, result.Status);
    }

    [Fact]
    public void ParseResponse_TruncatedFlagSet_GivesTruncatedTrue()
    {
        byte[] response =
        [
            0x00, 0x0a, 0x82, 0x00, // QR set, TC set (0x02, first flags byte), RCODE 0
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];

        var result = DnsMessage.ParseResponse(response, 0x000a);

        Assert.True(result.Truncated);
    }

    [Fact]
    public void ParseResponse_QrNotSet_GivesNotAnAnswer()
    {
        byte[] response =
        [
            0x00, 0x0b, 0x01, 0x00, // QR clear, RD set
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];

        var result = DnsMessage.ParseResponse(response, 0x000b);

        Assert.Equal(DnsParseStatus.NotAnAnswer, result.Status);
    }
}
