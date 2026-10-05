using Chronos.Core.Enforcement;
using Chronos.Core.Rules;
using Chronos.Core.Time;
using Chronos.Ipc;
using Chronos.Service.Configuration;
using Chronos.Service.Ipc;
using Chronos.Service.Reconciliation;
using Chronos.Service.Rules;
using Chronos.Service.Sessions;
using Chronos.Service.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.App.Tests;

/// <summary>A real service end on a pipe name of this test's own, which can be stopped and restarted on the same name.</summary>
internal sealed class ServiceHarness : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-app-tests", Guid.NewGuid().ToString("N"));

    private CancellationTokenSource? _cts;
    private Task? _running;

    public string PipeName { get; } = "chronos-app-test-" + Guid.NewGuid().ToString("N");

    /// <summary>The service's own event bus, so a test can make the real service announce a real block over the real pipe.</summary>
    public EventBus Events { get; } = new();

    /// <summary>What the configuration holds before the test does anything; tests that count rules set this empty.</summary>
    public IReadOnlyList<string> Seed { get; init; } = [KnownSite];

    public void Start()
    {
        if (_running is not null)
        {
            throw new InvalidOperationException("The harness is already running.");
        }

        _cts = new CancellationTokenSource();
        _running = Build().RunAsync(_cts.Token);
    }

    /// <summary>Returns once the accept loop has left, so a following start cannot race the listening instance.</summary>
    public async Task StopAsync()
    {
        if (_running is null || _cts is null)
        {
            return;
        }

        await _cts.CancelAsync();

        try
        {
            await _running;
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
        _cts = null;
        _running = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private IpcServer Build()
    {
        var paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();

        var config = new ConfigStore(paths, NullLogger<ConfigStore>.Instance);
        config.Save(new ChronosConfig { Sites = [.. Seed.Select(domain => new SiteRuleDto(domain, true))] });

        var clock = new FixedClock(Moment);
        var engine = new Chronos.Core.Sessions.SessionEngine(clock, new WindowsProtectedAppPolicy());
        var layers = new LayerStatusRegistry(clock);
        var gate = new SessionGate();
        var events = Events;

        var runner = new ReconcileRunner(
            engine,
            new ReconcileCoordinator([]),
            layers,
            new StateStore(paths, NullLogger<StateStore>.Instance),
            gate,
            events,
            BlankStatus,
            NullLogger<ReconcileRunner>.Instance);

        var dispatcher = new CommandDispatcher(
            engine,
            config,
            new ReconcileScheduler(runner, NullLogger<ReconcileScheduler>.Instance),
            layers,
            gate,
            clock,
            new WindowsProtectedAppPolicy(),
            events,
            NullLogger<CommandDispatcher>.Instance);

        // Through the seam, not the public constructor: the service's ACL grants an interactive logon session
        // ReadWrite but not CreateNewInstance, so an unelevated test gets one listening instance, and the
        // link would hold it for the whole test.
        return IpcServer.ForTests(dispatcher, events, NullLogger<IpcServer>.Instance, PipeName);
    }

    public const string KnownSite = "example.com";

    /// <summary>The service's clock, stopped. A status carrying this moment came from the service. The configured rules are no marker: StatusPayload.Sites and .Apps are empty without a session.</summary>
    public static readonly DateTimeOffset Moment = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static StatusPayload BlankStatus() =>
        new("Idle", DateTimeOffset.UnixEpoch, null, null, null, 0, [], [], []);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
