using System.Net;
using Chronos.Core.Enforcement;
using Chronos.Core.Rules;
using Chronos.Ipc;
using Chronos.Service.Apps;
using Chronos.Service.Configuration;
using Chronos.Service.Dns;
using Chronos.Service.Ipc;
using Chronos.Service.Sites;
using Chronos.Service.Wfp;
using Microsoft.Extensions.DependencyInjection;

namespace Chronos.Service.Tests;

/// <summary>Layer L2 in the service's composition. Nothing here probes, reconciles or clears it: resolving the instance touches no port, netsh or HKLM.</summary>
public sealed class ServiceCompositionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));

    public ServiceCompositionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TheContainerHandsOutTheDnsLayer()
    {
        using var provider = Build();

        Assert.NotNull(provider.GetRequiredService<DnsEnforcer>());
    }

    [Fact]
    public void TheCoordinatorRunsFourLayersWithDnsLast()
    {
        using var provider = Build();

        var layers = provider.GetRequiredService<ReconcileCoordinator>().Enforcers;

        Assert.Equal(
            [HostsEnforcer.LayerName, AppEnforcer.LayerName, WfpEnforcer.LayerName, DnsEnforcer.LayerName],
            layers.Select(layer => layer.Name));
    }

    [Fact]
    public void TheCoordinatorRunsTheSameDnsLayerTheContainerHandsOut()
    {
        using var provider = Build();

        // Two instances would be two owners of one port and one backup.
        Assert.Same(
            provider.GetRequiredService<DnsEnforcer>(),
            provider.GetRequiredService<ReconcileCoordinator>().Enforcers[3]);
    }

    [Fact]
    public void TheServiceTakesTheProductsSettingsLock()
    {
        // The name the command line opens; resolving it creates no mutex.
        using var provider = Build();

        var settingsLock = Assert.IsType<NamedDnsSettingsLock>(provider.GetRequiredService<IDnsSettingsLock>());
        Assert.Equal(NamedDnsSettingsLock.DefaultName, settingsLock.Name);
    }

    [Fact]
    public void TheServiceLayerChecksItselfWithARealQuery()
    {
        using var provider = Build();

        var check = Assert.IsType<LoopbackDnsSelfCheck>(provider.GetRequiredService<IDnsSelfCheck>());
        Assert.Same(check, provider.GetRequiredService<DnsEnforcer>().SelfCheck);
        Assert.Equal(TimeSpan.FromSeconds(1), check.Wait);
    }

    [Fact]
    public void TheServiceLayerAsksForPort53()
    {
        using var provider = Build();

        Assert.Equal(LoopbackDnsServer.DnsPort, provider.GetRequiredService<DnsEnforcer>().Port);
    }

    [Fact]
    public async Task TheHandlerForwardsThroughTheSwitchTheLayerFills()
    {
        using var provider = Build();
        var recording = new CountingForwarder();

        provider.GetRequiredService<SwappableDnsForwarder>().Use(recording);
        await provider.GetRequiredService<DnsRequestHandler>().AnswerAsync(
            ExampleOrgQuery, DnsTransportKind.Udp, default);

        Assert.Equal(1, recording.Calls);
    }

    [Fact]
    public async Task ABlockedAttemptTheHandlerAnswersReachesTheNotices()
    {
        var sent = new List<string>();
        using var provider = Build(services => services.AddSingleton(new AttemptNotices(
            sent.Add, new ServiceTestClock(DateTimeOffset.UnixEpoch), new CapturingLogger<AttemptNotices>())));
        var handler = provider.GetRequiredService<DnsRequestHandler>();

        handler.UseRules([new SiteRule("example.org", includeSubdomains: true)]);
        await handler.AnswerAsync(ExampleOrgQuery, DnsTransportKind.Udp, default);

        Assert.Equal(["example.org"], sent);
    }

    [Fact]
    public async Task ABlockedAttemptTheHandlerAnswersReachesTheEventBus()
    {
        // The whole service chain: handler, notices, announcer, the status the dispatcher builds.
        using var provider = Build();
        using var subscription = provider.GetRequiredService<EventBus>().Subscribe(out var events);
        var handler = provider.GetRequiredService<DnsRequestHandler>();

        handler.UseRules([new SiteRule("example.org", includeSubdomains: true)]);
        await handler.AnswerAsync(ExampleOrgQuery, DnsTransportKind.Udp, default);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var published = await events.ReadAsync(timeout.Token);

        Assert.Equal(IpcEventKind.SiteBlocked, published.Kind);
        Assert.Equal("example.org", published.Domain);
        Assert.NotNull(published.Status);
    }

    [Fact]
    public async Task TwoNamesUnderOneRuleGiveOneEventWithinTheWindow()
    {
        // One site on the list is one target, whatever the page resolves.
        using var provider = Build();
        using var subscription = provider.GetRequiredService<EventBus>().Subscribe(out var events);
        var handler = provider.GetRequiredService<DnsRequestHandler>();

        handler.UseRules([new SiteRule("example.org", includeSubdomains: true)]);
        await handler.AnswerAsync(WwwExampleOrgQuery, DnsTransportKind.Udp, default);
        await handler.AnswerAsync(ExampleOrgQuery, DnsTransportKind.Udp, default);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var published = await events.ReadAsync(timeout.Token);
        Assert.Equal("example.org", published.Domain);

        // A second announcement would already be on the pool; give it time to arrive if it exists.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(events.TryRead(out var second), $"A second event arrived: {second?.Domain}.");
    }

    [Fact]
    public void TheServiceBuildsARealForwarderFromTheServersItIsGiven()
    {
        using var provider = Build();
        var create = provider.GetRequiredService<Func<IReadOnlyList<IPAddress>, IDnsForwarder>>();

        Assert.IsType<DnsForwarder>(create([IPAddress.Parse("8.8.8.8")]));

        // The refusal the layer filters ahead of: a loopback upstream is our own socket.
        Assert.Throws<ArgumentException>(() => create([IPAddress.Loopback]));
    }

    private static byte[] ExampleOrgQuery =>
    [
        0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
        3, (byte)'o', (byte)'r', (byte)'g', 0,
        0x00, 0x01, 0x00, 0x01,
    ];

    private static byte[] WwwExampleOrgQuery =>
    [
        0x12, 0x35, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        3, (byte)'w', (byte)'w', (byte)'w',
        7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
        3, (byte)'o', (byte)'r', (byte)'g', 0,
        0x00, 0x01, 0x00, 0x01,
    ];

    private ServiceProvider Build(Action<IServiceCollection>? replace = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Service.Program.ConfigureServices(services, new ChronosPaths(_root, _root));

        // The real factory would open a persistent WFP session on the machine running the suite.
        services.AddSingleton<Func<IWfpEngine>>(_ => () => new FakeWfpEngine());
        replace?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private sealed class CountingForwarder : IDnsForwarder
    {
        public int Calls { get; private set; }

        public Task<byte[]?> ForwardAsync(byte[] query, bool overTcp, CancellationToken ct)
        {
            Calls++;

            return Task.FromResult<byte[]?>(null);
        }
    }
}
