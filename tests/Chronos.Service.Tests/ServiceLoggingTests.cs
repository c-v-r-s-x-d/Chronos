using Chronos.Service;
using Chronos.Service.Configuration;
using Chronos.Service.Diagnostics;

namespace Chronos.Service.Tests;

public sealed class ServiceLoggingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeSystemEventLog _eventLog = new();

    public void Dispose()
    {
        Serilog.Log.CloseAndFlush();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ChronosPaths Paths()
    {
        var paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();

        return paths;
    }

    [Fact]
    public void WritesIntoTheServiceLogDirectory()
    {
        var paths = Paths();

        var logger = ServiceLogging.Configure(paths, verbose: false, _eventLog);
        logger.Information("Probe entry.");
        Serilog.Log.CloseAndFlush();

        var files = Directory.GetFiles(paths.ServiceLogDirectory, "service-*.log");
        Assert.NotEmpty(files);
        Assert.Contains("Probe entry.", File.ReadAllText(files[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void AnErrorReachesTheWindowsEventLogAsWellAsTheFile()
    {
        // A property of the pipeline, not a habit of the logging code: the sink must be attached here, or every entry depends on someone remembering to write one.
        var paths = Paths();
        var logger = ServiceLogging.Configure(paths, verbose: false, _eventLog);

        logger.Error("The filters could not be applied.");
        Serilog.Log.CloseAndFlush();

        Assert.Contains(
            "The filters could not be applied.",
            Assert.Single(_eventLog.Entries).Message,
            StringComparison.Ordinal);

        // Duplicated, not diverted: the event log is a copy and the file log stays the full record.
        Assert.Contains(
            "The filters could not be applied.",
            File.ReadAllText(Directory.GetFiles(paths.ServiceLogDirectory, "service-*.log")[0]),
            StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutARegisteredSourceTheSinkIsNotAttachedAtAll()
    {
        // Checked once rather than on every entry: Write would answer false anyway but read the event-source registry each time.
        // install registers the source before it creates the service, so only a developer running straight from the build takes this branch.
        _eventLog.SourceIsRegistered = false;

        var logger = ServiceLogging.Configure(Paths(), verbose: false, _eventLog);
        logger.Error("The filters could not be applied.");

        Assert.Empty(_eventLog.Entries);
    }
}
