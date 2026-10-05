using Chronos.Core.Enforcement;
using Chronos.Core.Sessions;
using Chronos.Core.Time;
using Chronos.Service.Configuration;
using Chronos.Service.Ipc;
using Chronos.Service.Reconciliation;
using Chronos.Service.Rules;
using Chronos.Service.Sessions;
using Chronos.Service.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

public sealed class ReconcileSchedulerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));

    private sealed class BlockingEnforcer : IEnforcer
    {
        private readonly SemaphoreSlim _release = new(0);

        public string Name => "blocking";

        public int Concurrent;

        public int MaxConcurrent;

        public Task<EnforcerAvailability> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(EnforcerAvailability.Available);

        public async Task<ReconcileResult> ReconcileAsync(EnforcementPlan plan, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref Concurrent);
            MaxConcurrent = Math.Max(MaxConcurrent, now);

            await _release.WaitAsync(ct).ConfigureAwait(false);

            Interlocked.Decrement(ref Concurrent);
            return ReconcileResult.Unchanged(Name);
        }

        public Task ClearAsync(CancellationToken ct) => Task.CompletedTask;

        public void Release(int count) => _release.Release(count);
    }

    private ReconcileScheduler Build(IEnforcer enforcer)
    {
        var paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();

        var runner = new ReconcileRunner(
            new SessionEngine(new ServiceTestClock(DateTimeOffset.UnixEpoch), new WindowsProtectedAppPolicy()),
            new ReconcileCoordinator([enforcer]),
            new LayerStatusRegistry(SystemClock.Instance),
            new StateStore(paths, NullLogger<StateStore>.Instance),
            new SessionGate(),
            new EventBus(),
            TestStatus.Blank,
            NullLogger<ReconcileRunner>.Instance);

        return new ReconcileScheduler(runner, NullLogger<ReconcileScheduler>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void PeriodIsFifteenSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), ReconcileScheduler.Period);
    }

    [Fact]
    public async Task PassesNeverOverlap()
    {
        var enforcer = new BlockingEnforcer();
        var scheduler = Build(enforcer);

        var first = scheduler.RunPendingAsync("first", CancellationToken.None);
        var second = scheduler.RunPendingAsync("second", CancellationToken.None);

        enforcer.Release(2);
        await Task.WhenAll(first, second);

        Assert.Equal(1, enforcer.MaxConcurrent);
    }

    [Fact]
    public async Task SecondPassWaitsForTheFirstRatherThanBeingDropped()
    {
        var enforcer = new BlockingEnforcer();
        var scheduler = Build(enforcer);

        var first = scheduler.RunPendingAsync("first", CancellationToken.None);
        var second = scheduler.RunPendingAsync("second", CancellationToken.None);

        Assert.False(second.IsCompleted);

        enforcer.Release(2);
        await Task.WhenAll(first, second);

        Assert.True(second.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RequestImmediate_WakesTheLoopWithoutWaitingOutThePeriod()
    {
        var scheduler = Build(new BlockingEnforcer());

        scheduler.RequestImmediate("network change");
        var reason = await scheduler.WaitForNextTriggerAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("network change", reason);
    }

    [Fact]
    public async Task WaitForNextTrigger_DoesNotFireBeforeThePeriodElapses()
    {
        var scheduler = Build(new BlockingEnforcer());

        // Period is 15 s, so a 2 s budget proves nothing fired early; the wait is then abandoned.
        await Assert.ThrowsAsync<TimeoutException>(
            () => scheduler.WaitForNextTriggerAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RequestImmediate_IsCoalescedRatherThanQueued()
    {
        var scheduler = Build(new BlockingEnforcer());

        scheduler.RequestImmediate("first");
        scheduler.RequestImmediate("second");

        var reason = await scheduler.WaitForNextTriggerAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("second", reason);

        await Assert.ThrowsAsync<TimeoutException>(
            () => scheduler.WaitForNextTriggerAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RequestImmediate_IsSafeWhenCalledConcurrently()
    {
        var scheduler = Build(new BlockingEnforcer());

        var callers = Enumerable.Range(0, 32)
            .Select(i => Task.Run(() => scheduler.RequestImmediate($"caller {i}")))
            .ToArray();

        await Task.WhenAll(callers);

        var reason = await scheduler.WaitForNextTriggerAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.StartsWith("caller ", reason, StringComparison.Ordinal);

        await Assert.ThrowsAsync<TimeoutException>(
            () => scheduler.WaitForNextTriggerAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
