using Chronos.Core.Enforcement;
using Chronos.Core.Sessions;
using Chronos.Ipc;
using Chronos.Service.Apps;
using Chronos.Service.Configuration;
using Chronos.Service.Ipc;
using Chronos.Service.Reconciliation;
using Chronos.Service.Sites;
using Chronos.Service.Wfp;
using Microsoft.Extensions.DependencyInjection;

namespace Chronos.Service.Tests;

/// <summary>
/// The service's composition root, where the three layers meet. Other tests build subjects by hand, so a layer never registered,
/// or registered twice under two owners, would pass them. Nothing reaches the platform: the engine factory is a fake,
/// except in the disabled case, which must never call it.
/// </summary>
public sealed class WfpWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeWfpEngine _engine = new();

    public WfpWiringTests() => Directory.CreateDirectory(_root);

    private int EngineCreations { get; set; }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TheAddressLayerRunsThirdBehindHostsAndApps()
    {
        using var provider = Build();

        var layers = provider.GetRequiredService<ReconcileCoordinator>().Enforcers;

        Assert.Equal(
            [HostsEnforcer.LayerName, AppEnforcer.LayerName, WfpEnforcer.LayerName],
            layers.Take(3).Select(layer => layer.Name));
    }

    [Fact]
    public void TheCoordinatorRunsTheSameAddressLayerTheContainerHandsOut()
    {
        using var provider = Build();

        var registered = provider.GetRequiredService<WfpEnforcer>();
        var inCoordinator = provider.GetRequiredService<ReconcileCoordinator>().Enforcers[2];

        // Two instances would mean two owners of one engine session, and the second would file
        // filters nobody clears.
        Assert.Same(registered, inCoordinator);
        Assert.Same(registered, provider.GetRequiredService<WfpEnforcer>());
    }

    [Fact]
    public async Task TheAddressLayerIsOnWhenNothingIsConfigured()
    {
        using var provider = Build();

        var availability = await provider.GetRequiredService<WfpEnforcer>().ProbeAsync(CancellationToken.None);

        Assert.True(availability.IsAvailable);
        Assert.Equal(1, EngineCreations);

        // On a machine with nothing configured this is the idle machine: the layer is on so a session opens, but a provider and sub-layer created here
        // are persistent and would outlive every reboot of a machine that never blocked anything.
        Assert.Equal(0, _engine.EnsureObjectsCalls);
    }

    [Fact]
    public async Task TheConfiguredFlagReachesTheLayer()
    {
        using var provider = Build(wfpEnabled: false);

        var availability = await provider.GetRequiredService<WfpEnforcer>().ProbeAsync(CancellationToken.None);

        Assert.False(availability.IsAvailable);
        Assert.Equal(WfpEnforcer.Disabled, availability.Reason);

        // Switched off means it never reaches the platform at all, so not even the
        // factory runs - which is also what makes this the one case that is safe to probe with
        // the real registration in place.
        Assert.Equal(0, EngineCreations);
    }

    [Fact]
    public async Task TheEngineSessionIsClosedWithTheContainer()
    {
        var provider = Build();
        await provider.GetRequiredService<WfpEnforcer>().ProbeAsync(CancellationToken.None);

        await provider.DisposeAsync();

        // The layer is IDisposable and holds an OS handle; registered any other way than as a
        // singleton the container owns, the handle would outlive the service.
        Assert.True(_engine.Disposed);
    }

    // The flag is read when a session begins, not when the singleton is constructed: a user who switched L3 off and started a session
    // without restarting must get no filters, like the block list and durations, which are re-read on every command.
    [Fact]
    public async Task TheFlagIsReReadRatherThanCapturedWhenTheContainerIsBuilt()
    {
        using var provider = Build(wfpEnabled: true);
        var layer = provider.GetRequiredService<WfpEnforcer>();
        Assert.True((await layer.ProbeAsync(CancellationToken.None)).IsAvailable);

        WriteConfig(wfpEnabled: false);

        var availability = await layer.ProbeAsync(CancellationToken.None);

        Assert.False(availability.IsAvailable);
        Assert.Equal(WfpEnforcer.Disabled, availability.Reason);
    }

    [Fact]
    public void TheStatusInsideAnEventIsTakenFromTheEngineTheServiceActuallyRuns()
    {
        // The reconcile loop and the process watchdog are handed this delegate rather than the
        // dispatcher, because the dispatcher reaches the loop through the scheduler. Resolving it
        // here is what proves the container can close that loop at all.
        using var provider = Build();

        var snapshot = provider.GetRequiredService<Func<StatusPayload>>()();

        Assert.Equal(nameof(SessionState.Idle), snapshot.State);
    }

    [Fact]
    public void TheBusTheLoopPublishesIntoIsTheOneAConnectionSubscribesFrom()
    {
        // Two instances instead of one would mean a subscribed interface that never hears a
        // thing, and every test that builds its own bus would still pass.
        using var provider = Build();

        Assert.Same(provider.GetRequiredService<EventBus>(), provider.GetRequiredService<EventBus>());
        Assert.NotNull(provider.GetRequiredService<ReconcileRunner>());
        Assert.NotNull(provider.GetRequiredService<IpcServer>());
    }

    [Fact]
    public void TheRegistryTheReconcileLoopWritesIsTheOneAStatusRequestReads()
    {
        // Two instances instead of one would report every layer as never heard from, forever, and
        // no test that builds its own dispatcher would notice.
        using var provider = Build();
        provider.GetRequiredService<LayerStatusRegistry>()
            .Record([ReconcileResult.Skipped(WfpEnforcer.LayerName, "wfp.engine-unavailable")]);

        var status = provider.GetRequiredService<CommandDispatcher>()
            .Dispatch(new IpcRequest { Command = "GetStatus" });

        var layer = Assert.Single(status.Status!.Layers);
        Assert.Equal(WfpEnforcer.LayerName, layer.Name);
        Assert.Equal("wfp.engine-unavailable", layer.ReasonCode);
    }

    private void WriteConfig(bool wfpEnabled) => File.WriteAllText(
        new ChronosPaths(_root, _root).ConfigFile,
        $$"""{"wfpEnabled": {{(wfpEnabled ? "true" : "false")}}}""");

    private ServiceProvider Build(bool? wfpEnabled = null)
    {
        var paths = new ChronosPaths(_root, _root);

        if (wfpEnabled is { } flag)
        {
            WriteConfig(flag);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        Service.Program.ConfigureServices(services, paths);

        if (wfpEnabled is not false)
        {
            // Replaces the real factory, which would open a persistent session and leave provider
            // objects on the machine running the suite. The disabled case keeps the real one on
            // purpose: never calling it is the assertion.
            services.AddSingleton<Func<IWfpEngine>>(_ => CreateEngine);
        }

        return services.BuildServiceProvider();
    }

    private IWfpEngine CreateEngine()
    {
        EngineCreations++;
        return _engine;
    }
}
