using System.ComponentModel;

namespace Chronos.Service.Setup;

/// <summary>
/// Whether a failed start or stop is the service manager already doing what was asked.
/// <see cref="IServiceRegistration.Query"/> and the following call are two trips, so the state can
/// change in between, typically when the manager restarts a failed service. Public because
/// <c>recover</c> needs it and <c>ServiceInterop</c> is internal.
/// </summary>
public static class ServiceManagerRace
{
    /// <summary>True when the failure means the machine is already putting itself right.</summary>
    public static bool LostToTheManager(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Either the Win32Exception itself, or ServiceController's InvalidOperationException wrapping it.
        var win32 = exception as Win32Exception ?? exception.InnerException as Win32Exception;

        return win32 is
        {
            NativeErrorCode: ServiceInterop.ERROR_SERVICE_ALREADY_RUNNING
                or ServiceInterop.ERROR_SERVICE_CANNOT_ACCEPT_CTRL,
        };
    }
}
