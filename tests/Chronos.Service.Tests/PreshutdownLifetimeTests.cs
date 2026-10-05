using Chronos.Service.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chronos.Service.Tests;

/// <summary>
/// The service asks Windows for the pre-shutdown notification and uses it to stop the way a stop stops.
/// No service host or SCM is involved: the status handle and the SetServiceStatus call are constructor seams.
/// Whether the platform honours the report is a live check on an installed service.
/// </summary>
public sealed class PreshutdownLifetimeTests
{
    private static readonly IntPtr Handle = 42;

    private readonly FakeApplicationLifetime _application = new();

    private readonly CapturingLogger<PreshutdownLifetime> _logger = new();

    private readonly List<(IntPtr Handle, ServiceInterop.SERVICE_STATUS Status)> _reported = [];

    private int _error;

    private bool _stopped;

    private PreshutdownLifetime Lifetime(IntPtr handle) => new(
        new FakeEnvironment(),
        _application,
        new FakeLoggerFactory(_logger),
        Options.Create(new HostOptions()),
        Options.Create(new WindowsServiceLifetimeOptions { ServiceName = "ChronosService" }),
        () => handle,
        (reportedHandle, status) =>
        {
            _reported.Add((reportedHandle, status));
            return _error;
        },
        () => _stopped = true);

    [Fact]
    public void TheAcceptedControlsKeepEverythingServiceBaseAlreadyAccepted()
    {
        // dwControlsAccepted is a whole set that SetServiceStatus replaces, so the flag is added to what was already reported; assigning would drop SERVICE_ACCEPT_STOP.
        var updated = PreshutdownLifetime.WithPreshutdown(
            PreshutdownLifetime.AcceptStop | PreshutdownLifetime.AcceptShutdown);

        Assert.True((updated & PreshutdownLifetime.AcceptStop) != 0);
        Assert.True((updated & PreshutdownLifetime.AcceptShutdown) != 0);
        Assert.True((updated & PreshutdownLifetime.AcceptPreshutdown) != 0);
    }

    [Fact]
    public void PreshutdownIsTheControlCodeWindowsSends()
    {
        Assert.Equal(0x0F, PreshutdownLifetime.PreshutdownCommand);
    }

    [Fact]
    public void TheReportedStatusSaysTheServiceIsStillRunningAndIsNotWaitingForAnything()
    {
        // A wait hint on a settled state reads as a stuck transition, and the state must stay RUNNING: this reports one more accepted control only.
        var status = PreshutdownLifetime.RunningStatus();

        Assert.Equal(ServiceInterop.SERVICE_WIN32_OWN_PROCESS, status.dwServiceType);
        Assert.Equal(ServiceInterop.SERVICE_RUNNING, status.dwCurrentState);
        Assert.Equal(0u, status.dwWaitHint);
        Assert.Equal(0u, status.dwWin32ExitCode);
    }

    [Fact]
    public void TheReportedStatusStillAcceptsStopAndShutdown()
    {
        var accepted = PreshutdownLifetime.RunningStatus().dwControlsAccepted;

        Assert.True((accepted & ServiceInterop.SERVICE_ACCEPT_STOP) != 0);
        Assert.True((accepted & ServiceInterop.SERVICE_ACCEPT_SHUTDOWN) != 0);
        Assert.True((accepted & ServiceInterop.SERVICE_ACCEPT_PRESHUTDOWN) != 0);
    }

    [Fact]
    public void NothingIsReportedBeforeTheHostHasStarted()
    {
        // The status handle exists once the service main callback runs, but the service is not running until the host is.
        // Claiming otherwise reports a started service while the layers are still coming up.
        using var lifetime = Lifetime(Handle);

        Assert.Empty(_reported);
    }

    [Fact]
    public void TheLongerNoticeIsAskedForOnceTheHostHasStarted()
    {
        using var lifetime = Lifetime(Handle);

        _application.Start();

        var report = Assert.Single(_reported);
        Assert.Equal(Handle, report.Handle);
        Assert.True((report.Status.dwControlsAccepted & ServiceInterop.SERVICE_ACCEPT_PRESHUTDOWN) != 0);
    }

    [Fact]
    public void NothingIsReportedWithoutAStatusHandle()
    {
        // Zero is what ServiceBase hands out outside the SCM; SetServiceStatus on it fails with an invalid handle, so it is not called.
        using var lifetime = Lifetime(IntPtr.Zero);

        _application.Start();

        Assert.Empty(_reported);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void ARefusedReportIsSaidOutLoudAndTheServiceCarriesOn()
    {
        // Not swallowed: the service keeps working, but with the short shutdown notice.
        _error = 6;
        using var lifetime = Lifetime(Handle);

        _application.Start();

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("6", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACustomCommandThatIsNotPreshutdownDoesNotStopTheService()
    {
        // 128 is the first code anybody else can send; stopping on one would turn any control into a shutdown of the layers.
        using var lifetime = Lifetime(Handle);

        lifetime.Handle(128);

        Assert.False(_stopped);
    }

    [Fact]
    public void PreshutdownStopsTheServiceTheSameWayAStopDoes()
    {
        // State is saved, the trace session closed, the port freed: what stopping already does. The block stays in place.
        using var lifetime = Lifetime(Handle);

        lifetime.Handle(PreshutdownLifetime.PreshutdownCommand);

        Assert.True(_stopped);
    }

    [Fact]
    public void TheContainerCanBuildIt()
    {
        // Program registers this as the IHostLifetime and nothing resolves it, so an uncallable constructor would show up only on a freshly installed machine.
        // Resolving is not running: no ServiceBase is started.
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new FakeEnvironment());
        services.AddSingleton<IHostApplicationLifetime>(_application);
        services.AddSingleton<ILoggerFactory>(new FakeLoggerFactory(_logger));
        services.AddSingleton(Options.Create(new HostOptions()));
        services.AddSingleton(Options.Create(new WindowsServiceLifetimeOptions { ServiceName = "ChronosService" }));
        services.AddSingleton<IHostLifetime, PreshutdownLifetime>();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<PreshutdownLifetime>(provider.GetRequiredService<IHostLifetime>());
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();

        private readonly CancellationTokenSource _stopping = new();

        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void Start() => _started.Cancel();

        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class FakeLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => logger;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";

        public string ApplicationName { get; set; } = "Chronos.Service";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
