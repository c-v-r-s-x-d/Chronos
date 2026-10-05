using Chronos.Service.Apps;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Tests;

public sealed class ProcessEventsFactoryTests
{
    private readonly CapturingLogger<ProcessEventsFactoryTests> _logger = new();

    private static void Ignore(ProcessStarted started) => _ = started;

    [Fact]
    public void UsesThePrimarySourceWhenItStarts()
    {
        var primary = new FakeProcessEvents { SourceName = "etw" };
        var fallback = new FakeProcessEvents { SourceName = "wmi" };

        var events = ProcessEventsFactory.Create(Ignore, () => primary, () => fallback, _logger);

        Assert.Same(primary, events);
        Assert.Equal(1, primary.StartCalls);
        Assert.Equal(0, fallback.StartCalls);
    }

    [Fact]
    public void SubscribesTheHandlerBeforeDeliveryBegins()
    {
        var primary = new FakeProcessEvents();
        var seen = new List<int>();

        var events = ProcessEventsFactory.Create(
            started => seen.Add(started.ProcessId),
            () => primary,
            () => new FakeProcessEvents(),
            _logger);

        primary.Raise(new ProcessStarted(4242, @"D:\games\game.exe"));

        Assert.Same(primary, events);
        Assert.Equal([4242], seen);
    }

    [Fact]
    public void FallsBackAndSaysSoWhenThePrimaryCannotStart()
    {
        // The fallback is blind to full paths, so the switch must never be silent.
        var primary = new FakeProcessEvents
        {
            FailToStartWith = new InvalidOperationException("no trace session available"),
        };
        var fallback = new FakeProcessEvents { SourceName = "wmi" };

        var events = ProcessEventsFactory.Create(Ignore, () => primary, () => fallback, _logger);

        Assert.Same(fallback, events);
        Assert.True(fallback.IsStarted);
        Assert.True(primary.IsDisposed);

        // The sentence, not only the level: the fallback appears in no layer status, and the diagnostic package finds it by searching retained logs for this string.
        var record = Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Information);

        Assert.Equal(ProcessEventsFactory.FellBackToWmi, record.Message);
    }

    [Fact]
    public void RethrowsAfterAnErrorRecordWhenBothSourcesFail()
    {
        var primary = new FakeProcessEvents { FailToStartWith = new InvalidOperationException("no session") };
        var fallback = new FakeProcessEvents { FailToStartWith = new InvalidOperationException("access denied") };

        Assert.Throws<InvalidOperationException>(
            () => ProcessEventsFactory.Create(Ignore, () => primary, () => fallback, _logger));

        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.True(fallback.IsDisposed);
    }
}
