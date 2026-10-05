using Chronos.Service.Configuration;
using Chronos.Service.Diagnostics;
using Serilog;
using Serilog.Events;

namespace Chronos.Service;

public static class ServiceLogging
{
    public static Serilog.ILogger Configure(ChronosPaths paths, bool verbose, ISystemEventLog eventLog)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(eventLog);

        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.File(
                Path.Combine(paths.ServiceLogDirectory, "service-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

        // Checked once here, not per entry (it reads the registry). Install registers the source
        // first, so only an uninstalled dev run skips the sink.
        if (eventLog.SourceExists())
        {
            configuration = configuration.WriteTo.Sink(new EventLogSink(eventLog));
        }

        Log.Logger = configuration.CreateLogger();

        return Log.Logger;
    }
}
