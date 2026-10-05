using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Chronos.Service.Setup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chronos.Service;

/// <summary>Reports a service status to the service control manager; returns 0 or the Win32 error. The test seam.</summary>
internal delegate int ReportServiceStatus(IntPtr serviceHandle, ServiceInterop.SERVICE_STATUS status);

/// <summary>
/// The Windows service host, plus SERVICE_ACCEPT_PRESHUTDOWN. Pre-shutdown gives the service a
/// longer, unshared notice to save state, close the ETW session and release the port.
/// </summary>
/// <remarks>
/// A control that is accepted but not handled makes the manager wait out the whole pre-shutdown
/// timeout on every shutdown.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class PreshutdownLifetime : WindowsServiceLifetime
{
    /// <summary>The controls ServiceBase accepts itself: CanStop and CanShutdown.</summary>
    public const uint AcceptStop = ServiceInterop.SERVICE_ACCEPT_STOP;

    public const uint AcceptShutdown = ServiceInterop.SERVICE_ACCEPT_SHUTDOWN;

    public const uint AcceptPreshutdown = ServiceInterop.SERVICE_ACCEPT_PRESHUTDOWN;

    /// <summary>SERVICE_CONTROL_PRESHUTDOWN. ServiceBase has no case for it, so it arrives as a custom command.</summary>
    public const int PreshutdownCommand = (int)ServiceInterop.SERVICE_CONTROL_PRESHUTDOWN;

    private readonly Func<IntPtr> _serviceHandle;

    private readonly ReportServiceStatus _report;

    private readonly Action _stop;

    private readonly ILogger _logger;

    private readonly IDisposable? _started;

    public PreshutdownLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor)
        : this(
            environment,
            applicationLifetime,
            loggerFactory,
            optionsAccessor,
            windowsServiceOptionsAccessor,
            serviceHandle: null,
            report: SetServiceStatus,
            stop: null)
    {
    }

    internal PreshutdownLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        Func<IntPtr>? serviceHandle,
        ReportServiceStatus report,
        Action? stop)
        : base(
            environment,
            applicationLifetime,
            loggerFactory,
            optionsAccessor,
            windowsServiceOptionsAccessor)
    {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(report);

        _serviceHandle = serviceHandle ?? (() => ServiceHandle);
        _report = report;
        _stop = stop ?? Stop;
        _logger = loggerFactory.CreateLogger<PreshutdownLifetime>();

        // Not from OnStart: ServiceBase rewrites the status (RUNNING, without pre-shutdown) the
        // moment OnStart returns, overwriting anything reported inside it. ApplicationStarted
        // comes after that write.
        _started = applicationLifetime.ApplicationStarted.Register(AskForTheLongerNotice);
    }

    /// <summary>
    /// Adds pre-shutdown to the accepted controls. SetServiceStatus replaces the whole set, so
    /// assigning would drop SERVICE_ACCEPT_STOP.
    /// </summary>
    internal static uint WithPreshutdown(uint accepted) => accepted | AcceptPreshutdown;

    /// <summary>
    /// The status reported once the host is up: RUNNING with pre-shutdown added. The controls are
    /// restated because QueryServiceStatus needs rights of its own.
    /// </summary>
    internal static ServiceInterop.SERVICE_STATUS RunningStatus() => new()
    {
        dwServiceType = ServiceInterop.SERVICE_WIN32_OWN_PROCESS,
        dwCurrentState = ServiceInterop.SERVICE_RUNNING,
        dwControlsAccepted = WithPreshutdown(AcceptStop | AcceptShutdown),
        dwWin32ExitCode = 0,
        dwServiceSpecificExitCode = 0,
        dwCheckPoint = 0,

        // Zero: a wait hint on a settled state reads as a stuck transition.
        dwWaitHint = 0,
    };

    /// <summary><see cref="OnCustomCommand"/> without a service control manager to send the command.</summary>
    internal void Handle(int command) => OnCustomCommand(command);

    /// <summary>
    /// Asks the service control manager for pre-shutdown notifications. A failure is logged, not
    /// fatal; the service falls back to the short shared shutdown notice.
    /// </summary>
    internal void AskForTheLongerNotice()
    {
        var handle = _serviceHandle();

        if (handle == IntPtr.Zero)
        {
            // The process is not under the service control manager. Program registers this lifetime
            // only when it is, so this should not happen.
            _logger.LogWarning(
                "There is no service status handle, so pre-shutdown notifications were not asked for.");

            return;
        }

        var error = _report(handle, RunningStatus());

        if (error != 0)
        {
            _logger.LogWarning(
                "Windows refused the pre-shutdown notification request with error {Error}. " +
                "The service will get the short shutdown notice instead.",
                error);
        }
    }

    /// <summary>Pre-shutdown (control code 15) arrives here; everything else goes to the base.</summary>
    protected override void OnCustomCommand(int command)
    {
        if (command == PreshutdownCommand)
        {
            // A normal stop saves state and releases resources, and leaves hosts entries and filters
            // in place. Stop(), not OnStop(), so ServiceBase reports STOP_PENDING and STOPPED;
            // otherwise the manager waits out the pre-shutdown timeout.
            _stop();

            return;
        }

        base.OnCustomCommand(command);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _started?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>The platform call. GetLastWin32Error is read here before another managed call can replace it.</summary>
    private static int SetServiceStatus(IntPtr serviceHandle, ServiceInterop.SERVICE_STATUS status) =>
        ServiceInterop.SetServiceStatus(serviceHandle, ref status) ? 0 : Marshal.GetLastWin32Error();
}
