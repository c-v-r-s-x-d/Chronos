using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Chronos.Service.Setup;

/// <summary>The task scheduler, over schtasks.exe and the XML it registers a task from.</summary>
/// <remarks>
/// schtasks with an XML file was chosen over late-bound Schedule.Service COM, where a mistyped
/// property is a run-time error. Starting the process and removing the folder are constructor
/// arguments so tests can replace them.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SchtasksRegistration : ITaskRegistration
{
    // schtasks refuses UTF-8 task files; RecoveryTaskDefinition's declaration must agree.
    private static readonly UnicodeEncoding TaskFileEncoding = new(bigEndian: false, byteOrderMark: true);

    // ERROR_FILE_NOT_FOUND as an HRESULT: with /HRESULT, what schtasks leaves for an unregistered task.
    private const int TaskNotFound = unchecked((int)0x80070002);

    // ERROR_FILE_NOT_FOUND and ERROR_PATH_NOT_FOUND: the folder, or its parent, is missing.
    private static readonly int[] FolderNotFound = [unchecked((int)0x80070002), unchecked((int)0x80070003)];

    /// <summary>Finite because the caller may be an MSI custom action with no console to interrupt.</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long to wait for the last output after the process exits. Bounded because a grandchild
    /// that inherited the pipe can hold it open indefinitely.
    /// </summary>
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    // Only the exit code and first line are used, so stop collecting past this.
    private const int OutputLimit = 8 * 1024;

    private readonly string _taskName;
    private readonly Func<string[], ProcessResult> _run;
    private readonly Action<string> _removeFolder;

    public SchtasksRegistration()
        : this(RecoveryTaskDefinition.TaskName)
    {
    }

    public SchtasksRegistration(string taskName)
        : this(taskName, Run, DeleteTaskFolder)
    {
    }

    /// <summary>The same class with the two calls onto the machine replaced, for tests.</summary>
    internal SchtasksRegistration(string taskName, Func<string[], ProcessResult> run, Action<string> removeFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(removeFolder);

        _taskName = taskName;
        _run = run;
        _removeFolder = removeFolder;
    }

    /// <summary>The full path, not the name: this runs as LocalSystem, and PATH must not decide what "schtasks" is.</summary>
    internal static string SchtasksPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    public bool Exists()
    {
        // /HRESULT: without it schtasks leaves 1 both for "no such task" and "access denied". The
        // two must not be confused, or uninstall would report removing a task that still runs.
        var query = _run(["/Query", "/TN", _taskName, "/HRESULT"]);

        return query.ExitCode switch
        {
            0 => true,
            TaskNotFound => false,
            _ => throw Failure("query the recovery task", query),
        };
    }

    public void Register(string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);

        // A fresh, recognisable name so concurrent installations do not share a file.
        var file = Path.Combine(Path.GetTempPath(), $"chronos-recovery-{Guid.NewGuid():N}.xml");

        try
        {
            Write(file, xml);

            // /F replaces an existing task of the same name. /HRESULT as in Exists, so a log shows e.g. 0x80070005.
            var result = _run(["/Create", "/TN", _taskName, "/XML", file, "/F", "/HRESULT"]);
            if (result.ExitCode != 0)
            {
                throw Failure("register the recovery task", result);
            }
        }
        finally
        {
            Delete(file);
        }
    }

    public void Remove()
    {
        // A missing task is not a failure. Exists throws on anything unclear, so denial is never read as gone.
        if (Exists())
        {
            var result = _run(["/Delete", "/TN", _taskName, "/F", "/HRESULT"]);
            if (result.ExitCode != 0)
            {
                throw Failure("delete the recovery task", result);
            }
        }

        // schtasks cannot delete the folder. Done outside the branch above in case an earlier
        // uninstall removed the task but left the folder.
        RemoveFolder();
    }

    /// <summary>The folder the task lives in, if it lives in one.</summary>
    private void RemoveFolder()
    {
        var separator = _taskName.LastIndexOf('\\');
        if (separator < 0)
        {
            return;
        }

        var folder = _taskName[..separator].Trim('\\');
        if (folder.Length != 0)
        {
            _removeFolder(folder);
        }
    }

    /// <summary>Removes a task folder through the scheduler's COM object; schtasks has no such command.</summary>
    [SupportedOSPlatform("windows")]
    private static void DeleteTaskFolder(string folder)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            var service = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("The task scheduler could not be reached to remove the task folder.");

            // Connect's four optional arguments are for another machine or account; Missing means this one.
            Invoke(service, "Connect", BindingFlags.InvokeMethod, Type.Missing, Type.Missing, Type.Missing, Type.Missing);

            var root = Invoke(service, "GetFolder", BindingFlags.InvokeMethod, "\\")
                ?? throw new InvalidOperationException("The task scheduler has no root folder to remove from.");

            Invoke(root, "DeleteFolder", BindingFlags.InvokeMethod, folder, 0);
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is { } inner && FolderNotFound.Contains(inner.HResult))
        {
            // Already gone.
        }
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
        {
            // Rewrapped: a reflection wrapper in an MSI log says nothing useful.
            throw new InvalidOperationException(
                $"The task scheduler could not delete the '{folder}' task folder: {inner.Message}",
                inner);
        }
        catch (COMException exception)
        {
            // Reaching the object fails outside a reflection wrapper, e.g. with the scheduler service disabled.
            throw new InvalidOperationException(
                $"The task scheduler could not be reached to delete the '{folder}' task folder: {exception.Message}",
                exception);
        }
    }

    private static object? Invoke(object target, string member, BindingFlags flags, params object?[] arguments) =>
        target.GetType().InvokeMember(member, flags, binder: null, target, arguments);

    private static void Write(string file, string xml)
    {
        try
        {
            File.WriteAllText(file, xml, TaskFileEncoding);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Say what Chronos was doing; a bare IOException would only name a temp file.
            throw new InvalidOperationException(
                $"The recovery task document could not be written to '{file}': {exception.Message}",
                exception);
        }
    }

    private static void Delete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Runs in a finally block; throwing would replace the caller's exception.
        }
    }

    private static InvalidOperationException Failure(string what, ProcessResult result)
    {
        // Our half is English; the quoted line is schtasks's own, in the machine's language, and is labelled as such.
        var said = FirstLine(result.Output);

        return new InvalidOperationException(
            $"schtasks could not {what}: exit code 0x{result.ExitCode:X8}."
            + (said.Length == 0 ? string.Empty : $" schtasks said, in this machine's own language: {said}"));
    }

    private static string FirstLine(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length != 0)
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    private static ProcessResult Run(string[] arguments)
    {
        var info = new ProcessStartInfo(SchtasksPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,

            // Output and error are drained because a full pipe stalls the child; input is closed so a
            // prompt gets end of file instead of waiting on a console.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // ArgumentList quotes each argument; the temp path often contains a space.
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"'{SchtasksPath}' could not be started.");

        var output = new StringBuilder();
        var sync = new object();

        // Both handlers run on pool threads. Each stream ends with a null-data call; the drain waits on this count.
        var open = 2;

        void Collect(object sender, DataReceivedEventArgs received)
        {
            lock (sync)
            {
                if (received.Data is null)
                {
                    open--;
                    Monitor.PulseAll(sync);

                    return;
                }

                if (output.Length < OutputLimit)
                {
                    output.AppendLine(received.Data);
                }
            }
        }

        process.OutputDataReceived += Collect;
        process.ErrorDataReceived += Collect;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        if (!process.WaitForExit((int)CallTimeout.TotalMilliseconds))
        {
            Kill(process);

            throw new InvalidOperationException(
                $"schtasks did not answer within {CallTimeout.TotalSeconds:0} seconds and was stopped.");
        }

        // The last lines may still be in the pipe after the process exits.
        lock (sync)
        {
            var clock = Stopwatch.StartNew();
            var remaining = DrainTimeout;

            while (open > 0 && remaining > TimeSpan.Zero)
            {
                Monitor.Wait(sync, remaining);
                remaining = DrainTimeout - clock.Elapsed;
            }

            // Whatever arrived; missing output only makes the message less helpful.
            return new ProcessResult(process.ExitCode, output.ToString());
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already exiting; the caller hears about the timeout.
        }
    }

    internal readonly record struct ProcessResult(int ExitCode, string Output);
}
