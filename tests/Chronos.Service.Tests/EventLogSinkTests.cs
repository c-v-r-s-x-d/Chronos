using System.ComponentModel;
using System.Globalization;
using Chronos.Service.Diagnostics;
using Serilog;
using Serilog.Events;

namespace Chronos.Service.Tests;

public sealed class EventLogSinkTests : IDisposable
{
    private readonly FakeSystemEventLog _log = new();

    private readonly Serilog.Core.Logger _logger;

    public EventLogSinkTests() =>
        _logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new EventLogSink(_log))
            .CreateLogger();

    public void Dispose() => _logger.Dispose();

    private void Emit(LogEventLevel level, string template, params object?[] values) =>
        _logger.Write(level, template, values);

    private void Emit(LogEventLevel level, Exception exception, string template, params object?[] values) =>
        _logger.Write(level, exception, template, values);

    [Fact]
    public void WarningAndBelowAreNotDuplicated()
    {
        // Only two levels go to the Windows event log. Everything else already goes to the file log, and the event log is shared machine-wide.
        Emit(LogEventLevel.Verbose, "a probe ran");
        Emit(LogEventLevel.Debug, "the plan was recomputed");
        Emit(LogEventLevel.Information, "a pass finished");
        Emit(LogEventLevel.Warning, "the hosts file was busy");

        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void ErrorAndFatalAreDuplicated()
    {
        Emit(LogEventLevel.Error, "the filters could not be applied");
        Emit(LogEventLevel.Fatal, "the service is stopping");

        Assert.Equal(
            [SystemEventLevel.Error, SystemEventLevel.Error],
            _log.Entries.Select(entry => entry.Level));
    }

    [Fact]
    public void FatalIsToldApartFromErrorByItsEventId()
    {
        // The Windows log has no Critical entry type (EventLogEntryType stops at Error), so the two levels
        // would be indistinguishable without this. The id is also what an administrator filters by.
        Emit(LogEventLevel.Error, "the filters could not be applied");
        Emit(LogEventLevel.Fatal, "the service is stopping");

        Assert.Equal(
            [ChronosEvents.ServiceError, ChronosEvents.ServiceCritical],
            _log.Entries.Select(entry => entry.EventId));
    }

    [Fact]
    public void TheEntryCarriesTheRenderedMessage()
    {
        // An administrator reads the reason; an entry saying "an error occurred" answers nothing.
        Emit(LogEventLevel.Error, "the hosts file could not be written: {Reason}", "it is read-only");

        Assert.Contains("it is read-only", Assert.Single(_log.Entries).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEntryNamesWhatWroteIt()
    {
        // The band is two identifiers wide, so the id alone does not say where the entry came from. The
        // logging class tells a failed hosts write from a failed filter, so it goes into the text.
        _logger.ForContext<EventLogSinkTests>().Write(LogEventLevel.Error, "the engine refused");

        Assert.Contains(
            nameof(EventLogSinkTests),
            Assert.Single(_log.Entries).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheEntryIsEnglishOnARussianMachine()
    {
        // Rendering under the machine's culture would put a localised number format into an English log.
        // A whole number would not show it (the "G" format has no group separator in any culture), so this
        // uses a fraction, where ru-RU writes the comma that the invariant culture writes as a full stop.
        using var russian = new CultureScope("ru-RU");

        Emit(LogEventLevel.Error, "the pass took {Seconds} seconds", 12.5);

        var written = Assert.Single(_log.Entries).Message;
        Assert.Contains("12.5", written, StringComparison.Ordinal);
        Assert.DoesNotContain("12,5", written, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExceptionGoesInWholeAndNotAsItsLocalisedMessage()
    {
        // Win32Exception(5).Message is localised by the platform on non-English machines, so an entry
        // built from Message alone would not be English there. ToString carries the type and the stack
        // too, which is the half an administrator can act on.
        Emit(LogEventLevel.Error, new Win32Exception(5), "the engine refused");

        var written = Assert.Single(_log.Entries).Message;
        Assert.Contains("the engine refused", written, StringComparison.Ordinal);
        Assert.Contains(nameof(Win32Exception), written, StringComparison.Ordinal);
    }

    [Fact]
    public void ALogThatCannotBeWrittenDoesNotStopTheService()
    {
        // Write answers false on a machine with no registered source or a full log. A service that died because it could not describe its own failure is worse than a lost line.
        _log.WriteAnswers = false;

        Emit(LogEventLevel.Fatal, "the service is stopping");

        Assert.Single(_log.Entries);
    }

    /// <summary>The machine's culture for the length of one test (a non-invariant one, as on real machines).</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = new CultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
