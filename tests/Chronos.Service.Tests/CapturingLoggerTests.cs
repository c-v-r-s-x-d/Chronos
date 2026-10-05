using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Tests;

public sealed class CapturingLoggerTests
{
    // A bare List<T> drops entries under concurrent logging, so the helper must be the one thing in
    // the test that cannot lie.
    //
    // Dedicated threads rather than Parallel.For or the pool: the barrier releases only once every
    // writer has reached it, and a scheduler running a few at a time would never release it or would
    // serialise away the contention. Each writer catches its own throw and hands it back, because an
    // escape from a raw thread takes the test host down.
    [Fact]
    public void Log_KeepsEveryEntryWhenManyThreadsWriteAtOnce()
    {
        const int Writers = 8;
        const int PerWriter = 500;

        var logger = new CapturingLogger<CapturingLoggerTests>();
        var failures = new ConcurrentBag<Exception>();
        using var start = new Barrier(Writers);

        var threads = Enumerable.Range(0, Writers)
            .Select(writer => new Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    for (var i = 0; i < PerWriter; i++)
                    {
                        logger.LogInformation("entry from {Writer}", writer);
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }))
            .ToList();

        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());

        Assert.Empty(failures);
        Assert.Equal(Writers * PerWriter, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
    }
}
