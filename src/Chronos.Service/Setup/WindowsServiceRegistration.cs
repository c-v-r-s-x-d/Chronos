using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace Chronos.Service.Setup;

/// <summary>
/// The service control manager, over advapi32 for what it takes to register a service and over
/// <see cref="ServiceController"/> for what the framework already does.
/// </summary>
// Creating a service and setting failure actions are what ServiceController cannot do, so those
// are P/Invoke. sc.exe is avoided because its answers are localised text.
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceRegistration : IServiceRegistration
{
    private readonly string _serviceName;

    public WindowsServiceRegistration()
        : this(ServiceDefinition.ChronosServiceName)
    {
    }

    public WindowsServiceRegistration(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        _serviceName = serviceName;
    }

    public ServiceRunState Query()
    {
        using var controller = new ServiceController(_serviceName);

        try
        {
            return Map(controller.Status);
        }
        catch (InvalidOperationException exception)
            when (exception.InnerException is Win32Exception
                { NativeErrorCode: ServiceInterop.ERROR_SERVICE_DOES_NOT_EXIST })
        {
            // The only failure that means "not installed". Access denial must surface as itself, or
            // recovery would reinstall over a working installation.
            return ServiceRunState.NotInstalled;
        }
    }

    public void Install(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var manager = OpenManager();
        try
        {
            var service = CreateService(manager, definition);
            try
            {
                Configure(service, definition);
            }
            catch
            {
                // A half-configured service would start at boot with no failure actions and nothing
                // would finish it. No service at all can simply be retried.
                Rollback(service);
                throw;
            }
            finally
            {
                CloseHandle(service);
            }
        }
        finally
        {
            CloseHandle(manager);
        }
    }

    /// <summary>
    /// Sends the whole definition to an existing service, so a second installation matches the
    /// first. An older build or failed registration may have left it differently configured.
    /// </summary>
    /// <remarks>
    /// No rollback, unlike <see cref="Install"/>: the previous configuration was not recorded. A
    /// partial failure is reported and fixed by running the command again.
    /// </remarks>
    public void Update(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var manager = OpenManager();
        try
        {
            var service = OpenService(manager);
            try
            {
                Reconfigure(service, definition);
                Configure(service, definition);
            }
            finally
            {
                CloseHandle(service);
            }
        }
        finally
        {
            CloseHandle(manager);
        }
    }

    public void Remove()
    {
        var manager = OpenManager();
        try
        {
            var service = OpenService(manager);

            try
            {
                // Only marks the service for deletion; a running one stays listed until it exits, so uninstall stops it first.
                if (!ServiceInterop.DeleteService(service))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                CloseHandle(service);
            }
        }
        finally
        {
            CloseHandle(manager);
        }
    }

    public void Start(TimeSpan timeout)
    {
        using var controller = new ServiceController(_serviceName);

        // Starting a running service is an error, and callers may have lost a race. A service the
        // manager is restarting is start-pending, so starting it too gives ERROR_SERVICE_ALREADY_RUNNING.
        if (controller.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.StartPending))
        {
            controller.Start();
        }

        controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    public void Stop(TimeSpan timeout)
    {
        using var controller = new ServiceController(_serviceName);

        // A service already stopping refuses a stop with ERROR_SERVICE_CANNOT_ACCEPT_CTRL.
        if (controller.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
        {
            controller.Stop();
        }

        controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    private static ServiceRunState Map(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => ServiceRunState.Starting,
        ServiceControllerStatus.Running => ServiceRunState.Running,
        ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => ServiceRunState.Stopping,

        // Stopped, Paused (never accepted by Chronos) and unknown values.
        _ => ServiceRunState.Stopped,
    };

    private static IntPtr OpenManager()
    {
        var manager = ServiceInterop.OpenSCManagerW(null, null, ServiceInterop.SC_MANAGER_ALL_ACCESS);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return manager;
    }

    private IntPtr OpenService(IntPtr manager)
    {
        var service = ServiceInterop.OpenServiceW(manager, _serviceName, ServiceInterop.SERVICE_ALL_ACCESS);
        if (service == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return service;
    }

    /// <summary>Everything set after a service exists, shared by install and update so they cannot disagree.</summary>
    private static void Configure(IntPtr service, ServiceDefinition definition)
    {
        SetDescription(service, definition);
        SetFailureActions(service, definition);
        SetFailureActionsFlag(service, definition);
    }

    /// <summary>
    /// Updates what is fixed at creation: image path, start type, dependencies and account. The
    /// image path matters most: a service first installed from a build directory would otherwise
    /// keep pointing there after a package install.
    /// </summary>
    private static void Reconfigure(IntPtr service, ServiceDefinition definition)
    {
        var dependencies = Marshal.StringToHGlobalUni(definition.DependencyBlock);
        try
        {
            if (!ServiceInterop.ChangeServiceConfigW(
                service,
                ServiceInterop.SERVICE_WIN32_OWN_PROCESS,
                StartType(definition.StartMode),
                ServiceInterop.SERVICE_ERROR_NORMAL,
                definition.CommandLine,
                lpLoadOrderGroup: null,
                lpdwTagId: IntPtr.Zero,
                dependencies,

                // Named explicitly: null means "unchanged" here but LocalSystem to CreateServiceW.
                definition.AccountName ?? "LocalSystem",
                lpPassword: null,
                definition.DisplayName))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(dependencies);
        }
    }

    private static IntPtr CreateService(IntPtr manager, ServiceDefinition definition)
    {
        // The block contains NULs, so it is allocated here rather than marshalled as a string.
        var dependencies = Marshal.StringToHGlobalUni(definition.DependencyBlock);
        try
        {
            var service = ServiceInterop.CreateServiceW(
                manager,
                definition.Name,
                definition.DisplayName,
                ServiceInterop.SERVICE_ALL_ACCESS,
                ServiceInterop.SERVICE_WIN32_OWN_PROCESS,
                StartType(definition.StartMode),
                ServiceInterop.SERVICE_ERROR_NORMAL,
                definition.CommandLine,
                lpLoadOrderGroup: null,
                lpdwTagId: IntPtr.Zero,
                dependencies,
                definition.AccountName,   // null is LocalSystem to the service manager
                lpPassword: null);

            if (service == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return service;
        }
        finally
        {
            Marshal.FreeHGlobal(dependencies);
        }
    }

    private static void SetDescription(IntPtr service, ServiceDefinition definition)
    {
        var description = new ServiceInterop.SERVICE_DESCRIPTION { lpDescription = definition.Description };

        if (!ServiceInterop.ChangeServiceConfig2W(
            service, ServiceInterop.SERVICE_CONFIG_DESCRIPTION, ref description))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static void SetFailureActions(IntPtr service, ServiceDefinition definition)
    {
        var failure = AllocateFailureActions(definition);
        try
        {
            if (!ServiceInterop.ChangeServiceConfig2W(
                service, ServiceInterop.SERVICE_CONFIG_FAILURE_ACTIONS, ref failure))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            // SC_ACTION holds only integers, so freeing the block is all the cleanup.
            Marshal.FreeHGlobal(failure.lpsaActions);
        }
    }

    /// <summary>
    /// Builds what the service manager is handed for info level 2. The caller frees <c>lpsaActions</c>.
    /// Separate from the send so the units (ms in delays, seconds in the reset period) are testable
    /// without administrator rights.
    /// </summary>
    internal static ServiceInterop.SERVICE_FAILURE_ACTIONS AllocateFailureActions(ServiceDefinition definition)
    {
        var actions = definition.RestartDelays
            .Select(delay => new ServiceInterop.SC_ACTION
            {
                Type = ServiceInterop.SC_ACTION_RESTART,
                Delay = (uint)delay.TotalMilliseconds,
            })
            .ToArray();

        var size = Marshal.SizeOf<ServiceInterop.SC_ACTION>();
        var buffer = Marshal.AllocHGlobal(size * actions.Length);
        for (var i = 0; i < actions.Length; i++)
        {
            Marshal.StructureToPtr(actions[i], buffer + (i * size), fDeleteOld: false);
        }

        return new ServiceInterop.SERVICE_FAILURE_ACTIONS
        {
            // Seconds, unlike the millisecond delays above.
            dwResetPeriod = (uint)definition.FailureCountResetPeriod.TotalSeconds,
            lpRebootMsg = null,
            lpCommand = null,
            cActions = (uint)actions.Length,
            lpsaActions = buffer,
        };
    }

    private static void SetFailureActionsFlag(IntPtr service, ServiceDefinition definition)
    {
        // Says when the actions run. The default (FALSE) covers only a process that dies without
        // reporting SERVICE_STOPPED, but the Generic Host reports it with a non-zero exit code on
        // an unhandled exception, so without this only taskkill would trigger a restart.
        var flag = BuildFailureActionsFlag(definition);

        if (!ServiceInterop.ChangeServiceConfig2W(
            service, ServiceInterop.SERVICE_CONFIG_FAILURE_ACTIONS_FLAG, ref flag))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal static ServiceInterop.SERVICE_FAILURE_ACTIONS_FLAG BuildFailureActionsFlag(ServiceDefinition definition) =>
        new() { fFailureActionsOnNonCrashFailures = definition.RestartOnNonCrashFailures ? 1 : 0 };

    /// <summary>Unregisters a service this call created. The result is discarded so the original failure surfaces.</summary>
    private static void Rollback(IntPtr service) => _ = ServiceInterop.DeleteService(service);

    private static uint StartType(ServiceStartMode mode) => mode switch
    {
        ServiceStartMode.Automatic => ServiceInterop.SERVICE_AUTO_START,
        ServiceStartMode.Manual => ServiceInterop.SERVICE_DEMAND_START,
        ServiceStartMode.Disabled => ServiceInterop.SERVICE_DISABLED,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>Closes a handle. The result is discarded: it fails only on an invalid handle, and throwing from a finally would hide the real failure.</summary>
    private static void CloseHandle(IntPtr handle) => _ = ServiceInterop.CloseServiceHandle(handle);
}
