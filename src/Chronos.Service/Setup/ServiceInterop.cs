using System.Runtime.InteropServices;

namespace Chronos.Service.Setup;

/// <summary>
/// The service control manager's structures and entry points. Names match the Windows headers.
/// </summary>
internal static class ServiceInterop
{
    public const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    public const uint SERVICE_ALL_ACCESS = 0xF01FF;

    public const uint SERVICE_WIN32_OWN_PROCESS = 0x10;

    // Start types. BOOT_START and SYSTEM_START are for drivers; a user-mode service with either fails to start.
    public const uint SERVICE_AUTO_START = 2;
    public const uint SERVICE_DEMAND_START = 3;
    public const uint SERVICE_DISABLED = 4;

    public const uint SERVICE_ERROR_NORMAL = 1;

    public const uint SERVICE_CONFIG_DESCRIPTION = 1;
    public const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;

    // Level 2 sets the failure actions; whether they run on a reported failure is level 4, a
    // separate call. Only sc qfailureflag shows it.
    public const uint SERVICE_CONFIG_FAILURE_ACTIONS_FLAG = 4;

    public const uint SC_ACTION_RESTART = 1;

    /// <summary>The one failure that means "not installed".</summary>
    public const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;

    /// <summary>Somebody started it between the state read and the start, typically the manager restarting it.</summary>
    public const int ERROR_SERVICE_ALREADY_RUNNING = 1056;

    /// <summary>The service is mid-transition; the control is refused, not queued.</summary>
    public const int ERROR_SERVICE_CANNOT_ACCEPT_CTRL = 1061;

    // For the pre-shutdown notification, sent by PreshutdownLifetime. SetServiceStatus replaces
    // the whole accepted-controls set, so all three flags are needed together.
    public const uint SERVICE_RUNNING = 4;
    public const uint SERVICE_ACCEPT_STOP = 0x00000001;
    public const uint SERVICE_ACCEPT_SHUTDOWN = 0x00000004;
    public const uint SERVICE_ACCEPT_PRESHUTDOWN = 0x00000100;
    public const uint SERVICE_CONTROL_PRESHUTDOWN = 0x0000000F;

    /// <summary>One action after a failure. Blittable; the array is marshalled by hand.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SC_ACTION
    {
        public uint Type;
        public uint Delay;
    }

    /// <summary>SERVICE_FAILURE_ACTIONSW. The strings stay null (unchanged). lpsaActions is allocated and freed by the caller.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SERVICE_FAILURE_ACTIONS
    {
        public uint dwResetPeriod;
        private readonly uint padding;
        public string? lpRebootMsg;
        public string? lpCommand;
        public uint cActions;
        private readonly uint padding2;
        public IntPtr lpsaActions;
    }

    /// <summary>SERVICE_FAILURE_ACTIONS_FLAG, info level 4. BOOL is declared as a 4-byte int so the size is explicit.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_FAILURE_ACTIONS_FLAG
    {
        public int fFailureActionsOnNonCrashFailures;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SERVICE_DESCRIPTION
    {
        public string? lpDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    // lpDependencies is a double-NUL-terminated multi-string, so the caller allocates the block.
    // lpdwTagId matters only with a load order group and stays NULL.
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateServiceW(
        IntPtr hSCManager,
        string lpServiceName,
        string? lpDisplayName,
        uint dwDesiredAccess,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        IntPtr lpDependencies,
        string? lpServiceStartName,
        string? lpPassword);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    // CreateServiceW's parameters minus the name. All are sent rather than left at "no change", so
    // an existing service ends up matching the definition. lpPassword stays NULL for built-in accounts.
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfigW(
        IntPtr hService,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string? lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        IntPtr lpDependencies,
        string? lpServiceStartName,
        string? lpPassword,
        string? lpDisplayName);

    // ChangeServiceConfig2W takes an LPVOID shaped by the info level; one overload per shape keeps them in step.
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig2W(IntPtr hService, uint dwInfoLevel, ref SERVICE_FAILURE_ACTIONS lpInfo);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig2W(IntPtr hService, uint dwInfoLevel, ref SERVICE_DESCRIPTION lpInfo);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig2W(IntPtr hService, uint dwInfoLevel, ref SERVICE_FAILURE_ACTIONS_FLAG lpInfo);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteService(IntPtr hService);

    /// <summary>Called by <see cref="PreshutdownLifetime"/>. The handle is ServiceBase's, not one from OpenServiceW.</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetServiceStatus(IntPtr hServiceStatus, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseServiceHandle(IntPtr hSCObject);
}
