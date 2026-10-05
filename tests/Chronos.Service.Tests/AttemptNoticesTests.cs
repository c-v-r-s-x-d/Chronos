using Chronos.Service.Dns;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Tests;

/// <summary>One notice per domain per five minutes, so a browser retrying a blocked name does not become a stream of events.</summary>
public sealed class AttemptNoticesTests
{
    private const string Domain = "example.com";

    private readonly ServiceTestClock _clock = new(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly CapturingLogger<AttemptNotices> _log = new();
    private readonly List<string> _sent = [];

    [Fact]
    public void TheFirstAttemptAtADomainIsPassedOn()
    {
        Make().Notify(Domain);

        Assert.Equal([Domain], _sent);
    }

    [Fact]
    public void AnotherAttemptWithinFiveMinutesIsNotPassedOn()
    {
        var notices = Make();
        notices.Notify(Domain);

        _clock.Advance(AttemptNotices.Interval - TimeSpan.FromTicks(1));
        notices.Notify(Domain);

        Assert.Single(_sent);
    }

    [Fact]
    public void AnAttemptFiveMinutesLaterIsPassedOnAgain()
    {
        var notices = Make();
        notices.Notify(Domain);

        _clock.Advance(AttemptNotices.Interval);
        notices.Notify(Domain);

        Assert.Equal([Domain, Domain], _sent);
    }

    [Fact]
    public void TheIntervalIsFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), AttemptNotices.Interval);
    }

    [Fact]
    public void TheIntervalIsCountedFromTheNoticeThatWasPassedOnNotFromTheLastAttempt()
    {
        // Counted from the last attempt instead, a browser retrying every minute would never be heard from again.
        var notices = Make();
        notices.Notify(Domain);

        _clock.Advance(TimeSpan.FromMinutes(3));
        notices.Notify(Domain);
        _clock.Advance(TimeSpan.FromMinutes(2));
        notices.Notify(Domain);

        Assert.Equal([Domain, Domain], _sent);
    }

    [Fact]
    public void EachDomainHasAnIntervalOfItsOwn()
    {
        var notices = Make();

        notices.Notify(Domain);
        notices.Notify("example.org");

        Assert.Equal([Domain, "example.org"], _sent);
    }

    [Fact]
    public void TheSameDomainSpelledInOtherCapitalsIsTheSameDomain()
    {
        var notices = Make();

        notices.Notify(Domain);
        notices.Notify("ExAmPlE.CoM");

        Assert.Single(_sent);
    }

    [Fact]
    public void ASinkThatThrowsDoesNotReachTheCaller()
    {
        // The caller is the answer path: an exception here would leave a client without its NXDOMAIN.
        var notices = new AttemptNotices(_ => throw new InvalidOperationException("boom"), _clock, _log);

        notices.Notify(Domain);

        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Debug);
    }

    [Fact]
    public void ASinkThatThrowsAnythingAtAllDoesNotReachTheCaller()
    {
        // Not only the exception types used elsewhere in this suite: the sink is a seam with no exception contract.
        var notices = new AttemptNotices(_ => throw new SinkFailure(), _clock, _log);

        notices.Notify(Domain);

        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Debug);
    }

    [Fact]
    public void ASinkThatThrewIsNotAskedAgainWithinTheInterval()
    {
        var calls = 0;
        var notices = new AttemptNotices(
            _ =>
            {
                calls++;
                throw new InvalidOperationException("boom");
            },
            _clock,
            _log);

        notices.Notify(Domain);
        notices.Notify(Domain);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void NothingIsWrittenAboveDebug()
    {
        var notices = new AttemptNotices(_ => throw new InvalidOperationException(Domain), _clock, _log);

        notices.Notify(Domain);
        notices.Notify(Domain);

        Assert.All(_log.Entries, entry => Assert.Equal(LogLevel.Debug, entry.Level));
    }

    [Fact]
    public void ANullDomainIsIgnoredRatherThanThrown()
    {
        Make().Notify(null!);

        Assert.Empty(_sent);
    }

    [Fact]
    public void OnceFullANewDomainIsDroppedUntilAnEntryExpires()
    {
        var notices = Make();

        for (var i = 0; i < AttemptNotices.Capacity; i++)
        {
            notices.Notify($"n{i}.example");
        }

        notices.Notify("one-too-many.example");
        Assert.DoesNotContain("one-too-many.example", _sent);

        _clock.Advance(AttemptNotices.Interval);
        notices.Notify("one-too-many.example");
        Assert.Contains("one-too-many.example", _sent);
    }

    [Fact]
    public void ADomainAlreadyHeldIsStillThrottledWhenTheTableIsFull()
    {
        var notices = Make();

        for (var i = 0; i < AttemptNotices.Capacity; i++)
        {
            notices.Notify($"n{i}.example");
        }

        notices.Notify("n0.example");

        Assert.Equal(AttemptNotices.Capacity, _sent.Count);
    }

    [Fact]
    public void ItRefusesToBeBuiltWithoutASinkAClockOrALogger()
    {
        Assert.Throws<ArgumentNullException>(() => new AttemptNotices(null!, _clock, _log));
        Assert.Throws<ArgumentNullException>(() => new AttemptNotices(_sent.Add, null!, _log));
        Assert.Throws<ArgumentNullException>(() => new AttemptNotices(_sent.Add, _clock, null!));
    }

    private AttemptNotices Make() => new(_sent.Add, _clock, _log);

    private sealed class SinkFailure : Exception;
}
