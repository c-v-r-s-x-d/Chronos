using System.ComponentModel;
using System.Diagnostics;

namespace Chronos.App.Apps;

/// <summary>One program running now, as it is offered to the person.</summary>
public sealed record RunningApp(string Name, string Path);

/// <summary>
/// The programs running now, so a rule can be made by pointing at one. Processes that cannot be
/// inspected are skipped silently, and no path is logged.
/// </summary>
public static class RunningProcesses
{
    /// <summary>The distinct running programs. Protected ones are included so the dialog can say why they cannot be blocked.</summary>
    public static IReadOnlyList<RunningApp> Now()
    {
        var processes = Process.GetProcesses();

        try
        {
            return Gather(processes.Select(process => (Func<string?>)(() => process.MainModule?.FileName)));
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <param name="paths">One lazy image-path reading per process; each may fail.</param>
    internal static IReadOnlyList<RunningApp> Gather(IEnumerable<Func<string?>> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var found = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var read in paths)
        {
            string? path;

            try
            {
                path = read();
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Access denied, 32-bit seen from 64-bit, or the process ended. All normal.
                continue;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            path = path.Trim();
            var name = System.IO.Path.GetFileName(path);

            if (name.Length == 0)
            {
                continue;
            }

            // By path: twenty svchost.exe are one line, the same name in two places stays two.
            found.TryAdd(path, new RunningApp(name, path));
        }

        return
        [
            .. found.Values
                .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(app => app.Path, StringComparer.OrdinalIgnoreCase),
        ];
    }
}
