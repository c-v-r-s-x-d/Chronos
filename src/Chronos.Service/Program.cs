using System.Net;
using Chronos.Core.Enforcement;
using Chronos.Core.Rules;
using Chronos.Core.Sessions;
using Chronos.Core.Time;
using Chronos.Ipc;
using Chronos.Service.Apps;
using Chronos.Service.Configuration;
using Chronos.Service.Diagnostics;
using Chronos.Service.Dns;
using Chronos.Service.Ipc;
using Chronos.Service.Reconciliation;
using Chronos.Service.Rules;
using Chronos.Service.Sessions;
using Chronos.Service.Sites;
using Chronos.Service.Setup;
using Chronos.Service.State;
using Chronos.Service.Wfp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Chronos.Service;

public static class Program
{
    public static void Main(string[] args)
    {
        var eventLog = new WindowsEventLog();

        try
        {
            RunReportingAFailureToStart(() => Run(args, eventLog), eventLog);
        }
        finally
        {
            // Outside the guard: it is unit-tested, and closing Serilog's global logger there would
            // break other tests. It also keeps log lines from a failed composition.
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Runs the service and, if it fails to start, writes to the Windows event log. The Serilog
    /// sink cannot cover this because the failure can precede the logger. Rethrown so the service
    /// manager sees the failure and its restart actions fire.
    /// </summary>
    internal static void RunReportingAFailureToStart(Action run, ISystemEventLog eventLog)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(eventLog);

        try
        {
            run();
        }
        catch (Exception exception)
        {
            // ToString, not Message: Message is localised, and ToString carries the inner exception.
            eventLog.Write(
                SystemEventLevel.Error,
                ChronosEvents.ServiceCritical,
                "The Chronos service could not start: " + exception.ToString());

            throw;
        }
    }

    private static void Run(string[] args, ISystemEventLog eventLog)
    {
        var paths = ChronosPaths.Default;
        CreateDataDirectory(paths);

        var builder = Host.CreateApplicationBuilder(args);

        var config = new ConfigStore(
            paths,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigStore>.Instance);
        ServiceLogging.Configure(paths, config.Load().VerboseLogging, eventLog);

        builder.Services.AddSerilog();
        builder.Services.AddWindowsService(NameTheHost);

        // Replaces the lifetime AddWindowsService registered. Only when running as a service:
        // under a console a ServiceBase has no dispatcher and the host would never start.
        if (WindowsServiceHelpers.IsWindowsService())
        {
            builder.Services.AddSingleton<IHostLifetime, PreshutdownLifetime>();
        }

        ConfigureServices(builder.Services, paths);

        builder.Build().Run();
    }

    /// <summary>
    /// Sets the name the host answers the service dispatcher with. It must match the name
    /// <see cref="ServiceDefinition"/> registers, or the service never reaches Running.
    /// </summary>
    internal static void NameTheHost(WindowsServiceLifetimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ServiceName = ServiceDefinition.ChronosServiceName;
    }

    /// <summary>
    /// Creates the data directory with restricted rights. Both <c>install</c> and the service
    /// create it; a bare CreateDirectory inherits from %ProgramData%, which lets BUILTIN\Users
    /// write state.json. This matters after a failed <c>uninstall --purge</c> leaves a running
    /// service that recreates the directory.
    /// </summary>
    internal static void CreateDataDirectory(ChronosPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        DataDirectory.Create(paths.DataDirectory);

        // After it, so the log directory inherits the restricted list.
        paths.EnsureDataDirectoryExists();
    }

    /// <summary>The service composition, separate from <see cref="Main"/> so tests can build it.</summary>
    public static void ConfigureServices(IServiceCollection services, ChronosPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton(paths);
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<IProtectedAppPolicy, WindowsProtectedAppPolicy>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<StateStore>();
        services.AddSingleton<SessionGate>();

        services.AddSingleton(provider =>
        {
            var store = provider.GetRequiredService<StateStore>();
            var clock = provider.GetRequiredService<IClock>();
            var policy = provider.GetRequiredService<IProtectedAppPolicy>();
            var restored = store.Load();

            return restored is null
                ? new SessionEngine(clock, policy)
                : SessionEngine.Restore(clock, policy, restored);
        });

        services.AddSingleton<IDnsCache, WindowsDnsCache>();
        services.AddSingleton(_ => new HostsFile(HostsFile.SystemPath));
        services.AddSingleton<HostsEnforcer>();

        services.AddSingleton(_ => new DeviceMap(DeviceMap.ReadWindowsDevices));
        services.AddSingleton<IProcessControl, WindowsProcessControl>();
        services.AddSingleton<AppWatchdog>();
        services.AddSingleton(provider =>
        {
            var loggers = provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();

            return new AppEnforcer(
                provider.GetRequiredService<AppWatchdog>(),
                onStarted => ProcessEventsFactory.Create(
                    onStarted,
                    () => new EtwProcessEvents(
                        provider.GetRequiredService<DeviceMap>(),
                        loggers.CreateLogger<EtwProcessEvents>()),
                    () => new WmiProcessEvents(),
                    loggers.CreateLogger(typeof(ProcessEventsFactory))),
                provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AppEnforcer>>());
        });

        services.AddSingleton<ProtectedAddresses>();
        services.AddSingleton<IDnsTransport, UdpDnsTransport>();
        services.AddSingleton<IAddressResolver>(provider => new ExternalAddressResolver(
            provider.GetRequiredService<IDnsTransport>(),
            ExternalAddressResolver.DefaultServers,
            ExternalAddressResolver.DefaultTimeout));

        // Persistent, so a block survives a reboot and a service killed mid-session.
        services.AddSingleton<Func<IWfpEngine>>(provider => () =>
        {
            var engine = new WfpEngine(
                persistent: true,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WfpEngine>>());
            engine.Open();

            return engine;
        });

        services.AddSingleton(provider => new WfpEnforcer(
            // Re-read on each call, not captured: the singleton is built once per service start.
            () => provider.GetRequiredService<ConfigStore>().Load().WfpEnabled,
            provider.GetRequiredService<Func<IWfpEngine>>(),
            provider.GetRequiredService<IAddressResolver>(),
            provider.GetRequiredService<ProtectedAddresses>(),
            WfpEnforcer.SystemInfrastructureAddresses,
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WfpEnforcer>>()));

        AddDnsLayer(services);

        // Order affects the log only; layers are independent.
        services.AddSingleton(provider => new ReconcileCoordinator(
        [
            provider.GetRequiredService<HostsEnforcer>(),
            provider.GetRequiredService<AppEnforcer>(),
            provider.GetRequiredService<WfpEnforcer>(),
            provider.GetRequiredService<DnsEnforcer>(),
        ]));
        // One shared instance: the reconcile loop writes it, status requests read it.
        services.AddSingleton<LayerStatusRegistry>();
        services.AddSingleton<ReconcileRunner>();
        services.AddSingleton<ReconcileScheduler>();
        services.AddSingleton<CommandDispatcher>();

        services.AddSingleton<EventBus>();

        // Resolved lazily: the dispatcher reaches the reconcile loop through the scheduler, so
        // injecting it directly would be a circular dependency.
        services.AddSingleton<Func<StatusPayload>>(provider =>
            () => provider.GetRequiredService<CommandDispatcher>().Snapshot());
        services.AddSingleton(provider => new IpcServer(
            provider.GetRequiredService<CommandDispatcher>(),
            provider.GetRequiredService<EventBus>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<IpcServer>>()));

        services.AddHostedService<ChronosWorker>();
    }

    /// <summary>The DNS layer and its parts. Resolving any of it touches neither port 53 nor netsh.</summary>
    private static void AddDnsLayer(IServiceCollection services)
    {
        // Runs on the DNS answer path, so it neither throws nor waits.
        services.AddSingleton(provider => new SiteBlockAnnouncer(
            provider.GetRequiredService<EventBus>(),
            provider.GetRequiredService<Func<StatusPayload>>(),
            provider.GetRequiredService<ILogger<SiteBlockAnnouncer>>()));
        services.AddSingleton(provider => new AttemptNotices(
            provider.GetRequiredService<SiteBlockAnnouncer>().Announce,
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<ILogger<AttemptNotices>>()));

        // The upstream servers come from the DNS backup, so the layer fills the forwarder in later.
        services.AddSingleton<SwappableDnsForwarder>();
        services.AddSingleton(provider => new DnsRequestHandler(
            provider.GetRequiredService<SwappableDnsForwarder>(),
            new DnsResponseCache(provider.GetRequiredService<IClock>()),
            provider.GetRequiredService<AttemptNotices>().Notify,
            provider.GetRequiredService<ILogger<DnsRequestHandler>>()));

        services.AddSingleton<Func<IReadOnlyList<IPAddress>, IDnsForwarder>>(provider => servers => new DnsForwarder(
            provider.GetRequiredService<IDnsTransport>(),
            servers,
            DnsForwarder.DefaultTimeout,
            provider.GetRequiredService<ILogger<DnsForwarder>>()));

        services.AddSingleton<IInterfaceDns, InterfaceDnsRegistry>();
        services.AddSingleton<INetworkDnsControl, NetshDnsControl>();
        services.AddSingleton<IBackupMirror, RegistryBackupMirror>();
        services.AddSingleton<DnsBackupStore>();
        services.AddSingleton<IDnsSettingsLock>(_ => new NamedDnsSettingsLock());
        services.AddSingleton<IDnsSelfCheck, LoopbackDnsSelfCheck>();

        services.AddSingleton(provider =>
        {
            var handler = provider.GetRequiredService<DnsRequestHandler>();
            var serverLogger = provider.GetRequiredService<ILogger<LoopbackDnsServer>>();

            return new DnsEnforcer(
                port => new LoopbackDnsServer(handler, serverLogger, port),
                handler,
                provider.GetRequiredService<SwappableDnsForwarder>(),
                provider.GetRequiredService<Func<IReadOnlyList<IPAddress>, IDnsForwarder>>(),
                provider.GetRequiredService<IInterfaceDns>(),
                provider.GetRequiredService<INetworkDnsControl>(),
                provider.GetRequiredService<DnsBackupStore>(),
                provider.GetRequiredService<IDnsCache>(),
                provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<ILogger<DnsEnforcer>>(),
                provider.GetRequiredService<IDnsSettingsLock>(),
                provider.GetRequiredService<IDnsSelfCheck>());
        });
    }
}
