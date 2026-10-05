using System.Diagnostics;
using Chronos.Service.Diagnostics;

namespace Chronos.Service.Tests;

public sealed class RepeatedDiagnosticTests
{
    [Fact]
    public void IsNews_IsTrueOnceForACauseThatDoesNotChange()
    {
        var diagnostic = new RepeatedDiagnostic();

        Assert.True(diagnostic.IsNews("disk full"));
        Assert.False(diagnostic.IsNews("disk full"));
        Assert.False(diagnostic.IsNews("disk full"));
    }

    [Fact]
    public void IsNews_IsTrueAgainWhenTheCauseClearsAndReturns()
    {
        var diagnostic = new RepeatedDiagnostic();

        Assert.True(diagnostic.IsNews("disk full"));
        Assert.False(diagnostic.IsNews(null));
        Assert.True(diagnostic.IsNews("disk full"));
    }

    // Suppression is keyed on the cause, not on whether anything was reported before: a plain sticky flag passes the other two tests and fails here.
    [Fact]
    public void IsNews_IsTrueAgainWhenAnotherCauseTakesOver()
    {
        var diagnostic = new RepeatedDiagnostic();

        Assert.True(diagnostic.IsNews("disk full"));
        Assert.True(diagnostic.IsNews("file locked"));
        Assert.False(diagnostic.IsNews("file locked"));
        Assert.True(diagnostic.IsNews("disk full"));
    }

    // The type owns its lock, so callers that never agreed on one still report a permanent cause exactly once.
    // Parallel.For ramps workers up too slowly to overlap, so dedicated threads gather on a barrier and spin to a shared instant.
    // Many rounds, since one instant is a coin toss: with the lock removed most of the 200 report more than once.
    [Fact]
    public void IsNews_ReportsExactlyOnceUnderConcurrentCallers()
    {
        const int Rounds = 200;
        var workers = Math.Max(Environment.ProcessorCount, 4);
        var spin = Stopwatch.Frequency / 20_000; // 50 microseconds, long enough to cover the skew.

        var diagnostics = new RepeatedDiagnostic[Rounds];
        for (var round = 0; round < Rounds; round++)
        {
            diagnostics[round] = new RepeatedDiagnostic();
        }

        var news = new int[Rounds];
        long releaseAt = 0;

        using var barrier = new Barrier(
            workers,
            _ => Volatile.Write(ref releaseAt, Stopwatch.GetTimestamp() + spin));

        var threads = new Thread[workers];
        for (var worker = 0; worker < workers; worker++)
        {
            threads[worker] = new Thread(() =>
            {
                for (var round = 0; round < Rounds; round++)
                {
                    barrier.SignalAndWait();

                    var target = Volatile.Read(ref releaseAt);
                    while (Stopwatch.GetTimestamp() < target)
                    {
                        // Spinning on purpose: any yield here would scatter the threads again.
                    }

                    if (diagnostics[round].IsNews("disk full"))
                    {
                        Interlocked.Increment(ref news[round]);
                    }
                }
            })
            {
                IsBackground = true,
                Name = $"repeated-diagnostic-{worker}",
            };
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        var wrong = news.Index().Where(entry => entry.Item != 1).ToArray();

        Assert.True(
            wrong.Length == 0,
            $"{wrong.Length} of {Rounds} rounds did not report exactly once: " + string.Join(
                ", ",
                wrong.Take(5).Select(entry => $"round {entry.Index} reported {entry.Item} times")));
    }
}
