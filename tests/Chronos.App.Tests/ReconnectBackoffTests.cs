using Chronos.App.Services;

namespace Chronos.App.Tests;

public sealed class ReconnectBackoffTests
{
    [Fact]
    public void Waits_GrowAndThenStopGrowing()
    {
        var backoff = new ReconnectBackoff();

        var waits = Enumerable.Range(0, 8).Select(_ => backoff.Next()).ToList();

        // Growing, so a service that stays down is not asked for every quarter second for hours.
        Assert.Equal(waits.OrderBy(static wait => wait).ToList(), waits);

        // Capped, so a service that comes back is noticed in seconds rather than in minutes.
        Assert.All(waits, wait => Assert.True(wait <= TimeSpan.FromSeconds(5), $"{wait} is past the cap."));
        Assert.Equal(TimeSpan.FromSeconds(5), waits[^1]);
        Assert.True(waits[0] < TimeSpan.FromSeconds(1), $"The first retry waited {waits[0]}.");
        Assert.True(waits[0] > TimeSpan.Zero, "The first retry did not wait at all.");
    }

    [Fact]
    public void AConnectionThatWorked_PutsTheWaitBackWhereItStarted()
    {
        var backoff = new ReconnectBackoff();

        var first = backoff.Next();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            backoff.Next();
        }

        backoff.Reset();

        Assert.Equal(first, backoff.Next());
    }
}
