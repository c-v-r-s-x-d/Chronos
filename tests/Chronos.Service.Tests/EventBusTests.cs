using Chronos.Ipc;
using Chronos.Service.Ipc;

namespace Chronos.Service.Tests;

public sealed class EventBusTests
{
    private static readonly DateTimeOffset Moment = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Publish_ReachesEverySubscriber()
    {
        var bus = new EventBus();
        using var first = bus.Subscribe(out var one);
        using var second = bus.Subscribe(out var two);

        bus.Publish(Event(1));

        Assert.True(one.TryRead(out var toFirst));
        Assert.True(two.TryRead(out var toSecond));
        Assert.Equal("1", toFirst!.AppName);
        Assert.Equal("1", toSecond!.AppName);
    }

    [Fact]
    public void Publish_DoesNotBlockWhenASubscriberNeverReads()
    {
        var bus = new EventBus();
        using var subscription = bus.Subscribe(out _);

        for (var i = 0; i < 1000; i++)
        {
            bus.Publish(Event(i));
        }

        Assert.Equal(1, bus.SubscriberCount);
    }

    [Fact]
    public void Publish_KeepsTheNewestEventsWhenTheQueueOverflows()
    {
        var bus = new EventBus();
        using var subscription = bus.Subscribe(out var events);

        for (var i = 0; i < 100; i++)
        {
            bus.Publish(Event(i));
        }

        var delivered = new List<string?>();
        while (events.TryRead(out var one))
        {
            delivered.Add(one.AppName);
        }

        // Sixty-four kept, and the last sixty-four: an event dropped for room is the oldest, never the newest.
        Assert.Equal(64, delivered.Count);
        Assert.Equal("36", delivered[0]);
        Assert.Equal("99", delivered[^1]);
    }

    [Fact]
    public void SubscriberCount_DropsWhenASubscriptionIsDisposed()
    {
        var bus = new EventBus();

        var subscription = bus.Subscribe(out _);
        Assert.Equal(1, bus.SubscriberCount);

        subscription.Dispose();

        Assert.Equal(0, bus.SubscriberCount);
    }

    [Fact]
    public void Publish_AfterASubscriptionIsDisposed_ReachesNobody()
    {
        var bus = new EventBus();
        var subscription = bus.Subscribe(out var events);

        subscription.Dispose();
        bus.Publish(Event(1));

        Assert.False(events.TryRead(out _));
    }

    [Fact]
    public void Dispose_EndsTheReaderSoAWaitingSubscriberStops()
    {
        var bus = new EventBus();
        var subscription = bus.Subscribe(out var events);

        subscription.Dispose();

        // Completed, not merely empty: the connection task waits on this reader, and a wait that never ends would hold its slot until the service stops.
        Assert.True(events.Completion.IsCompleted);
    }

    [Fact]
    public void Publish_WithNoSubscribers_DoesNothingAndDoesNotThrow()
    {
        var bus = new EventBus();

        bus.Publish(Event(1));

        Assert.Equal(0, bus.SubscriberCount);
    }

    private static IpcEvent Event(int number) =>
        new(IpcEventKind.AppBlocked, Moment, null, number.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
}
