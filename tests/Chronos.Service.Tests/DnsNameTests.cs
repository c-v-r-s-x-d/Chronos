using System.Text;
using Chronos.Service.Dns;

namespace Chronos.Service.Tests;

public sealed class DnsNameTests
{
    // The limits spelled out (RFC 1035 2.3.4). Wire buffers below use literal lengths, so a test of
    // the limit is not written in terms of the limit, but nothing else would notice a constant drifting.
    [Fact]
    public void TheLimitsAreTheOnesTheRfcGives()
    {
        Assert.Equal(63, DnsName.MaxLabelLength);
        Assert.Equal(255, DnsName.MaxWireLength);
    }

    [Fact]
    public void TryRead_JoinsLabelsAndLowerCasesThem()
    {
        // 3 www 7 EXAMPLE 3 com 0
        byte[] wire = [3, 119, 119, 119, 7, 69, 88, 65, 77, 80, 76, 69, 3, 99, 111, 109, 0];

        var pos = 0;

        Assert.True(DnsName.TryRead(wire, ref pos, out var name));
        Assert.Equal("www.example.com", name);
        Assert.Equal(wire.Length, pos);
    }

    [Fact]
    public void TryRead_ReadsTheRootAsAnEmptyName()
    {
        var pos = 0;

        Assert.True(DnsName.TryRead([0], ref pos, out var name));
        Assert.Equal(string.Empty, name);
        Assert.Equal(1, pos);
    }

    // A byte no rule can match is kept rather than turned into a question mark or a refusal, since the
    // upstream server may know what to do with it. Carried, not round-tripped: each byte is widened as
    // Latin-1, so 0xC0 reads back as U+00C0. It is a name for matching and logging.
    [Fact]
    public void TryRead_WidensBytesOutsideAsciiAsLatin1()
    {
        byte[] wire = [2, 0xC0, 0xE0, 3, 99, 111, 109, 0];

        var pos = 0;

        Assert.True(DnsName.TryRead(wire, ref pos, out var name));
        Assert.Equal("Àà.com", name);
    }

    // The out parameter is not a partial result: a caller that mishandles false must not hold half a hostile name that looks whole.
    [Fact]
    public void TryRead_LeavesNameEmptyAndPosUntouchedWhenItFails()
    {
        // "com" reads fine, then a label whose bytes are not all there: the walk fails with two labels' worth of characters already collected.
        byte[] wire = [3, 99, 111, 109, 3, 99, 111];

        var pos = 0;

        Assert.False(DnsName.TryRead(wire, ref pos, out var name));
        Assert.Equal(string.Empty, name);
        Assert.Equal(0, pos);
    }

    [Fact]
    public void TryRead_FollowsACompressionPointer()
    {
        // "example.com" at offset 0, then a name that is one label plus a pointer back to it.
        byte[] wire =
        [
            7, 101, 120, 97, 109, 112, 108, 101, 3, 99, 111, 109, 0,
            3, 99, 100, 110, 0xC0, 0x00,
        ];

        var pos = 13;

        Assert.True(DnsName.TryRead(wire, ref pos, out var name));
        Assert.Equal("cdn.example.com", name);
        Assert.Equal(wire.Length, pos); // lands after the pointer, not after the target
    }

    // A pointer to a pointer to labels, ending in success, where the landing place matters. Only the
    // first pointer may set it: landing after the last one would make ParseResponse read QTYPE and
    // QCLASS from somebody else's offset.
    [Fact]
    public void TryRead_LandsAfterTheFirstPointerWhenPointersChain()
    {
        byte[] wire =
        [
            3, 99, 111, 109, 0, // offset 0: "com"
            0xC0, 0x00,         // offset 5: pointer to offset 0
            0xC0, 0x05,         // offset 7: pointer to offset 5
        ];

        var pos = 7;

        Assert.True(DnsName.TryRead(wire, ref pos, out var name));
        Assert.Equal("com", name);
        Assert.Equal(9, pos);
    }

    [Fact]
    public void TryRead_RefusesAPointerThatDoesNotGoBackwards()
    {
        var pos = 0;

        Assert.False(DnsName.TryRead([0xC0, 0x02, 0, 0], ref pos, out _));
    }

    // A message that stops on the first byte of a pointer: the offset it would jump to is half missing.
    [Fact]
    public void TryRead_RefusesAPointerCutOffAfterItsFirstByte()
    {
        var pos = 3;

        Assert.False(DnsName.TryRead([0, 0, 0, 0xC0], ref pos, out _));
    }

    // "Strictly backward" separates a pointer to the byte before it (the tightest legal jump) from a
    // pointer to itself. This does not prove much: with the hop cap, a self-pointer past this guard
    // would spin 128 times and be refused anyway, so only the promptness differs.
    [Fact(Timeout = 5000)]
    public async Task TryRead_TakesAPointerToTheByteBeforeItButNotOneToItself()
    {
        // xUnit's Timeout needs a Task to race; a synchronous walk that looped would never hand control back.
        var oneByteBack = await Task.Run(() =>
        {
            var pos = 1;
            return DnsName.TryRead([0, 0xC0, 0x00], ref pos, out _);
        });

        var toItself = await Task.Run(() =>
        {
            var pos = 0;
            return DnsName.TryRead([0xC0, 0x00, 0, 0], ref pos, out _);
        });

        Assert.True(oneByteBack);
        Assert.False(toItself);
    }

    // The cap counts hops, so the boundary is a count: a chain of exactly 128 resolves.
    [Fact(Timeout = 5000)]
    public async Task TryRead_AcceptsAChainOfExactlyTheHopLimit()
    {
        var wire = PointerChain(128);

        var walk = await Task.Run(() =>
        {
            var pos = wire.Length - 2;
            var ok = DnsName.TryRead(wire, ref pos, out var name);
            return (Ok: ok, Name: name, Pos: pos);
        });

        Assert.True(walk.Ok);
        Assert.Equal(string.Empty, walk.Name); // the chain ends at the root label at offset 0
        Assert.Equal(wire.Length, walk.Pos);
    }

    [Fact(Timeout = 5000)]
    public async Task TryRead_RefusesAChainOneHopOverTheHopLimit()
    {
        var wire = PointerChain(129);

        var read = await Task.Run(() =>
        {
            var pos = wire.Length - 2;
            return DnsName.TryRead(wire, ref pos, out _);
        });

        Assert.False(read);
    }

    // 255 is the whole name as it sits on the wire, the root octet included (RFC 1035 2.3.4). The
    // walk must not leave that octet out and accept 256, which Encode would refuse to produce.
    [Fact]
    public void TryRead_AcceptsANameExactlyAtTheWireLimit()
    {
        var wire = WireName(63, 63, 63, 61); // 4 length octets + 250 label bytes + root = 255

        var pos = 0;

        Assert.Equal(255, wire.Length);
        Assert.True(DnsName.TryRead(wire, ref pos, out _));
        Assert.Equal(wire.Length, pos);
    }

    [Fact]
    public void TryRead_RefusesANameOneByteOverTheWireLimit()
    {
        var wire = WireName(63, 63, 63, 62); // 256

        var pos = 0;

        Assert.Equal(256, wire.Length);
        Assert.False(DnsName.TryRead(wire, ref pos, out _));
    }

    [Fact]
    public void TryRead_RefusesALabelThatRunsPastTheEndOfTheMessage()
    {
        var pos = 0;

        Assert.False(DnsName.TryRead([7, 101, 120, 97], ref pos, out _));
    }

    [Fact]
    public void TryRead_RefusesANameWithNoTerminator()
    {
        var pos = 0;

        Assert.False(DnsName.TryRead([3, 99, 111, 109], ref pos, out _));
    }

    // 0x40 and 0x80 are reserved label types. The buffer is sized so the byte would read fine as a
    // length, leaving the label-type check as the only thing that can refuse it; on a short buffer
    // the walk would refuse for running off the end and prove nothing.
    [Theory]
    [InlineData(0x40)]
    [InlineData(0x80)]
    public void TryRead_RefusesAReservedLabelType(int labelType)
    {
        var wire = new byte[1 + labelType + 1];
        wire[0] = (byte)labelType;
        Array.Fill(wire, (byte)'a', 1, labelType);

        var pos = 0;

        Assert.False(DnsName.TryRead(wire, ref pos, out _));
    }

    // A pointer names a prior occurrence (RFC 1035 4.1.4) and a question is first in a query, so a
    // compressed question name cannot be meant. The codec reports the pointer; refusing it is the caller's job.

    [Fact]
    public void TryRead_ReportsNoCompressionForANameWrittenOutInFull()
    {
        var wire = new byte[] { 3, 119, 119, 119, 7, 101, 120, 97, 109, 112, 108, 101, 0 };

        var pos = 0;
        Assert.True(DnsName.TryRead(wire, ref pos, out _, out var compressed));
        Assert.False(compressed);
    }

    [Fact]
    public void TryRead_ReportsCompressionForANameThatEndsInAPointer()
    {
        // "example.com" at offset 0, then "cdn" plus a pointer back to it.
        var wire = new byte[]
        {
            7, 101, 120, 97, 109, 112, 108, 101, 3, 99, 111, 109, 0, 3, 99, 100, 110, 0xC0, 0x00,
        };

        var pos = 13;
        Assert.True(DnsName.TryRead(wire, ref pos, out var name, out var compressed));
        Assert.Equal("cdn.example.com", name);
        Assert.True(compressed);
    }

    [Fact]
    public void TryRead_ReportsCompressionForANameThatIsNothingButAPointer()
    {
        var wire = new byte[] { 3, 99, 111, 109, 0, 0xC0, 0x00 };

        var pos = 5;
        Assert.True(DnsName.TryRead(wire, ref pos, out var name, out var compressed));
        Assert.Equal("com", name);
        Assert.True(compressed);
    }

    public static TheoryData<byte[], int> Names => new()
    {
        { [0], 0 },
        { [3, 119, 119, 119, 7, 69, 88, 65, 77, 80, 76, 69, 3, 99, 111, 109, 0], 0 },
        {
            [7, 101, 120, 97, 109, 112, 108, 101, 3, 99, 111, 109, 0, 3, 99, 100, 110, 0xC0, 0x00],
            13
        },
        { [0xC0, 0x02, 0, 0], 0 },       // forward pointer
        { [7, 101, 120, 97], 0 },        // label past the end
        { [0x40, 99, 111, 109, 0], 0 },  // reserved label type
    };

    // Both wrappers drive the same walk, so this is narrow: asking for the labels changes neither the
    // verdict nor the landing place. The tests above cover the walk itself. TrySkip is TryRead minus the string.
    [Theory]
    [MemberData(nameof(Names))]
    public void TrySkip_LandsWhereTryReadLands(byte[] wire, int start)
    {
        var skipPos = start;
        var readPos = start;

        var skipped = DnsName.TrySkip(wire, ref skipPos);
        var read = DnsName.TryRead(wire, ref readPos, out _);

        Assert.Equal(read, skipped);
        Assert.Equal(readPos, skipPos);
    }

    [Fact]
    public void Encode_WritesLengthPrefixedLabelsAndARootTerminator()
    {
        byte[] expected = [7, 101, 120, 97, 109, 112, 108, 101, 3, 99, 111, 109, 0];

        Assert.Equal(expected, DnsName.Encode("example.com"));
    }

    [Fact]
    public void Encode_TreatsATrailingDotAsTheRootItAlreadyWrites()
    {
        Assert.Equal(DnsName.Encode("example.com"), DnsName.Encode("example.com."));
    }

    [Fact]
    public void Encode_PutsANonAsciiNameOnTheWireAsPunycode()
    {
        var encoded = DnsName.Encode("вконтакте.рф");

        Assert.Equal("xn--80adksbqg7ac.xn--p1ai", LabelsOf(encoded));
    }

    [Fact]
    public void Encode_RejectsANameThatCannotBeConvertedToPunycode()
    {
        var exception = Assert.Throws<ArgumentException>(() => DnsName.Encode("😀\0.example"));

        // The type alone does not show the rewrap (punycode throws that type too); the rewrap adds the parameter name and the underlying failure.
        Assert.Equal("name", exception.ParamName);
        Assert.NotNull(exception.InnerException);
    }

    // IdnMapping refuses these two before Encode's own length checks are reached, which are pinned
    // directly further down. Here the point is Encode's contract: nonsense in the block list costs
    // that domain by exception rather than going out mangled.
    [Fact]
    public void Encode_RejectsALabelLongerThanTheLabelLimit()
    {
        var name = new string('a', DnsName.MaxLabelLength + 1) + ".com";

        Assert.Throws<ArgumentException>(() => DnsName.Encode(name));
    }

    [Fact]
    public void Encode_RejectsAnEmptyLabel()
    {
        Assert.Throws<ArgumentException>(() => DnsName.Encode("www..com"));
    }

    // The other side of the boundary TryRead draws: 255 on the wire is a name this produces, 256 is not.
    [Fact]
    public void Encode_DrawsTheWireLimitWhereTryReadDrawsIt()
    {
        var encoded = DnsName.Encode(TextName(63, 63, 63, 61));

        Assert.Equal(255, encoded.Length);
        Assert.Equal(WireName(63, 63, 63, 61), encoded);
        Assert.Throws<ArgumentException>(() => DnsName.Encode(TextName(63, 63, 63, 62)));
    }

    [Fact]
    public void Encode_ProducesANameTryReadReadsBack()
    {
        var wire = DnsName.Encode("CDN.Example.COM");

        var pos = 0;

        Assert.True(DnsName.TryRead(wire, ref pos, out var name));
        Assert.Equal("cdn.example.com", name);
        Assert.Equal(wire.Length, pos);
    }

    // Called directly, not through Encode: inside Encode, punycode conversion rejects an empty or
    // over-long label first, so these checks would go untested while looking tested.

    [Fact]
    public void ValidateWireLength_CountsTheRootTerminator()
    {
        Assert.Equal(13, DnsName.ValidateWireLength("example.com", "name")); // (1+7) + (1+3) + 1
    }

    [Fact]
    public void ValidateWireLength_AcceptsANameExactlyAtTheWireLimit()
    {
        Assert.Equal(255, DnsName.ValidateWireLength(TextName(63, 63, 63, 61), "name"));
    }

    [Fact]
    public void ValidateWireLength_RejectsANameOneByteOverTheWireLimit()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => DnsName.ValidateWireLength(TextName(63, 63, 63, 62), "name"));

        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void ValidateWireLength_AcceptsALabelExactlyAtTheLabelLimit()
    {
        Assert.Equal(65, DnsName.ValidateWireLength(TextName(63), "name")); // 1 + 63 + root
    }

    [Theory]
    [InlineData(64)]  // one over the label limit
    [InlineData(200)]
    public void ValidateWireLength_RejectsALabelOverTheLabelLimit(int labelLength)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => DnsName.ValidateWireLength(TextName(labelLength, 3), "name"));

        Assert.Equal("name", exception.ParamName);
    }

    [Theory]
    [InlineData("www..com")]  // an empty label in the middle
    [InlineData(".com")]      // an empty label at the front
    [InlineData("www.com.")]  // an empty label at the end: Encode trims the dot, this does not
    [InlineData("")]          // no labels at all
    public void ValidateWireLength_RejectsAnEmptyLabel(string ascii)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => DnsName.ValidateWireLength(ascii, "name"));

        Assert.Equal("name", exception.ParamName);
    }

    // A chain of `hops` compression pointers ending at the root label at offset 0. Pointer i sits at
    // offset 1 + 2i and aims at the one before it, so only their number can be the problem.
    private static byte[] PointerChain(int hops)
    {
        var wire = new byte[1 + (2 * hops)];

        for (var i = 0; i < hops; i++)
        {
            var at = 1 + (2 * i);
            var target = i == 0 ? 0 : at - 2;
            wire[at] = (byte)(0xC0 | (target >> 8));
            wire[at + 1] = (byte)target;
        }

        return wire;
    }

    // A name of all-'a' labels of the given lengths, on the wire and as text.
    private static byte[] WireName(params int[] labelLengths)
    {
        var wire = new List<byte>();

        foreach (var length in labelLengths)
        {
            wire.Add((byte)length);
            wire.AddRange(Enumerable.Repeat((byte)'a', length));
        }

        wire.Add(0); // root
        return wire.ToArray();
    }

    private static string TextName(params int[] labelLengths) =>
        string.Join('.', labelLengths.Select(length => new string('a', length)));

    private static string LabelsOf(ReadOnlySpan<byte> wire)
    {
        var labels = new StringBuilder();

        for (var pos = 0; pos < wire.Length && wire[pos] != 0; pos += 1 + wire[pos])
        {
            if (labels.Length > 0)
            {
                labels.Append('.');
            }

            labels.Append(Encoding.ASCII.GetString(wire.Slice(pos + 1, wire[pos])));
        }

        return labels.ToString();
    }
}
