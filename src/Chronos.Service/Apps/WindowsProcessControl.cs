using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Chronos.Service.Apps;

public sealed class WindowsProcessControl : IProcessControl
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint Terminate = 0x0001;

    // What OpenProcess reports for a process id that no longer exists.
    private const int ErrorInvalidParameter = 87;

    // The exit code GetExitCodeProcess reports while the process is running.
    private const uint StillActive = 259;

    public IReadOnlyList<RunningProcess> List()
    {
        var running = new List<RunningProcess>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (TryReadImagePath(process.Id, out var imagePath))
                {
                    running.Add(new RunningProcess(process.Id, imagePath));
                }
            }
        }

        return running;
    }

    public string? ImagePathOf(int processId) =>
        TryReadImagePath(processId, out var imagePath) ? imagePath : null;

    public KillOutcome Kill(int processId)
    {
        var handle = OpenProcess(Terminate, false, processId);
        if (handle == IntPtr.Zero)
        {
            // A vanished process is routine; a live one we may not open means the block failed.
            return Marshal.GetLastWin32Error() is ErrorInvalidParameter
                ? KillOutcome.AlreadyGone
                : KillOutcome.Denied;
        }

        try
        {
            if (TerminateProcess(handle, 1))
            {
                return KillOutcome.Terminated;
            }

            // TerminateProcess on an exited process whose handle is still held elsewhere fails with
            // the same ERROR_ACCESS_DENIED as a protected live one. Only the exit code tells them apart.
            return HasExited(processId) ? KillOutcome.AlreadyGone : KillOutcome.Denied;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool HasExited(int processId)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return GetExitCodeProcess(handle, out var exitCode) && exitCode != StillActive;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool TryReadImagePath(int processId, out string imagePath)
    {
        imagePath = string.Empty;

        // PROCESS_QUERY_LIMITED_INFORMATION reads far more processes than Process.MainModule,
        // which needs the module list and fails across sessions and bitness.
        var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;

            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size))
            {
                return false;
            }

            imagePath = buffer.ToString();
            return imagePath.Length > 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
