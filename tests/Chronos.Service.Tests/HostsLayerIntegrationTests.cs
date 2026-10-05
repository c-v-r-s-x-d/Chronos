using Chronos.Core.Enforcement;
using Chronos.Core.Rules;
using Chronos.Core.Sessions;
using Chronos.Core.Time;
using Chronos.Service.Configuration;
using Chronos.Service.Ipc;
using Chronos.Service.Reconciliation;
using Chronos.Service.Rules;
using Chronos.Service.Sessions;
using Chronos.Service.Sites;
using Chronos.Service.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

public sealed class HostsLayerIntegrationTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceTestClock _clock = new(Start);

    private string HostsPath => Path.Combine(_root, "hosts");

    private (ReconcileRunner Runner, SessionEngine Engine) Build()
    {
        var paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();

        var enforcer = new HostsEnforcer(
            new HostsFile(HostsPath), new FakeDnsCache(), NullLogger<HostsEnforcer>.Instance);
        var engine = new SessionEngine(_clock, new WindowsProtectedAppPolicy());
        var runner = new ReconcileRunner(
            engine,
            new ReconcileCoordinator([enforcer]),
            new LayerStatusRegistry(SystemClock.Instance),
            new StateStore(paths, NullLogger<StateStore>.Instance),
            new SessionGate(),
            new EventBus(),
            TestStatus.Blank,
            NullLogger<ReconcileRunner>.Instance);

        return (runner, engine);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ABlockLeftOverFromAnUnconfirmedCleanupIsRemovedByTheNextCycle()
    {
        // ReconcileRunner confirms the cleanup even when a layer failed to clear, so a block can
        // outlive the session record. What saves it is the layer itself: an idle engine has an
        // empty plan, and the next cycle takes back whatever block it finds.
        var (runner, engine) = Build();
        File.WriteAllText(HostsPath, HostsBlock.Write("127.0.0.1 localhost\r\n", ["0.0.0.0 example.com"]));

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(SessionState.Idle, engine.State);
        Assert.Equal("127.0.0.1 localhost\r\n", File.ReadAllText(HostsPath));
    }

    [Fact]
    public async Task ASessionBlocksItsDomainsAndAnExpiredSessionTakesThemBack()
    {
        var (runner, engine) = Build();
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        engine.StartSession(
            BlockList.Empty.WithSite(new SiteRule("example.com", includeSubdomains: true)),
            TimeSpan.FromHours(1),
            TimeSpan.FromMinutes(5));

        await runner.RunOnceAsync(CancellationToken.None);
        Assert.Contains("0.0.0.0 example.com", File.ReadAllText(HostsPath), StringComparison.Ordinal);

        _clock.Advance(TimeSpan.FromHours(1));
        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal("127.0.0.1 localhost\r\n", File.ReadAllText(HostsPath));
        Assert.Equal(SessionState.Idle, engine.State);
    }
}
