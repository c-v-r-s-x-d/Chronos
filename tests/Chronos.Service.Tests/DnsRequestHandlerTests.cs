using System.Buffers.Binary;
using System.Text;
using Chronos.Core.Rules;
using Chronos.Service.Dns;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Tests;

public sealed class DnsRequestHandlerTests
{
    private const string Blocked = "example.com";
    private const string Allowed = "example.org";

    // The name a compression pointer into the header happens to spell out; see PointerQuestionAnswer.
    private const string PointedAtName = "p";

    private const ushort TypeA = 1;
    private const ushort TypeAaaa = 28;
    private const ushort ClassInternet = 1;
    private const ushort ClassChaos = 3;
    private const ushort OptType = 41;

    private const byte RecursionDesired = 0x01;
    private const byte Truncated = 0x02;
    private const byte Qr = 0x80;

    private const byte RcodeNoError = 0;
    private const byte RcodeFormatError = 1;
    private const byte RcodeServerFailure = 2;
    private const byte RcodeNameError = 3;
    private const byte RcodeNotImplemented = 4;

    private readonly ServiceTestClock _clock = new(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly CapturingLogger<DnsRequestHandler> _log = new();
    private readonly FakeForwarder _forwarder = new();
    private readonly List<string> _notified = [];

    private DnsResponseCache _cache = null!;

    [Fact]
    public void AForwarderIsRequired()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DnsRequestHandler(null!, Cache(), _notified.Add, _log));
    }

    [Fact]
    public void ACacheIsRequired()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DnsRequestHandler(_forwarder, null!, _notified.Add, _log));
    }

    [Fact]
    public void SomebodyToTellAboutABlockedAttemptIsRequired()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DnsRequestHandler(_forwarder, Cache(), null!, _log));
    }

    [Fact]
    public void ALoggerIsRequired()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DnsRequestHandler(_forwarder, Cache(), _notified.Add, null!));
    }

    [Fact]
    public void UseRules_RefusesANullPlan()
    {
        Assert.Throws<ArgumentNullException>(() => Make().UseRules(null!));
    }

    [Fact]
    public async Task AnswerAsync_RefusesANullQuery()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Make().AnswerAsync(null!, DnsTransportKind.Udp, CancellationToken.None));
    }

    [Fact]
    public async Task AnswerAsync_ReturnsNxdomainForADomainInThePlan()
    {
        var handler = Make([new SiteRule(Blocked, includeSubdomains: false)]);

        var reply = await handler.AnswerAsync(Query(Blocked), DnsTransportKind.Udp, default);

        Assert.Equal(RcodeNameError, Rcode(reply!));
        Assert.Empty(_forwarder.Sent);
    }

    [Fact]
    public async Task AnswerAsync_ReturnsNxdomainForASubdomainWhenTheRuleSaysSo()
    {
        var handler = Make([new SiteRule(Blocked, includeSubdomains: true)]);

        var reply = await handler.AnswerAsync(Query("cdn.media." + Blocked), DnsTransportKind.Udp, default);

        Assert.Equal(RcodeNameError, Rcode(reply!));
        Assert.Empty(_forwarder.Sent);
    }

    [Fact]
    public async Task AnswerAsync_ForwardsASubdomainWhenTheRuleDoesNotIncludeThem()
    {
        _forwarder.Answer = Answer("cdn." + Blocked);
        var handler = Make([new SiteRule(Blocked, includeSubdomains: false)]);

        var reply = await handler.AnswerAsync(Query("cdn." + Blocked), DnsTransportKind.Udp, default);

        Assert.Equal(RcodeNoError, Rcode(reply!));
        Assert.Single(_forwarder.Sent);
    }

    [Fact]
    public async Task AnswerAsync_ForwardsANameThatOnlyEndsLikeARule()
    {
        _forwarder.Answer = Answer("notexample.com");
        var handler = Make([new SiteRule(Blocked, includeSubdomains: true)]);

        await handler.AnswerAsync(Query("notexample.com"), DnsTransportKind.Udp, default);

        Assert.Single(_forwarder.Sent);
    }

    // A plan of several rules: the whole list is walked, so a domain in the middle or at the end is blocked as surely as the first.
    [Theory]
    [InlineData("first.example")]
    [InlineData("middle.example")]
    [InlineData("last.example")]
    public async Task AnswerAsync_MatchesARuleWhereverItSitsInThePlan(string domain)
    {
        var handler = Make(ThreeRules());

        var reply = await handler.AnswerAsync(Query(domain), DnsTransportKind.Udp, default);

        Assert.Equal(RcodeNameError, Rcode(reply!));
        Assert.Empty(_forwarder.Sent);
    }

    [Fact]
    public async Task AnswerAsync_ForwardsANameNoRuleInThePlanMatches()
    {
        _forwarder.Answer = Answer(Allowed);
        var handler = Make(ThreeRules());

        var reply = await handler.AnswerAsync(Query(Allowed), DnsTransportKind.Udp, default);

        Assert.Equal(RcodeNoError, Rcode(reply!));
        Assert.Single(_forwarder.Sent);
    }

    // The whole list is told about, not only the first rule's domain.
    [Fact]
    public async Task AnswerAsync_NotifiesWithTheDomainOfWhicheverRuleMatched()
    {
        var handler = Make(ThreeRules());

        await handler.AnswerAsync(Query("last.example"), DnsTransportKind.Udp, default);

        Assert.Equal(["last.example"], _notified);
    }

    // Matched whatever the client capitalised: DomainMatcher settles that for all layers and the parser has lower-cased the name.
    [Fact]
    public async Task AnswerAsync_MatchesARuleWhateverTheClientCapitalised()
    {
        var handler = Make([new SiteRule(Blocked, includeSubdomains: false)]);

        var reply = await handler.AnswerAsync(Query("ExAmPlE.cOm"), DnsTransportKind.Udp, default);

        Assert.Equal(RcodeNameError, Rcode(reply!));
        Assert.Empty(_forwarder.Sent);
    }

    [Fact]
    public async Task AnswerAsync_ForwardsAQueryForTheRootRatherThanMatchingItAgainstAnything()
    {
        _forwarder.Answer = Answer(Allowed);
        var handler = Make([new SiteRule(Blocked, includeSubdomains: true)]);

        await handler.AnswerAsync(RootQuery(), DnsTransportKind.Udp, default);

        Assert.Single(_forwarder.Sent);
    }

    [Fact]
    public async Task AnswerAsync_TheNxdomainEchoesTheQuestionAndTheIdentifier()
    {
        var query = Query(Blocked, id: 0xBEEF);
        var handler = Make([new SiteRule(Blocked, includeSubdomains: false)]);

        var reply = await handler.AnswerAsync(query, DnsTransportKind.Udp, default);

        Assert.Equal(0xBEEF, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(0, 2)));
        Assert.Equal(
            query.AsSpan(DnsQuery.HeaderLength).ToArray(),
            reply.AsSpan(DnsQuery.HeaderLength).ToArray());
    }

    // The OPT record RFC 6891 6.1.1 owes a client that sent one. The handler carries HasEdns from parse to builder.
    [Fact]
    public async Task AnswerAsync_TheNxdomainCarriesAnOptRecordWhenTheQueryDid()
    {
        var handler = Make([new SiteRule(Blocked, includeSubdomains: false)]);

        var withEdns = await handler.AnswerAsync(
            Query(Blocked, ednsPayloadSize: 4096), DnsTransportKind.Udp, default);
        var without = await handler.AnswerAsync(Query(Blocked), DnsTransportKind.Udp, default);

        Assert.Equal(1, Additionals(withEdns!));
        Assert.Equal(0, Additionals(without!));
    }

    [Fact]
    public async Task AnswerAsync_TheServerFailureCarriesAnOptRecordWhenTheQueryDid()
    {
        _forwarder.Answer = null;
        var handler = Make();

        var withEdns = await handler.AnswerAsync(
            Query(Allowed, ednsPayloadSize: 4096), DnsTransportKind.Udp, default);
        var without = await handler.AnswerAsync(Query(Allowed), DnsTransportKind.Udp, default);

        Assert.Equal(RcodeServerFailure, Rcode(withEdns!));
        Assert.Equal(1, Additionals(withEdns!));
        Assert.Equal(0, Additionals(without!));
    }

    // A blocked name is answered by the plan and never kept, or the cache would go on saying NXDOMAIN for up to an hour after the rule was lifted.
    [Fact]
    public async Task AnswerAsync_KeepsNothingAboutANameThePlanBlocked()
    {
        _forwarder.Answer = Answer(Blocked);
        var handler = Make([new SiteRule(Blocked, includeSubdomains: false)]);

        await handler.AnswerAsync(Query(Blocked), DnsTransportKind.Udp, default);
        Assert.Equal(0, _cache.Count);

        handler.UseRules([]);
        var reply = await handler.AnswerAsync(Query(Blocked), DnsTransportKind.Udp, default);

        Assert.Equal(RcodeNoError, Rcode(reply!));
        Assert.Single(_forwarder.Sent);
    }

    // The plan is consulted before the cache; reversed, a name blocked after being answered would keep coming out of the cache.
    [Fact]
    public async Task AnswerAsync_BlocksANameItHadAlreadyCached()
    {
        _forwarder.Answer = Answer(Blocked, ttl: 3600);
        var handler = Make();

        await handler.AnswerAsync(Query(Blocked), DnsTransportKind.Udp, default);
        Assert.Equal(1, _cache.Count);

        handler.UseRules([new SiteRule(Blocked, includeSubdomains: false)]);
        var reply = await handler.AnswerAsync(Query(Blocked), DnsTransportKind.Udp, default);

        Assert.Equal(RcodeNameError, Rcode(reply!));
    }

    [Fact]
    public async Task UseRules_TakesEffectOnTheNextQuery()
    {
        _forwarder.Answer = Answer(Blocked);
        var handler = Make();

        var before = await handler.AnswerAsync(Query(Blocked), DnsTransportKind.Udp, default);
        Assert.Equal(RcodeNoError, Rcode(before!));

        handler.UseRules([new SiteRule(Blocked, includeSubdomains: false)]);
        var after = await handler.AnswerAsync(Query(Blocked, id: 2), DnsTransportKind.Udp, default);

        Assert.Equal(RcodeNameError, Rcode(after!));
    }

    [Fact]
    public async Task UseRules_ReplacesThePlanRatherThanAddingToIt()
    {
        _forwarder.Answer = Answer(Blocked);
        var handler = Make([new SiteRule(Blocked, includeSubdomains: false)]);

        handler.UseRules([new SiteRule("elsewhere.example", includeSubdomains: false)]);
        var reply = await handler.AnswerAsync(Query(Blocked), DnsTransportKind.Udp, default);

        Assert.Equal(RcodeNoError, Rcode(reply!));
    }

    // The caller keeps editing its own list; the plan in force is the one it handed over.
    [Fact]
    public async Task UseRules_CopiesThePlanItWasGiven()
    {
        _forwarder.Answer = Answer(Blocked);
        var plan = new List<SiteRule>();
        var handler = Make();
        handler.UseRules(plan);

        plan.Add(new SiteRule(Blocked, includeSubdomains: false));
        var reply = await handler.AnswerAsync(Query(Blocked), DnsTransportKind.Udp, default);

        Assert.Equal(RcodeNoError, Rcode(reply!));
    }

    // An array cannot grow but its elements can be overwritten; skipping the copy for an array would hand the plan to whoever holds it.
    [Fact]
    public async Task UseRules_CopiesEvenAnArrayItWasHandedDirectly()
    {
        _forwarder.Answer = Answer(Blocked);
        var plan = new SiteRule[] { new("elsewhere.example", includeSubdomains: false) };
        var handler = Make();
        handler.UseRules(plan);

        plan[0] = new SiteRule(Blocked, includeSubdomains: false);
        var reply = await handler.AnswerAsync(Query(Blocked), DnsTransportKind.Udp, default);

        Assert.Equal(RcodeNoError, Rcode(reply!));
    }

    [Fact]
    public async Task AnswerAsync_ForwardsTheQueryByteForByte()
    {
        _forwarder.Answer = Answer(Allowed);
        var query = Query(Allowed, id: 0x4242);

        await Make().AnswerAsync(query, DnsTransportKind.Udp, default);

        Assert.Equal(query, _forwarder.Sent[0]);
    }

    // Compared with an answer built here, not the array the forwarder was given: the forwarder hands
    // over a copy, as a real one hands over bytes off a socket, so an octet written on the way out is seen.
    [Fact]
    public async Task AnswerAsync_ReturnsTheUpstreamAnswerUnchanged()
    {
        _forwarder.Answer = Answer(Allowed);

        var reply = await Make().AnswerAsync(Query(Allowed), DnsTransportKind.Udp, default);

        Assert.Equal(Answer(Allowed), reply);
    }

    [Fact]
    public async Task AnswerAsync_ReturnsServerFailureWhenNobodyAnswered()
    {
        _forwarder.Answer = null;

        var reply = await Make().AnswerAsync(Query(Allowed, id: 0x1357), DnsTransportKind.Udp, default);

        Assert.Equal(RcodeServerFailure, Rcode(reply!));
        Assert.Equal(0x1357, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(0, 2)));
    }

    [Fact]
    public async Task AnswerAsync_KeepsNothingWhenNobodyAnswered()
    {
        _forwarder.Answer = null;
        var handler = Make();

        await handler.AnswerAsync(Query(Allowed), DnsTransportKind.Udp, default);

        Assert.Equal(0, _cache.Count);
    }

    [Theory]
    [InlineData(DnsTransportKind.Udp, false)]
    [InlineData(DnsTransportKind.Tcp, true)]
    public async Task AnswerAsync_ForwardsOverTheTransportTheClientUsed(DnsTransportKind kind, bool overTcp)
    {
        _forwarder.Answer = Answer(Allowed);

        await Make().AnswerAsync(Query(Allowed), kind, default);

        Assert.Equal(overTcp, _forwarder.Calls[0].OverTcp);
    }

    [Fact]
    public async Task AnswerAsync_HandsTheCancellationTokenToTheForwarder()
    {
        using var source = new CancellationTokenSource();
        _forwarder.Answer = Answer(Allowed);

        await Make().AnswerAsync(Query(Allowed), DnsTransportKind.Udp, source.Token);

        Assert.Equal(source.Token, _forwarder.Calls[0].Token);
    }

    [Fact]
    public async Task AnswerAsync_LetsACancelledForwardOut()
    {
        var handler = new DnsRequestHandler(new CancellingForwarder(), Cache(), _notified.Add, _log);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.AnswerAsync(Query(Allowed), DnsTransportKind.Udp, CancellationToken.None));
    }

    // The server above reads into the datagram buffer again. Compared with a copy taken beforehand, not with itself.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnswerAsync_DoesNotEditTheCallersBuffer(bool fromTheCache)
    {
        _forwarder.Answer = Answer(Allowed);
        var handler = Make();

        if (fromTheCache)
        {
            await handler.AnswerAsync(Query(Allowed), DnsTransportKind.Udp, default);
        }

        // Spelled differently from the cached answer on purpose: the hit path writes this client's question into the answer, and must not write into this array.
        var query = Query("ExAmPlE.oRg", id: 7);
        var untouched = query.ToArray();

        await handler.AnswerAsync(query, DnsTransportKind.Udp, default);

        Assert.Equal(untouched, query);
    }

    [Fact]
    public async Task AnswerAsync_ServesTheSecondAskFromTheCache()
    {
        _forwarder.Answer = Answer(Allowed, ttl: 300);
        var handler = Make();

        await handler.AnswerAsync(Query(Allowed, id: 1), DnsTransportKind.Udp, default);
        var second = await handler.AnswerAsync(Query(Allowed, id: 2), DnsTransportKind.Udp, default);

        Assert.Single(_forwarder.Sent);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(second.AsSpan(0, 2)));
    }

    [Fact]
    public async Task AnswerAsync_AsksAgainOnceTheAnswerHasExpired()
    {
        _forwarder.Answer = Answer(Allowed, ttl: 60);
        var handler = Make();

        await handler.AnswerAsync(Query(Allowed, id: 1), DnsTransportKind.Udp, default);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await handler.AnswerAsync(Query(Allowed, id: 2), DnsTransportKind.Udp, default);

        Assert.Equal(2, _forwarder.Sent.Count);
    }

    [Fact]
    public async Task AnswerAsync_DoesNotServeOneRecordTypeOutOfAnothersAnswer()
    {
        _forwarder.Answer = Answer(Allowed);
        var handler = Make();

        await handler.AnswerAsync(Query(Allowed, type: TypeA), DnsTransportKind.Udp, default);
        await handler.AnswerAsync(Query(Allowed, type: TypeAaaa), DnsTransportKind.Udp, default);

        Assert.Equal(2, _forwarder.Sent.Count);
    }

    // The class the client asked in is part of the key. Every other test asks in IN, so a cache keyed by a class of its own would go unnoticed.
    [Fact]
    public async Task AnswerAsync_KeepsAnAnswerUnderTheClassItWasAskedIn()
    {
        _forwarder.Answer = Answer(Allowed, ttl: 300, qclass: ClassChaos);
        var handler = Make();

        await handler.AnswerAsync(
            Query(Allowed, id: 1, qclass: ClassChaos), DnsTransportKind.Udp, default);
        var second = await handler.AnswerAsync(
            Query(Allowed, id: 2, qclass: ClassChaos), DnsTransportKind.Udp, default);

        Assert.Single(_forwarder.Sent);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(second.AsSpan(0, 2)));
    }

    // A truncated answer has a section cut short on purpose; DnsResponseCache.Store refuses it, and
    // this test is the promise that the handler keeps no copy of its own.
    [Fact]
    public async Task AnswerAsync_DoesNotServeATruncatedUpstreamAnswerASecondTime()
    {
        _forwarder.Answer = Answer(Allowed, truncated: true);
        var handler = Make();

        await handler.AnswerAsync(Query(Allowed, id: 1), DnsTransportKind.Tcp, default);
        await handler.AnswerAsync(Query(Allowed, id: 2), DnsTransportKind.Tcp, default);

        Assert.Equal(2, _forwarder.Sent.Count);
        Assert.Equal(0, _cache.Count);
    }

    // What is kept is the upstream answer, not the TC reply cut for one client's buffer; the next client over TCP has no such buffer.
    [Fact]
    public async Task AnswerAsync_KeepsTheWholeAnswerEvenWhenTheAskerOnlyGotTheTruncatedOne()
    {
        _forwarder.Answer = Answer(Allowed, size: 900);
        var handler = Make();

        var cut = await handler.AnswerAsync(Query(Allowed, id: 1), DnsTransportKind.Udp, default);
        var whole = await handler.AnswerAsync(Query(Allowed, id: 2), DnsTransportKind.Tcp, default);

        Assert.True((cut![2] & Truncated) != 0);
        Assert.Single(_forwarder.Sent);
        Assert.Equal(900, whole!.Length);
    }

    [Fact]
    public async Task AnswerAsync_TruncatesACachedAnswerForAClientThatCannotTakeItWhole()
    {
        _forwarder.Answer = Answer(Allowed, size: 900);
        var handler = Make();

        var whole = await handler.AnswerAsync(Query(Allowed, id: 1), DnsTransportKind.Tcp, default);
        var cut = await handler.AnswerAsync(Query(Allowed, id: 2), DnsTransportKind.Udp, default);

        Assert.Equal(900, whole!.Length);
        Assert.Single(_forwarder.Sent);
        Assert.True((cut![2] & Truncated) != 0);
    }

    // The key is the lower-cased name; the octets held carry the first asker's capitalisation, which a
    // client that randomised its own compares octet for octet.
    [Fact]
    public async Task AnswerAsync_ACacheHitEchoesTheQuestionThisClientAsked()
    {
        _forwarder.Answer = Answer(Allowed, ttl: 300);
        var handler = Make();

        await handler.AnswerAsync(Query(Allowed, id: 1), DnsTransportKind.Udp, default);

        var mixed = Query("ExAmPlE.oRg", id: 2);
        var reply = await handler.AnswerAsync(mixed, DnsTransportKind.Udp, default);

        Assert.Single(_forwarder.Sent);
        Assert.Equal(
            mixed.AsSpan(DnsQuery.HeaderLength, QuestionLength(mixed)).ToArray(),
            reply.AsSpan(DnsQuery.HeaderLength, QuestionLength(mixed)).ToArray());
    }

    // Only the question. The records behind it are the upstream server's, handed on as they were, owner name included.
    [Fact]
    public async Task AnswerAsync_ACacheHitRewritesTheQuestionAndNothingBehindIt()
    {
        var upstream = Answer(Allowed, ttl: 300);
        _forwarder.Answer = upstream;
        var handler = Make();

        await handler.AnswerAsync(Query(Allowed, id: 1), DnsTransportKind.Udp, default);
        var reply = await handler.AnswerAsync(Query("ExAmPlE.oRg", id: 2), DnsTransportKind.Udp, default);

        var behind = DnsQuery.HeaderLength + QuestionLength(upstream);
        Assert.Equal(upstream.AsSpan(behind).ToArray(), reply.AsSpan(behind).ToArray());
    }

    // The two names are the same length on purpose: a handler checking only that the question sections
    // line up would splice this client's question onto somebody else's records.
    [Fact]
    public async Task AnswerAsync_WillNotServeAnAnswerThatEchoesADifferentQuestionOfTheSameLength()
    {
        Assert.Equal(Allowed.Length, Blocked.Length);

        _forwarder.Answer = Answer(Blocked, ttl: 300);
        var handler = Make();

        await handler.AnswerAsync(Query(Allowed, id: 1), DnsTransportKind.Udp, default);
        await handler.AnswerAsync(Query(Allowed, id: 2), DnsTransportKind.Udp, default);

        // Kept, and passed over all the same: an entry the cache never took would send the second query upstream for an unrelated reason.
        Assert.Equal(1, _cache.Count);
        Assert.Equal(2, _forwarder.Sent.Count);
    }

    // The name is not the whole question: an answer echoing another QTYPE holds records this client did not ask for.
    [Fact]
    public async Task AnswerAsync_WillNotServeAnAnswerThatEchoesADifferentType()
    {
        _forwarder.Answer = Answer(Allowed, ttl: 300, type: TypeAaaa);
        var handler = Make();

        await handler.AnswerAsync(Query(Allowed, type: TypeA, id: 1), DnsTransportKind.Udp, default);
        await handler.AnswerAsync(Query(Allowed, type: TypeA, id: 2), DnsTransportKind.Udp, default);

        // Kept, and passed over all the same: an entry the cache never took would send the second query upstream for an unrelated reason.
        Assert.Equal(1, _cache.Count);
        Assert.Equal(2, _forwarder.Sent.Count);
    }

    [Fact]
    public async Task AnswerAsync_WillNotServeAnAnswerThatEchoesADifferentClass()
    {
        _forwarder.Answer = Answer(Allowed, ttl: 300, qclass: ClassChaos);
        var handler = Make();

        await handler.AnswerAsync(
            Query(Allowed, id: 1, qclass: ClassInternet), DnsTransportKind.Udp, default);
        await handler.AnswerAsync(
            Query(Allowed, id: 2, qclass: ClassInternet), DnsTransportKind.Udp, default);

        Assert.Equal(1, _cache.Count);
        Assert.Equal(2, _forwarder.Sent.Count);
    }

    [Fact]
    public async Task AnswerAsync_WillNotServeAnAnswerThatEchoesNoQuestionAtAll()
    {
        _forwarder.Answer = Answer(Allowed, ttl: 300, questions: 0);
        var handler = Make();

        await handler.AnswerAsync(Query(Allowed, id: 1), DnsTransportKind.Udp, default);
        await handler.AnswerAsync(Query(Allowed, id: 2), DnsTransportKind.Udp, default);

        // Kept, and passed over all the same: an entry the cache never took would send the second query upstream for an unrelated reason.
        Assert.Equal(1, _cache.Count);
        Assert.Equal(2, _forwarder.Sent.Count);
    }

    // The name matches, so only the pointer is left to reject it on. The question written as two
    // octets is shorter than the one copied over it, so taking it would splice questions onto records.
    [Fact]
    public async Task AnswerAsync_WillNotServeAnAnswerWhoseQuestionIsACompressionPointer()
    {
        _forwarder.Answer = PointerQuestionAnswer(ttl: 300);
        var handler = Make();

        await handler.AnswerAsync(Query(PointedAtName, id: 1), DnsTransportKind.Udp, default);
        await handler.AnswerAsync(Query(PointedAtName, id: 2), DnsTransportKind.Udp, default);

        Assert.Equal(1, _cache.Count);
        Assert.Equal(2, _forwarder.Sent.Count);
    }

    [Fact]
    public async Task AnswerAsync_TruncatesAUdpAnswerThatDoesNotFitTheClientsBuffer()
    {
        _forwarder.Answer = Answer(Allowed, size: 900);

        var reply = await Make().AnswerAsync(Query(Allowed), DnsTransportKind.Udp, default);

        Assert.True((reply![2] & Truncated) != 0);
        Assert.Equal(RcodeNoError, Rcode(reply));
        Assert.True(reply.Length <= DnsQuery.DefaultUdpPayloadSize);
    }

    // Both sides of the boundary: an answer exactly the advertised buffer size is one the client can receive.
    [Fact]
    public async Task AnswerAsync_SendsAnAnswerThatIsExactlyTheAdvertisedBuffer()
    {
        _forwarder.Answer = Answer(Allowed, size: DnsQuery.DefaultUdpPayloadSize);

        var reply = await Make().AnswerAsync(Query(Allowed), DnsTransportKind.Udp, default);

        Assert.Equal(DnsQuery.DefaultUdpPayloadSize, reply!.Length);
        Assert.True((reply[2] & Truncated) == 0);
    }

    [Fact]
    public async Task AnswerAsync_TruncatesAnAnswerOneOctetOverTheAdvertisedBuffer()
    {
        _forwarder.Answer = Answer(Allowed, size: DnsQuery.DefaultUdpPayloadSize + 1);

        var reply = await Make().AnswerAsync(Query(Allowed), DnsTransportKind.Udp, default);

        Assert.True((reply![2] & Truncated) != 0);
    }

    [Fact]
    public async Task AnswerAsync_SendsTheWholeAnswerToAClientThatAdvertisedRoomForIt()
    {
        _forwarder.Answer = Answer(Allowed, size: 900);

        var reply = await Make().AnswerAsync(
            Query(Allowed, ednsPayloadSize: 4096), DnsTransportKind.Udp, default);

        Assert.Equal(900, reply!.Length);
    }

    [Fact]
    public async Task AnswerAsync_NeverTruncatesOverTcp()
    {
        _forwarder.Answer = Answer(Allowed, size: 4000);

        var reply = await Make().AnswerAsync(Query(Allowed), DnsTransportKind.Tcp, default);

        Assert.Equal(4000, reply!.Length);
    }

    [Fact]
    public async Task AnswerAsync_TheTruncatedReplyEchoesTheQuestionAndKeepsTheClientsOpt()
    {
        _forwarder.Answer = Answer(Allowed, size: 900);
        var query = Query("ExAmPlE.oRg", id: 0x0F0F, ednsPayloadSize: 512);

        var reply = await Make().AnswerAsync(query, DnsTransportKind.Udp, default);

        Assert.True((reply![2] & Truncated) != 0);
        Assert.Equal(0x0F0F, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(0, 2)));
        Assert.Equal(
            query.AsSpan(DnsQuery.HeaderLength, QuestionLength(query)).ToArray(),
            reply.AsSpan(DnsQuery.HeaderLength, QuestionLength(query)).ToArray());
        Assert.Equal(1, Additionals(reply));
    }

    [Fact]
    public async Task AnswerAsync_AnswersFormatErrorToAQueryItCannotRead()
    {
        // Twelve octets of header saying QDCOUNT 0, and two more nobody can read as a question.
        var reply = await Make().AnswerAsync(new byte[DnsQuery.HeaderLength + 2], DnsTransportKind.Udp, default);

        Assert.Equal(RcodeFormatError, Rcode(reply!));
        Assert.Empty(_forwarder.Sent);
    }

    [Fact]
    public async Task AnswerAsync_SaysNothingToADatagramTooShortToHoldAnIdentifier()
    {
        Assert.Null(await Make().AnswerAsync(new byte[4], DnsTransportKind.Udp, default));
        Assert.Empty(_forwarder.Sent);
    }

    [Fact]
    public async Task AnswerAsync_SaysNothingToSomebodyElsesAnswer()
    {
        var notAQuery = Query(Allowed);
        notAQuery[2] |= Qr;

        Assert.Null(await Make().AnswerAsync(notAQuery, DnsTransportKind.Udp, default));
        Assert.Empty(_forwarder.Sent);
    }

    [Fact]
    public async Task AnswerAsync_AnswersNotImplementedToAnOpcodeItDoesNotServe()
    {
        var update = Query(Allowed);
        update[2] |= 0x28; // OPCODE 5 (UPDATE)

        var reply = await Make().AnswerAsync(update, DnsTransportKind.Udp, default);

        Assert.Equal(RcodeNotImplemented, Rcode(reply!));
        Assert.Empty(_forwarder.Sent);
    }

    // BADVERS is extended code 16: four zero bits in the header and the top eight in the OPT record (RFC 6891 6.1.3).
    [Fact]
    public async Task AnswerAsync_AnswersBadVersionToAnEdnsVersionItDoesNotSpeak()
    {
        var reply = await Make().AnswerAsync(
            Query(Allowed, ednsPayloadSize: 4096, ednsVersion: 1), DnsTransportKind.Udp, default);

        Assert.NotNull(reply);
        Assert.Equal(RcodeNoError, Rcode(reply));
        Assert.Equal(1, Additionals(reply));

        // EXTENDED-RCODE is the first of the four octets the OPT record keeps where other records keep a
        // lifetime, and the record begins where the question ends.
        Assert.Equal(16 >> 4, reply[DnsQuery.HeaderLength + QuestionLength(reply) + 5]);
        Assert.Empty(_forwarder.Sent);
    }

    [Fact]
    public async Task AnswerAsync_NotifiesOnceForEveryBlockedAttempt()
    {
        var handler = Make([new SiteRule(Blocked, includeSubdomains: false)]);

        await handler.AnswerAsync(Query(Blocked), DnsTransportKind.Udp, default);
        await handler.AnswerAsync(Query(Blocked), DnsTransportKind.Udp, default);

        // Every attempt, not every distinct domain: how often the user is told is decided one layer up.
        Assert.Equal([Blocked, Blocked], _notified);
    }

    // The rule's domain, not the name asked for: a page load resolves many subdomains of one site, and
    // the user's list counts one notice per site.
    [Fact]
    public async Task AnswerAsync_NotifiesWithTheRulesDomainNotTheNameAskedFor()
    {
        var handler = Make([new SiteRule(Blocked, includeSubdomains: true)]);

        await handler.AnswerAsync(Query("CDN.ExAmPlE.cOm"), DnsTransportKind.Udp, default);
        await handler.AnswerAsync(Query("www.example.com"), DnsTransportKind.Udp, default);

        Assert.Equal([Blocked, Blocked], _notified);
    }

    [Fact]
    public async Task AnswerAsync_DoesNotNotifyForANameItForwarded()
    {
        _forwarder.Answer = Answer(Allowed);

        await Make().AnswerAsync(Query(Allowed), DnsTransportKind.Udp, default);

        Assert.Empty(_notified);
    }

    [Fact]
    public async Task AnswerAsync_DoesNotNotifyForAQueryItCouldNotRead()
    {
        var handler = Make([new SiteRule(Blocked, includeSubdomains: false)]);

        await handler.AnswerAsync(new byte[DnsQuery.HeaderLength + 2], DnsTransportKind.Udp, default);

        Assert.Empty(_notified);
    }

    // A domain learned from an attempt is a visit and is not written above Debug, even when it matched
    // a user's own rule. All four paths, since each has its own message.
    [Fact]
    public async Task AnswerAsync_LogsAQueriedDomainNoHigherThanDebug()
    {
        _forwarder.Answer = Answer(Allowed, ttl: 300);
        var handler = Make([new SiteRule(Blocked, includeSubdomains: false)]);

        await handler.AnswerAsync(Query(Blocked), DnsTransportKind.Udp, default);          // blocked
        await handler.AnswerAsync(Query(Allowed, id: 1), DnsTransportKind.Udp, default);   // forwarded
        await handler.AnswerAsync(Query(Allowed, id: 2), DnsTransportKind.Udp, default);   // from the cache

        _forwarder.Answer = null;
        await handler.AnswerAsync(Query("nobody.example"), DnsTransportKind.Udp, default);  // SERVFAIL

        foreach (var domain in new[] { Blocked, Allowed, "nobody.example" })
        {
            Assert.DoesNotContain(
                _log.Entries.Where(entry => entry.Level > LogLevel.Debug),
                entry => entry.Message.Contains(domain, StringComparison.OrdinalIgnoreCase));

            // And the domain is written down somewhere, so a deleted log is not what makes the assertion above pass.
            Assert.Contains(
                _log.Entries.Where(entry => entry.Level == LogLevel.Debug),
                entry => entry.Message.Contains(domain, StringComparison.OrdinalIgnoreCase));
        }
    }

    private DnsResponseCache Cache() => _cache ??= new DnsResponseCache(_clock);

    private DnsRequestHandler Make(IReadOnlyCollection<SiteRule>? rules = null)
    {
        var handler = new DnsRequestHandler(_forwarder, Cache(), _notified.Add, _log);

        if (rules is not null)
        {
            handler.UseRules(rules);
        }

        return handler;
    }

    // A plan the size of a real list, so no rule is reached by being the only one.
    private static SiteRule[] ThreeRules() =>
    [
        new("first.example", includeSubdomains: false),
        new("middle.example", includeSubdomains: false),
        new("last.example", includeSubdomains: false),
    ];

    private static byte Rcode(byte[] message) => (byte)(message[3] & 0x0F);

    private static int Additionals(byte[] message) => BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(10, 2));

    /// <summary>How many octets the question of this message occupies, by this file's own arithmetic so assertions do not depend on the codec under test.</summary>
    private static int QuestionLength(byte[] message)
    {
        var pos = DnsQuery.HeaderLength;
        while (message[pos] != 0)
        {
            pos += 1 + message[pos];
        }

        return pos + 1 + 4 - DnsQuery.HeaderLength;
    }

    /// <summary>Encodes exactly the spelling given, which <see cref="DnsName.Encode"/> does not (it lower-cases via IdnMapping). A DNS-0x20 client's point is its capitalisation.</summary>
    private static byte[] EncodeLiteral(string name)
    {
        var bytes = new List<byte>();

        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }

        bytes.Add(0);
        return [.. bytes];
    }

    private static byte[] Query(
        string name,
        ushort type = TypeA,
        ushort id = 1,
        ushort qclass = ClassInternet,
        ushort? ednsPayloadSize = null,
        byte ednsVersion = 0)
    {
        var message = new List<byte>(new byte[DnsQuery.HeaderLength]);
        message.AddRange(EncodeLiteral(name));
        message.AddRange(Be16(type));
        message.AddRange(Be16(qclass));

        if (ednsPayloadSize is { } size)
        {
            message.AddRange(Opt(size, ednsVersion));
        }

        var query = message.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(0, 2), id);
        query[2] = RecursionDesired;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), 1); // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(
            query.AsSpan(10, 2), ednsPayloadSize is null ? (ushort)0 : (ushort)1); // ARCOUNT

        return query;
    }

    // A question for the root, which has no labels and which no rule can match.
    private static byte[] RootQuery(ushort id = 1)
    {
        var query = new byte[DnsQuery.HeaderLength + 1 + 4];

        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(0, 2), id);
        query[2] = RecursionDesired;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), 1);          // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(13, 2), TypeA);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(15, 2), ClassInternet);

        return query;
    }

    // In an OPT record the class field is the requestor's buffer size and the TTL position holds EXTENDED-RCODE, VERSION, DO and Z (RFC 6891 6.1.2, 6.1.3).
    private static byte[] Opt(ushort payloadSize, byte version)
    {
        var record = new byte[11];

        record[0] = 0; // owned by the root
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(1, 2), OptType);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(3, 2), payloadSize);
        record[6] = version;

        return record;
    }

    /// <summary>An answer as an upstream server writes one: the question echoed, then one record owned by the same name with <paramref name="size"/> bytes of data.</summary>
    private static byte[] Answer(
        string name,
        uint ttl = 300,
        ushort id = 1,
        ushort type = TypeA,
        ushort qclass = ClassInternet,
        int size = 0,
        bool truncated = false,
        ushort questions = 1)
    {
        var encoded = EncodeLiteral(name);
        var fixedLength = DnsQuery.HeaderLength + (questions * (encoded.Length + 4)) + encoded.Length + 10;
        var rdata = Math.Max(4, size - fixedLength);

        var message = new List<byte>(new byte[DnsQuery.HeaderLength]);

        for (var question = 0; question < questions; question++)
        {
            message.AddRange(encoded);
            message.AddRange(Be16(type));
            message.AddRange(Be16(qclass));
        }

        message.AddRange(encoded);
        message.AddRange(Be16(type));
        message.AddRange(Be16(qclass));
        message.AddRange([(byte)(ttl >> 24), (byte)(ttl >> 16), (byte)(ttl >> 8), (byte)ttl]);
        message.AddRange(Be16((ushort)rdata));
        message.AddRange(new byte[rdata]);

        var answer = message.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(0, 2), id);
        answer[2] = (byte)(Qr | RecursionDesired | (truncated ? Truncated : 0));
        answer[3] = 0x80; // RA, RCODE 0
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(4, 2), questions); // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(6, 2), 1);         // ANCOUNT

        return answer;
    }

    /// <summary>
    /// An answer whose question name is a compression pointer into the header, which no honest
    /// responder produces. The header octets are picked so the pointer reads as a name (a one-octet
    /// label 'p', then a zero), so the answer parses, walks and is worth caching, and reads as
    /// <see cref="PointedAtName"/>.
    ///
    /// The pointer names offset 2, not 0: the cache writes the asker's identifier over the first two
    /// octets, which would change the name before comparison. Offset 3 doubles as the RCODE octet, so
    /// the label is 'p' (0x70): its low nibble is zero and the answer still says NOERROR.
    /// </summary>
    private static byte[] PointerQuestionAnswer(uint ttl)
    {
        var message = new List<byte>(new byte[DnsQuery.HeaderLength]);

        message.AddRange([0xC0, 0x02]); // the question name: a pointer to offset 2
        message.AddRange(Be16(TypeA));
        message.AddRange(Be16(ClassInternet));

        message.AddRange(EncodeLiteral(PointedAtName)); // the record's owner, spelled out
        message.AddRange(Be16(TypeA));
        message.AddRange(Be16(ClassInternet));
        message.AddRange([(byte)(ttl >> 24), (byte)(ttl >> 16), (byte)(ttl >> 8), (byte)ttl]);
        message.AddRange(Be16(4));
        message.AddRange(new byte[4]);

        var answer = message.ToArray();
        answer[2] = 1;                 // the label length the pointer lands on, and RD as a flag
        answer[3] = (byte)'p';         // its one octet, and RCODE 0 in the low nibble
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(4, 2), 1); // QDCOUNT, and the zero
                                                                       // octet that ends the name
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(6, 2), 1); // ANCOUNT

        return answer;
    }

    private static byte[] Be16(ushort value) => [(byte)(value >> 8), (byte)value];

    private sealed class FakeForwarder : IDnsForwarder
    {
        public List<Call> Calls { get; } = [];

        public byte[]? Answer { get; set; }

        /// <summary>The queries as they arrived, copied: the handler must not edit the buffer.</summary>
        public IReadOnlyList<byte[]> Sent => [.. Calls.Select(call => call.Query)];

        public Task<byte[]?> ForwardAsync(byte[] query, bool overTcp, CancellationToken ct)
        {
            Calls.Add(new Call([.. query], overTcp, ct));

            // A copy, as an answer off a socket is a fresh array each time; handing out the field itself
            // would compare the reply with an array the handler could edit.
            byte[]? answer = Answer is { } held ? [.. held] : null;
            return Task.FromResult(answer);
        }
    }

    /// <summary>A forwarder whose send is cancelled mid-flight like the real transport: the exception comes out of the send.</summary>
    private sealed class CancellingForwarder : IDnsForwarder
    {
        public async Task<byte[]?> ForwardAsync(byte[] query, bool overTcp, CancellationToken ct)
        {
            await Task.Yield();
            throw new OperationCanceledException();
        }
    }

    private readonly record struct Call(byte[] Query, bool OverTcp, CancellationToken Token);
}
