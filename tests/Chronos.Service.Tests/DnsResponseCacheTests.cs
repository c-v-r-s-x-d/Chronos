using System.Buffers.Binary;
using Chronos.Service.Dns;
using static Chronos.Service.Tests.DnsTtlTests;

namespace Chronos.Service.Tests;

public sealed class DnsResponseCacheTests
{
    private const ushort TypeAaaa = 28;
    private const ushort ClassChaos = 3;

    private readonly ServiceTestClock _clock = new(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void TheLimitsAreTheOnesThePlanGives()
    {
        Assert.Equal(4096, DnsResponseCache.DefaultCapacity);
        Assert.Equal(TimeSpan.FromSeconds(5), DnsResponseCache.MinimumTtl);
        Assert.Equal(TimeSpan.FromHours(1), DnsResponseCache.MaximumTtl);
    }

    [Fact]
    public void ACapacityOfNoAnswersAtAllIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DnsResponseCache(_clock, capacity: 0));
    }

    [Fact]
    public void AClockIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => new DnsResponseCache(null!));
    }

    [Fact]
    public void ANameIsRequiredOfBothSides()
    {
        var cache = new DnsResponseCache(_clock);

        Assert.Throws<ArgumentNullException>(() => cache.Take(null!, TypeA, ClassInternet, 1));
        Assert.Throws<ArgumentNullException>(() => cache.Store(null!, TypeA, ClassInternet, Response()));
    }

    [Fact]
    public void ACapacityOfOneIsAllowed()
    {
        var cache = new DnsResponseCache(_clock, capacity: 1);
        cache.Store("a.example", TypeA, ClassInternet, Response([("a.example", 600u)]));

        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Take_IsNullWhenNothingWasEverStored()
    {
        Assert.Null(new DnsResponseCache(_clock).Take("example.com", TypeA, ClassInternet, 1));
    }

    [Fact]
    public void Take_ReturnsTheStoredAnswerWithTheAskersOwnId()
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 300u)], id: 0x1111));

        var served = cache.Take("example.com", TypeA, ClassInternet, id: 0x2222);

        Assert.NotNull(served);
        Assert.Equal(0x2222, BinaryPrimitives.ReadUInt16BigEndian(served.AsSpan(0, 2)));
    }

    [Fact]
    public void Take_ChangesNothingInTheAnswerButTheIdAndTheTtls()
    {
        var stored = Response([("example.com", 300u)], id: 0x1111);
        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, stored);

        var served = cache.Take("example.com", TypeA, ClassInternet, 0x1111)!;

        Assert.Equal(stored, served);
    }

    // The byte written into is the last of the record data, not the first of the identifier: the
    // second Take writes its own identifier over that one, so a cache handing out its own array would pass on byte zero.
    [Fact]
    public void Take_DoesNotHandOutTheCopyItKeeps()
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 300u)]));

        var first = cache.Take("example.com", TypeA, ClassInternet, 1)!;
        first[^1] = 0xFF;

        var second = cache.Take("example.com", TypeA, ClassInternet, 2)!;
        Assert.NotEqual(0xFF, second[^1]);
    }

    // The caller's buffer is the one the datagram was read into and will be read into again.
    [Fact]
    public void Store_DoesNotKeepTheCallersBuffer()
    {
        var upstream = Response([("example.com", 300u)]);
        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, upstream);

        Array.Clear(upstream);

        Assert.Equal<uint>([300], TtlsOf(cache.Take("example.com", TypeA, ClassInternet, 1)!));
    }

    [Fact]
    public void Take_LeavesTheAnswerForTheNextAskerToo()
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 300u)]));

        Assert.NotNull(cache.Take("example.com", TypeA, ClassInternet, 1));
        Assert.NotNull(cache.Take("example.com", TypeA, ClassInternet, 2));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Take_AgesTheAnswerByHowLongItWasHeld()
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 300u)]));

        _clock.Advance(TimeSpan.FromSeconds(45));

        Assert.Equal<uint>([255], TtlsOf(cache.Take("example.com", TypeA, ClassInternet, 1)!));
    }

    [Fact]
    public void Take_AgesAnAnswerHandedBackAtOnceByNothing()
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 300u)]));

        Assert.Equal<uint>([300], TtlsOf(cache.Take("example.com", TypeA, ClassInternet, 1)!));
    }

    // Ageing is measured from arrival, not from when the answer was last handed out, or a popular name would keep its TTL.
    [Fact]
    public void Take_AgesFromTheAnswersArrivalAndNotFromTheLastHit()
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 300u)]));

        _clock.Advance(TimeSpan.FromSeconds(10));
        cache.Take("example.com", TypeA, ClassInternet, 1);
        _clock.Advance(TimeSpan.FromSeconds(20));

        Assert.Equal<uint>([270], TtlsOf(cache.Take("example.com", TypeA, ClassInternet, 2)!));
    }

    // A wall clock can step backwards, and an answer arriving "in the future" has been held for no time, not a negative amount.
    [Fact]
    public void Take_AgesByNothingWhenTheClockHasGoneBackwards()
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 300u)]));

        _clock.Advance(TimeSpan.FromSeconds(-30));

        Assert.Equal<uint>([300], TtlsOf(cache.Take("example.com", TypeA, ClassInternet, 1)!));
    }

    // Both sides of the moment the answer stops being true: served for its last second, not the next.
    [Theory]
    [InlineData(59, true)]
    [InlineData(60, false)]
    public void Take_ServesAnAnswerUntilItsSmallestTtlHasPassed(int held, bool served)
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 60u)]));

        _clock.Advance(TimeSpan.FromSeconds(held));

        Assert.Equal(served, cache.Take("example.com", TypeA, ClassInternet, 1) is not null);
    }

    [Fact]
    public void Take_ForgetsAnAnswerThatHasExpired()
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 60u)]));

        _clock.Advance(TimeSpan.FromSeconds(61));
        cache.Take("example.com", TypeA, ClassInternet, 1);

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Take_TellsRecordTypesApart()
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 300u)]));

        Assert.Null(cache.Take("example.com", TypeAaaa, ClassInternet, 1));
    }

    [Fact]
    public void Take_TellsRecordClassesApart()
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 300u)]));

        Assert.Null(cache.Take("example.com", TypeA, ClassChaos, 1));
    }

    [Fact]
    public void Take_TellsNamesApart()
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("a.example", TypeA, ClassInternet, Response([("a.example", 300u)]));

        Assert.Null(cache.Take("b.example", TypeA, ClassInternet, 1));
    }

    // The parser lower-cases every name, so a name with a capital did not come from there. Matching it
    // Ordinal is a miss, which costs a lookup and answers nothing wrong.
    [Fact]
    public void Take_MatchesTheNameByOrdinal()
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 300u)]));

        Assert.Null(cache.Take("Example.com", TypeA, ClassInternet, 1));
    }

    [Fact]
    public void Store_KeepsNothingWithoutRecordsToExpire()
    {
        var cache = new DnsResponseCache(_clock);

        cache.Store("example.com", TypeA, ClassInternet, Response());

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Store_KeepsNothingItCannotWalk()
    {
        var response = Response([("example.com", 300u)]);
        var cache = new DnsResponseCache(_clock);

        cache.Store("example.com", TypeA, ClassInternet, response.AsSpan(0, response.Length - 3));

        Assert.Equal(0, cache.Count);
    }

    // Two guards. Up to three octets there is no response code to read and only Store's own length
    // check stands before the end of the array; from four up the walk finds no records. Offering only
    // eleven octets proves the second guard, not the first.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(DnsQuery.HeaderLength - 1)]
    public void Store_KeepsNothingTooShortToBeAResponse(int length)
    {
        var cache = new DnsResponseCache(_clock);

        cache.Store("example.com", TypeA, ClassInternet, new byte[length]);

        Assert.Equal(0, cache.Count);
    }

    // RFC 1035 4.1.3: a TTL of zero means the record is not to be cached; holding it for the floor would cache what it refused.
    [Fact]
    public void Store_KeepsNothingWhoseTtlIsZero()
    {
        var cache = new DnsResponseCache(_clock);

        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 0u)]));

        Assert.Equal(0, cache.Count);
    }

    // Only NOERROR. A blocked name is answered from the plan and never gets here, and an upstream failure is not worth repeating for an hour.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(5, 0)]
    public void Store_KeepsOnlyAnAnswerThatSucceeded(byte rcode, int kept)
    {
        var cache = new DnsResponseCache(_clock);

        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 300u)], rcode: rcode));

        Assert.Equal(kept, cache.Count);
    }

    // The other eight bits of the code. BADVERS is 16: all of it sits in the OPT record's EXTENDED-RCODE
    // (RFC 6891 6.1.3) and the header's four bits read as success.
    [Theory]
    [InlineData(0x00000000u, 1)] // no extended code: the header's NOERROR is the whole of it
    [InlineData(0x00008000u, 1)] // VERSION and DO, which are in the same four octets and are not it
    [InlineData(0x01000000u, 0)] // BADVERS, code 16
    [InlineData(0xFF000000u, 0)] // the top of the range those eight bits can hold
    public void Store_KeepsOnlyAnAnswerWhoseExtendedCodeSucceededToo(uint optFlags, int kept)
    {
        var cache = new DnsResponseCache(_clock);

        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 300u)], optFlags: optFlags));

        Assert.Equal(kept, cache.Count);
    }

    // TC says the responder cut the message down (RFC 1035 4.1.1), so its record sections are not all
    // the records. Kept, that half would be served as the whole for up to an hour, to TCP askers too.
    [Fact]
    public void Store_KeepsNothingThatArrivedTruncated()
    {
        var response = Response([("example.com", 300u)]);
        response[2] |= 0x02; // TC, in the first flag octet

        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, response);

        Assert.Equal(0, cache.Count);
    }

    // A one-second TTL would mean a lookup per client for a name that changes daily; the floor makes
    // the cache worth having and holds the answer past the record's own second.
    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void Store_HoldsAShortTtlForTheFloor(int held, bool served)
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("short.example", TypeA, ClassInternet, Response([("short.example", 1u)]));

        _clock.Advance(TimeSpan.FromSeconds(held));

        Assert.Equal(served, cache.Take("short.example", TypeA, ClassInternet, 1) is not null);
    }

    // A day-long upstream TTL is a day this resolver cannot be corrected in.
    [Theory]
    [InlineData(3599, true)]
    [InlineData(3600, false)]
    public void Store_HoldsALongTtlOnlyForTheCeiling(int held, bool served)
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("long.example", TypeA, ClassInternet, Response([("long.example", 86400u)]));

        _clock.Advance(TimeSpan.FromSeconds(held));

        Assert.Equal(served, cache.Take("long.example", TypeA, ClassInternet, 1) is not null);
    }

    // Between the two limits the record's own TTL counts; neither is applied.
    [Theory]
    [InlineData(299, true)]
    [InlineData(300, false)]
    public void Store_HoldsATtlBetweenTheLimitsForExactlyWhatItSays(int held, bool served)
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 300u)]));

        _clock.Advance(TimeSpan.FromSeconds(held));

        Assert.Equal(served, cache.Take("example.com", TypeA, ClassInternet, 1) is not null);
    }

    [Fact]
    public void Store_FillsToCapacityBeforeDroppingAnything()
    {
        var cache = new DnsResponseCache(_clock, capacity: 2);
        cache.Store("a.example", TypeA, ClassInternet, Response([("a.example", 600u)]));
        cache.Store("b.example", TypeA, ClassInternet, Response([("b.example", 600u)]));

        Assert.Equal(2, cache.Count);
        Assert.NotNull(cache.Take("a.example", TypeA, ClassInternet, 1));
        Assert.NotNull(cache.Take("b.example", TypeA, ClassInternet, 1));
    }

    [Fact]
    public void Store_DropsTheSoonestToExpireWhenItIsFull()
    {
        var cache = new DnsResponseCache(_clock, capacity: 2);
        cache.Store("a.example", TypeA, ClassInternet, Response([("a.example", 60u)]));
        cache.Store("b.example", TypeA, ClassInternet, Response([("b.example", 600u)]));
        cache.Store("c.example", TypeA, ClassInternet, Response([("c.example", 600u)]));

        Assert.Equal(2, cache.Count);
        Assert.Null(cache.Take("a.example", TypeA, ClassInternet, 1));
        Assert.NotNull(cache.Take("b.example", TypeA, ClassInternet, 1));
        Assert.NotNull(cache.Take("c.example", TypeA, ClassInternet, 1));
    }

    // Replacing an answer takes no room, so nothing else has to go to make room for it.
    [Fact]
    public void Store_DropsNothingToReplaceAnAnswerItAlreadyHolds()
    {
        var cache = new DnsResponseCache(_clock, capacity: 2);
        cache.Store("a.example", TypeA, ClassInternet, Response([("a.example", 600u)]));
        cache.Store("b.example", TypeA, ClassInternet, Response([("b.example", 60u)]));

        cache.Store("a.example", TypeA, ClassInternet, Response([("a.example", 600u)]));

        Assert.Equal(2, cache.Count);
        Assert.NotNull(cache.Take("b.example", TypeA, ClassInternet, 1));
    }

    // A fresher answer replaces the one held and is held from when it arrived; keeping the older arrival time would age it and expire it early.
    [Fact]
    public void Store_HoldsAReplacementFromWhenItArrived()
    {
        var cache = new DnsResponseCache(_clock);
        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 300u)]));

        _clock.Advance(TimeSpan.FromSeconds(100));
        cache.Store("example.com", TypeA, ClassInternet, Response([("example.com", 300u)]));

        Assert.Equal(1, cache.Count);
        Assert.Equal<uint>([300], TtlsOf(cache.Take("example.com", TypeA, ClassInternet, 1)!));
    }
}
