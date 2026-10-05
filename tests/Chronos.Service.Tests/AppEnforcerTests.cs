using Chronos.Core.Enforcement;
using Chronos.Core.Rules;
using Chronos.Service.Apps;
using Chronos.Service.Ipc;
using Chronos.Service.Rules;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

public sealed class AppEnforcerTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeProcessControl _processes = new();
    private readonly FakeProcessEvents _events = new();
    private readonly ServiceTestClock _clock = new(Start);

    /// <summary>Sources handed out ahead of <see cref="_events"/>, for tests that need a second source.</summary>
    private readonly Queue<FakeProcessEvents> _next = new();

    // The delegate starts the source it returns, as ProcessEventsFactory does; the enforcer must not start it again.
    private AppEnforcer Create() => new(
        new AppWatchdog(_processes, new WindowsProtectedAppPolicy(), _clock, new EventBus(), TestStatus.Blank, NullLogger<AppWatchdog>.Instance),
        onStarted =>
        {
            var source = _next.Count > 0 ? _next.Dequeue() : _events;
            source.Start(onStarted);

            return source;
        },
        _clock,
        NullLogger<AppEnforcer>.Instance);

    private static EnforcementPlan Plan(params string[] fileNames) => new(
        Guid.NewGuid(),
        Start.AddHours(1),
        [],
        [.. fileNames.Select(name => new AppRule(AppMatchKind.FileName, name))]);

    [Fact]
    public async Task Probe_ReportsAvailableBeforeAnySessionHasNeededTheSource()
    {
        Assert.True((await Create().ProbeAsync(CancellationToken.None)).IsAvailable);
    }

    [Fact]
    public async Task Reconcile_StartsTheSourceAndArmsTheRules()
    {
        var enforcer = Create();

        var result = await enforcer.ReconcileAsync(Plan("game.exe"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.True(_events.IsStarted);

        _events.Raise(new ProcessStarted(4242, @"D:\games\game.exe"));
        Assert.Equal([4242], _processes.Killed);
    }

    [Fact]
    public async Task Reconcile_StartsTheSourceExactlyOnce()
    {
        // A second Start would build a second kernel session over a live one for ETW and leak a second
        // watcher for WMI, turning every process start into two kill attempts.
        var enforcer = Create();

        await enforcer.ReconcileAsync(Plan("game.exe"), CancellationToken.None);

        Assert.Equal(1, _events.StartCalls);
    }

    [Fact]
    public async Task Reconcile_SweepsProcessesThatWereAlreadyRunning()
    {
        _processes.Running.Add(new RunningProcess(1000, @"D:\games\game.exe"));
        var enforcer = Create();

        await enforcer.ReconcileAsync(Plan("game.exe"), CancellationToken.None);

        Assert.Equal([1000], _processes.Killed);
    }

    [Fact]
    public async Task Reconcile_ChangesNothingWhenTheRulesAreAlreadyArmed()
    {
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("game.exe"), CancellationToken.None);
        _processes.Killed.Clear();

        var second = await enforcer.ReconcileAsync(Plan("game.exe"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Unchanged, second.Outcome);
        Assert.Equal(1, _processes.ListCalls);
    }

    [Fact]
    public async Task Reconcile_RebuildsAFaultedSourceThoughThePlanIsUnchanged()
    {
        // A dead pump leaves every other signal saying the layer is healthy. A fault has to be examined
        // before the unchanged short-circuit, or a working layer is announced while no event is delivered.
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("game.exe"), CancellationToken.None);

        var rebuilt = new FakeProcessEvents();
        _next.Enqueue(rebuilt);
        _events.IsFaulted = true;

        var result = await enforcer.ReconcileAsync(Plan("game.exe"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.True(_events.IsDisposed);
        Assert.True(rebuilt.IsStarted);

        rebuilt.Raise(new ProcessStarted(4242, @"D:\games\game.exe"));
        Assert.Contains(4242, _processes.Killed);
    }

    [Fact]
    public async Task Reconcile_ReportsACodeWhenNoSourceCanBeStarted()
    {
        _events.FailToStartWith = new InvalidOperationException("no trace session available");
        var enforcer = Create();

        var result = await enforcer.ReconcileAsync(Plan("game.exe"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.Equal(AppEnforcer.NoEventSource, result.Detail);
    }

    [Fact]
    public async Task Probe_ReportsTheCodeAfterTheSourceFailedToStart()
    {
        _events.FailToStartWith = new InvalidOperationException("no trace session available");
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("game.exe"), CancellationToken.None);

        var availability = await enforcer.ProbeAsync(CancellationToken.None);

        Assert.False(availability.IsAvailable);
        Assert.Equal(AppEnforcer.NoEventSource, availability.Reason);
    }

    [Fact]
    public async Task Reconcile_RetriesTheSourceOnceTheBackoffHasPassed()
    {
        // One transient failure must not disable application blocking for good: a latched failure makes
        // the coordinator skip the layer, and only the layer could clear the latch.
        _events.FailToStartWith = new InvalidOperationException("no trace session available");
        var enforcer = Create();

        var failed = await enforcer.ReconcileAsync(Plan("game.exe"), CancellationToken.None);
        Assert.Equal(ReconcileOutcome.Failed, failed.Outcome);
        Assert.False((await enforcer.ProbeAsync(CancellationToken.None)).IsAvailable);

        _clock.Advance(AppEnforcer.RetryInterval + TimeSpan.FromSeconds(1));
        _events.FailToStartWith = null;

        Assert.True((await enforcer.ProbeAsync(CancellationToken.None)).IsAvailable);

        var retried = await enforcer.ReconcileAsync(Plan("game.exe"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Changed, retried.Outcome);
        _events.Raise(new ProcessStarted(4242, @"D:\games\game.exe"));
        Assert.Contains(4242, _processes.Killed);
    }

    [Fact]
    public async Task Reconcile_RetriesAfterARebuildOfAFaultedSourceFailed()
    {
        // A fault drops the source and the rebuild fails, so the armed rules have no source behind them.
        // The enforcer must give up that belief, or the unchanged short-circuit hides the failure for good.
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("game.exe"), CancellationToken.None);

        _events.IsFaulted = true;
        _next.Enqueue(new FakeProcessEvents
        {
            FailToStartWith = new InvalidOperationException("no trace session available"),
        });

        var failed = await enforcer.ReconcileAsync(Plan("game.exe"), CancellationToken.None);
        Assert.Equal(ReconcileOutcome.Failed, failed.Outcome);

        _clock.Advance(AppEnforcer.RetryInterval + TimeSpan.FromSeconds(1));
        var recovered = new FakeProcessEvents();
        _next.Enqueue(recovered);

        var result = await enforcer.ReconcileAsync(Plan("game.exe"), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Changed, result.Outcome);
        Assert.True(recovered.IsStarted);

        recovered.Raise(new ProcessStarted(4242, @"D:\games\game.exe"));
        Assert.Contains(4242, _processes.Killed);
    }

    [Fact]
    public async Task Reconcile_DisarmsWhenThePlanCarriesNoApplications()
    {
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("game.exe"), CancellationToken.None);

        await enforcer.ReconcileAsync(EnforcementPlan.Empty, CancellationToken.None);
        _processes.Killed.Clear();
        _events.Raise(new ProcessStarted(4242, @"D:\games\game.exe"));

        Assert.Empty(_processes.Killed);
        // Nothing is watched any more, and a kernel trace session left running costs buffers and a pump thread.
        Assert.True(_events.IsDisposed);
    }

    [Fact]
    public async Task Clear_DisarmsAndStopsTheSource()
    {
        var enforcer = Create();
        await enforcer.ReconcileAsync(Plan("game.exe"), CancellationToken.None);

        await enforcer.ClearAsync(CancellationToken.None);

        Assert.True(_events.IsDisposed);
        _events.Raise(new ProcessStarted(4242, @"D:\games\game.exe"));
        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public async Task Clear_IsSafeWhenNothingWasEverStarted()
    {
        await Create().ClearAsync(CancellationToken.None);
    }
}
