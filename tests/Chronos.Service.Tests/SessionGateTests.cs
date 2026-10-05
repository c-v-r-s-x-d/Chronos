using System.Collections.Concurrent;
using Chronos.Core.Enforcement;
using Chronos.Core.Rules;
using Chronos.Core.Sessions;
using Chronos.Ipc;
using Chronos.Service.Configuration;
using Chronos.Service.Ipc;
using Chronos.Service.Reconciliation;
using Chronos.Service.Rules;
using Chronos.Service.Sessions;
using Chronos.Service.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

public sealed class SessionGateTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteRule Site = new("example.com", includeSubdomains: true);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceTestClock _clock = new(Start);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static IpcRequest Request(string command) => new()
    {
        ProtocolVersion = IpcProtocol.Version,
        Command = command,
    };

    // SessionEngine is not thread-safe: without SessionGate, Tick() can read State as UnlockPending, lose a race to a concurrent CancelUnlock
    // that nulls Session.Unlock, and throw a NullReferenceException on Session.Unlock!.EffectiveAt.
    // The window is a single instruction gap, so eight command threads hammer RequestUnlock/CancelUnlock; one rarely lands in it within 200 passes.
    [Fact]
    public async Task ConcurrentCommandsAndReconcilePassesDoNotCorruptTheEngine()
    {
        var paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();

        var config = new ConfigStore(paths, NullLogger<ConfigStore>.Instance);
        var state = new StateStore(paths, NullLogger<StateStore>.Instance);
        var engine = new SessionEngine(_clock, new WindowsProtectedAppPolicy());
        var gate = new SessionGate();

        var coordinator = new ReconcileCoordinator([]);
        var layers = new LayerStatusRegistry(_clock);
        var runner = new ReconcileRunner(
            engine,
            coordinator,
            layers,
            state,
            gate,
            new EventBus(),
            TestStatus.Blank,
            NullLogger<ReconcileRunner>.Instance);
        var scheduler = new ReconcileScheduler(runner, NullLogger<ReconcileScheduler>.Instance);
        var dispatcher = new CommandDispatcher(
            engine,
            config,
            scheduler,
            layers,
            gate,
            _clock,
            new WindowsProtectedAppPolicy(),
            new EventBus(),
            NullLogger<CommandDispatcher>.Instance);

        // A cool-down of half an hour, with a clock that never advances, so the session never ends by
        // elapsed time; the only way it could end during this test is the race being guarded against.
        var start = engine.StartSession(
            BlockList.Empty.WithSite(Site),
            TimeSpan.FromHours(2),
            TimeSpan.FromMinutes(30));
        Assert.True(start.Accepted);

        var stopping = new CancellationTokenSource();
        var failures = new ConcurrentBag<Exception>();

        var reconcileLoop = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 200; i++)
                {
                    await runner.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            finally
            {
                // The command threads have nothing left to race against once the reconcile
                // loop is done, so stop them rather than let them spin forever.
                stopping.Cancel();
            }
        });

        var commandLoops = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                try
                {
                    while (!stopping.IsCancellationRequested)
                    {
                        dispatcher.Dispatch(Request("RequestUnlock"));
                        dispatcher.Dispatch(Request("CancelUnlock"));
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                    stopping.Cancel();
                }
            }))
            .ToArray();

        await Task.WhenAll(commandLoops.Append(reconcileLoop));

        Assert.Empty(failures);
    }
}
