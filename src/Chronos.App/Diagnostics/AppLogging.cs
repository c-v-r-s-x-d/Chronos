using Serilog;
using Serilog.Events;

namespace Chronos.App.Diagnostics;

/// <summary>Where the interface's log goes and how it rotates. Same scheme as the service's <c>ServiceLogging</c>; keep the two in step.</summary>
public static class AppLogging
{
    /// <summary>Seven days, one file a day.</summary>
    public const int RetainedFiles = 7;

    /// <summary>Ten megabytes to a file.</summary>
    public const long FileSizeLimitBytes = 10L * 1024 * 1024;

    private const string Template =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary><c>%LOCALAPPDATA%\Chronos\logs</c>. Not <c>%ProgramData%</c>: the service writes there under another account.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chronos", "logs");

    /// <summary>Opens the log in a named directory, creating it. No default directory on purpose, so a test cannot write to the real log by forgetting.</summary>
    /// <param name="verbose">Whether <c>Debug</c> records are kept; full paths and addresses may appear only at that level.</param>
    public static AppLog Open(string directory, bool verbose = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        // Not assigned to Serilog's static Log.Logger, unlike ServiceLogging: tests build several logs in one process.
        return new AppLog(new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.File(
                Path.Combine(directory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFiles,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: Template)
            .CreateLogger());
    }
}
