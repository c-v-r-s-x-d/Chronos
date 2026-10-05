using Chronos.Core.Enforcement;
using Chronos.Ipc;
using Chronos.Core.Rules;
using Chronos.Core.Sessions;
using Chronos.Service.Configuration;
using Chronos.Service.Ipc;
using Chronos.Service.Reconciliation;
using Chronos.Service.Rules;
using Chronos.Service.Sessions;
using Chronos.Service.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

public sealed class ReconcileRunnerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteRule Site = new("example.com", includeSubdomains: true);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceTestClock _clock = new(Start);
    private readonly EventBus _events = new();

    private readonly LayerStatusRegistry _layers;

    public ReconcileRunnerTests() => _layers = new LayerStatusRegistry(_clock);

    private (ReconcileRunner Runner, SessionEngine Engine, StateStore State, RecordingEnforcer Enforcer) Build(
        ILogger<ReconcileRunner>? logger = null)
    {
        var paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();

        var state = new StateStore(paths, NullLogger<StateStore>.Instance);
        var engine = new SessionEngine(_clock, new WindowsProtectedAppPolicy());
        var enforcer = new RecordingEnforcer("test");
        var coordinator = new ReconcileCoordinator([enforcer]);
        var runner = new ReconcileRunner(
            engine,
            coordinator,
            _layers,
            state,
            new SessionGate(),
            _events,
            () => Snapshot(engine),
            logger ?? NullLogger<ReconcileRunner>.Instance);

        return (runner, engine, state, enforcer);
    }

    /// <summary>Only the field the event tests look at; the runner never inspects the rest.</summary>
    private StatusPayload Snapshot(SessionEngine engine) =>
        new(engine.State.ToString(), _clock.UtcNow, null, null, null, 0, [], [], []);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task RunOnce_WithNoSession_AppliesAnEmptyPlan()
    {
        var (runner, _, _, enforcer) = Build();

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Single(enforcer.ReconciledPlans);
        Assert.True(enforcer.ReconciledPlans[0].IsEmpty);
    }

    [Fact]
    public async Task RunOnce_WithActiveSession_AppliesItsRulesAndPersistsState()
    {
        var (runner, engine, state, enforcer) = Build();
        engine.StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(2), TimeSpan.FromMinutes(5));

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Contains(Site, enforcer.ReconciledPlans[0].Sites);
        Assert.NotNull(state.Load());
    }

    [Fact]
    public async Task RunOnce_PersistsStateBeforeApplyingIt()
    {
        var paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();

        var state = new StateStore(paths, NullLogger<StateStore>.Instance);
        var engine = new SessionEngine(_clock, new WindowsProtectedAppPolicy());
        var observer = new StateObservingEnforcer(() => File.Exists(paths.StateFile));
        var runner = new ReconcileRunner(
            engine,
            new ReconcileCoordinator([observer]),
            _layers,
            state,
            new SessionGate(),
            _events,
            TestStatus.Blank,
            NullLogger<ReconcileRunner>.Instance);

        engine.StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(2), TimeSpan.FromMinutes(5));
        Assert.False(File.Exists(paths.StateFile));

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.True(
            observer.StateFileExistedDuringReconcile,
            "State must be persisted before the plan reaches the enforcers, so a crash between the two "
            + "leaves a record the next pass can act on.");
    }

    [Fact]
    public async Task RunOnce_EndsAnExpiredSessionAndClearsEveryLayer()
    {
        var (runner, engine, state, enforcer) = Build();
        engine.StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(1), TimeSpan.FromMinutes(5));
        await runner.RunOnceAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromHours(1));
        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(SessionState.Idle, engine.State);
        Assert.Equal(1, enforcer.ClearCalls);
        Assert.Null(state.Load());
    }

    [Fact]
    public async Task RunOnce_KeepsBlockingWhileAnUnlockIsPending()
    {
        var (runner, engine, _, enforcer) = Build();
        engine.StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(2), TimeSpan.FromMinutes(10));
        engine.RequestUnlock();

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Contains(Site, enforcer.ReconciledPlans[^1].Sites);
        Assert.Equal(0, enforcer.ClearCalls);
    }

    [Fact]
    public async Task RunOnce_AppliesTheSamePlanOnEveryPassOfOneSession()
    {
        var (runner, engine, _, enforcer) = Build();
        engine.StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(2), TimeSpan.FromMinutes(5));

        await runner.RunOnceAsync(CancellationToken.None);
        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, enforcer.ReconciledPlans.Count);
        Assert.Equal(enforcer.ReconciledPlans[0].SessionId, enforcer.ReconciledPlans[1].SessionId);
    }

    [Fact]
    public async Task RunOnce_ReturnsResultsForLogging()
    {
        var (runner, _, _, _) = Build();

        var results = await runner.RunOnceAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("test", results[0].EnforcerName);
    }

    [Fact]
    public async Task RunOnce_RecordsWhatThePassFoundOutAboutEachLayer()
    {
        var (runner, _, _, enforcer) = Build();
        enforcer.Availability = EnforcerAvailability.Unavailable("wfp.engine-unavailable");

        await runner.RunOnceAsync(CancellationToken.None);

        var layer = Assert.Single(_layers.Current());
        Assert.Equal("test", layer.Name);
        Assert.False(layer.IsAvailable);
        Assert.Equal("wfp.engine-unavailable", layer.ReasonCode);
    }

    [Fact]
    public async Task RunOnce_KeepsTheClearOfAnEndedSessionOutOfTheRegistry()
    {
        // Clearing does not probe: every layer is cleared whether or not it can enforce. Recording that would report a layer available on a pass that never asked.
        var (runner, engine, _, enforcer) = Build();
        engine.StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(1), TimeSpan.FromMinutes(5));
        enforcer.Availability = EnforcerAvailability.Unavailable("wfp.engine-unavailable");
        await runner.RunOnceAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromHours(1));
        await runner.RunOnceAsync(CancellationToken.None);

        Assert.False(Assert.Single(_layers.Current()).IsAvailable);
    }

    [Fact]
    public async Task RunOnce_RecordsThePassBeforeItIsWrittenToTheLog()
    {
        // A journal that cannot be written must not make the settings screen lose what the pass found.
        var paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();

        var enforcer = new RecordingEnforcer("test")
        {
            Availability = EnforcerAvailability.Unavailable("wfp.engine-unavailable"),
        };
        var runner = new ReconcileRunner(
            new SessionEngine(_clock, new WindowsProtectedAppPolicy()),
            new ReconcileCoordinator([enforcer]),
            _layers,
            new StateStore(paths, NullLogger<StateStore>.Instance),
            new SessionGate(),
            _events,
            () => Snapshot(new SessionEngine(_clock, new WindowsProtectedAppPolicy())),
            new ThrowingLogger());

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunOnceAsync(CancellationToken.None));

        Assert.Equal("wfp.engine-unavailable", Assert.Single(_layers.Current()).ReasonCode);
    }

    [Fact]
    public async Task RunOnce_PublishesNothingWhenThePassChangedNoState()
    {
        var (runner, engine, _, _) = Build();
        engine.StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(2), TimeSpan.FromMinutes(5));
        using var subscription = _events.Subscribe(out var published);

        await runner.RunOnceAsync(CancellationToken.None);
        await runner.RunOnceAsync(CancellationToken.None);
        await runner.RunOnceAsync(CancellationToken.None);

        // The loop comes round every fifteen seconds; an event per pass would fire constantly on an idle machine.
        Assert.False(published.TryRead(out _));
    }

    [Fact]
    public async Task RunOnce_PublishesTheNewStatusWhenTheSessionRanOutDuringThePass()
    {
        var (runner, engine, _, _) = Build();
        engine.StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(1), TimeSpan.FromMinutes(5));
        await runner.RunOnceAsync(CancellationToken.None);

        using var subscription = _events.Subscribe(out var published);
        _clock.Advance(TimeSpan.FromHours(1));
        await runner.RunOnceAsync(CancellationToken.None);

        // Nobody asked for this. It is the only way an interface learns that the session it was
        // counting down finished.
        Assert.True(published.TryRead(out var one));
        Assert.Equal(IpcEventKind.StatusChanged, one!.Kind);
        Assert.Equal(nameof(SessionState.Idle), one.Status!.State);
        Assert.False(published.TryRead(out _));
    }

    [Fact]
    public async Task RunOnce_PublishesTheNewStatusWhenAPendingUnlockCameDue()
    {
        var (runner, engine, _, _) = Build();
        engine.StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(4), TimeSpan.FromMinutes(30));
        engine.RequestUnlock();
        await runner.RunOnceAsync(CancellationToken.None);

        using var subscription = _events.Subscribe(out var published);
        _clock.Advance(TimeSpan.FromMinutes(31));
        await runner.RunOnceAsync(CancellationToken.None);

        Assert.True(published.TryRead(out var one));
        Assert.Equal(nameof(SessionState.Idle), one!.Status!.State);
    }

    [Fact]
    public async Task RunOnce_SaysAnUnavailableLayerOnceWhileTheReasonStaysTheSame()
    {
        var log = new CapturingLogger<ReconcileRunner>();
        var (runner, _, _, enforcer) = Build(log);
        enforcer.Availability = EnforcerAvailability.Unavailable("dns.port-busy");

        await runner.RunOnceAsync(CancellationToken.None);
        await runner.RunOnceAsync(CancellationToken.None);
        await runner.RunOnceAsync(CancellationToken.None);

        // The loop comes round every fifteen seconds; a pass with nothing new says nothing above Debug.
        var said = Assert.Single(log.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains("dns.port-busy", said.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnce_KeepsARepeatedReasonAtDebugAndDoesNotDropIt()
    {
        var log = new CapturingLogger<ReconcileRunner>();
        var (runner, _, _, enforcer) = Build(log);
        enforcer.Availability = EnforcerAvailability.Unavailable("dns.port-busy");

        await runner.RunOnceAsync(CancellationToken.None);
        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Single(
            log.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("dns.port-busy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunOnce_SaysAgainWhenTheReasonChanges()
    {
        var log = new CapturingLogger<ReconcileRunner>();
        var (runner, _, _, enforcer) = Build(log);
        enforcer.Availability = EnforcerAvailability.Unavailable("dns.port-busy");
        await runner.RunOnceAsync(CancellationToken.None);

        enforcer.Availability = EnforcerAvailability.Unavailable("dns.other");
        await runner.RunOnceAsync(CancellationToken.None);

        var said = log.Entries.Where(e => e.Level == LogLevel.Information).ToList();
        Assert.Equal(2, said.Count);
        Assert.Contains("dns.other", said[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnce_SaysWhenTheLayerIsAvailableAgainAndOnlyOnce()
    {
        var log = new CapturingLogger<ReconcileRunner>();
        var (runner, _, _, enforcer) = Build(log);
        enforcer.Availability = EnforcerAvailability.Unavailable("dns.port-busy");
        await runner.RunOnceAsync(CancellationToken.None);
        log.Clear();

        enforcer.Availability = EnforcerAvailability.Available;
        await runner.RunOnceAsync(CancellationToken.None);
        await runner.RunOnceAsync(CancellationToken.None);

        var said = Assert.Single(
            log.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("available again", StringComparison.Ordinal));
        Assert.Contains("test", said.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnce_SaysWhenTheLayerIsAvailableAgainOnAPassThatChangedNothing()
    {
        // An idle pass - no session, nothing to apply - answers Unchanged, and that is the pass a
        // freed port is most often noticed on.
        var log = new CapturingLogger<ReconcileRunner>();
        var (runner, _, _, enforcer) = Build(log);
        enforcer.Availability = EnforcerAvailability.Unavailable("dns.port-busy");
        await runner.RunOnceAsync(CancellationToken.None);
        log.Clear();

        enforcer.Availability = EnforcerAvailability.Available;
        enforcer.ReportsNoChange = true;
        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Single(
            log.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("available again", StringComparison.Ordinal));

        // And the reason is forgotten, so the next time it is news again.
        enforcer.Availability = EnforcerAvailability.Unavailable("dns.port-busy");
        log.Clear();
        await runner.RunOnceAsync(CancellationToken.None);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task RunOnce_SaysTheSameReasonAgainAfterTheLayerCameBack()
    {
        var log = new CapturingLogger<ReconcileRunner>();
        var (runner, _, _, enforcer) = Build(log);
        enforcer.Availability = EnforcerAvailability.Unavailable("dns.port-busy");
        await runner.RunOnceAsync(CancellationToken.None);
        enforcer.Availability = EnforcerAvailability.Available;
        await runner.RunOnceAsync(CancellationToken.None);
        log.Clear();

        enforcer.Availability = EnforcerAvailability.Unavailable("dns.port-busy");
        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Single(
            log.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunOnce_SaysNothingAboutALayerThatWasNeverUnavailable()
    {
        var log = new CapturingLogger<ReconcileRunner>();
        var (runner, _, _, _) = Build(log);

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("available again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunOnce_KeepsTheLayersApart()
    {
        var log = new CapturingLogger<ReconcileRunner>();
        var paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();
        var first = new RecordingEnforcer("first") { Availability = EnforcerAvailability.Unavailable("x.busy") };
        var second = new RecordingEnforcer("second") { Availability = EnforcerAvailability.Unavailable("x.busy") };
        var runner = new ReconcileRunner(
            new SessionEngine(_clock, new WindowsProtectedAppPolicy()),
            new ReconcileCoordinator([first, second]),
            _layers,
            new StateStore(paths, NullLogger<StateStore>.Instance),
            new SessionGate(),
            _events,
            TestStatus.Blank,
            log);

        await runner.RunOnceAsync(CancellationToken.None);
        await runner.RunOnceAsync(CancellationToken.None);

        // The same reason on two layers is two pieces of news, not one.
        Assert.Equal(2, log.Entries.Count(e => e.Level == LogLevel.Information));
    }

    [Fact]
    public async Task RunOnce_DoesNotTakeTheClearOfAnEndedSessionForTheLayerComingBack()
    {
        var log = new CapturingLogger<ReconcileRunner>();
        var (runner, engine, _, enforcer) = Build(log);
        engine.StartSession(BlockList.Empty.WithSite(Site), TimeSpan.FromHours(1), TimeSpan.FromMinutes(5));
        enforcer.Availability = EnforcerAvailability.Unavailable("dns.port-busy");
        await runner.RunOnceAsync(CancellationToken.None);
        log.Clear();

        // Clearing does not probe, so it says nothing about whether the layer can enforce.
        _clock.Advance(TimeSpan.FromHours(1));
        await runner.RunOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("available again", StringComparison.Ordinal));
    }

    private sealed class ThrowingLogger : ILogger<ReconcileRunner>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("The log sink is gone.");
    }
}
