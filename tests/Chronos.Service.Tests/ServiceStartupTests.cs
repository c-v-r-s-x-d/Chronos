using Chronos.Service;
using Chronos.Service.Diagnostics;
using Chronos.Service.Setup;
using Microsoft.Extensions.Hosting;

namespace Chronos.Service.Tests;

/// <summary>
/// A broken installation, a service that does not start and an administrator reading the Windows event log.
/// The Serilog sink cannot cover it (the failure can precede the logger), so the guard around the host is its own path.
/// </summary>
public sealed class ServiceStartupTests
{
    private readonly FakeSystemEventLog _log = new();

    [Fact]
    public void AFailureToStartIsWrittenWhereAnAdministratorLooks()
    {
        Assert.Throws<InvalidOperationException>(() => Program.RunReportingAFailureToStart(
            () => throw new InvalidOperationException("The data directory is not writable."),
            _log));

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(SystemEventLevel.Error, entry.Level);
        Assert.Equal(ChronosEvents.ServiceCritical, entry.EventId);
        Assert.Contains("The data directory is not writable.", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFailureIsRethrownSoTheServiceManagerStillSeesIt()
    {
        // Swallowing it would leave the service manager believing the service started and the restart actions would never fire. The log entry is a copy, not a handler.
        var failure = new InvalidOperationException("The host could not be built.");

        var thrown = Assert.Throws<InvalidOperationException>(
            () => Program.RunReportingAFailureToStart(() => throw failure, _log));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public void TheEntryCarriesTheWholeExceptionAndNotItsLocalisedMessage()
    {
        // The inner cause is usually the answer, and a platform Message arrives already translated.
        var failure = new InvalidOperationException(
            "The host could not be built.",
            new UnauthorizedAccessException("Access to the path is denied."));

        Assert.Throws<InvalidOperationException>(
            () => Program.RunReportingAFailureToStart(() => throw failure, _log));

        Assert.Contains(
            nameof(UnauthorizedAccessException),
            Assert.Single(_log.Entries).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AServiceThatStartsWritesNothing()
    {
        var ran = false;

        Program.RunReportingAFailureToStart(() => ran = true, _log);

        Assert.True(ran);
        Assert.Empty(_log.Entries);
    }

    /// <summary>The host's half of the service name, pinned against the definition the service manager registers; the two must agree or the dispatcher never connects.</summary>
    [Fact]
    public void TheHostAnswersToTheNameTheServiceIsRegisteredUnder()
    {
        var options = new WindowsServiceLifetimeOptions();

        Program.NameTheHost(options);

        Assert.Equal(
            ServiceDefinition.Chronos(@"C:\Program Files\Chronos\Chronos.Service.exe").Name,
            options.ServiceName);
    }
}
