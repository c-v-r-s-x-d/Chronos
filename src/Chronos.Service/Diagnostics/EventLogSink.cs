using System.Globalization;
using Serilog.Core;
using Serilog.Events;

namespace Chronos.Service.Diagnostics;

/// <summary>
/// Copies Error and Fatal entries of the service log into the Windows event log.
/// </summary>
public sealed class EventLogSink : ILogEventSink
{
    /// <summary>The quietest level that is copied; the rest stays in the file log.</summary>
    public const LogEventLevel Threshold = LogEventLevel.Error;

    private readonly ISystemEventLog _log;

    public EventLogSink(ISystemEventLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (logEvent.Level < Threshold)
        {
            return;
        }

        // The result is dropped on purpose: a lost line is better than a service that stops over it.
        _log.Write(SystemEventLevel.Error, EventIdFor(logEvent.Level), Describe(logEvent));
    }

    /// <summary>The Windows event log has no Critical type, so the event id is what tells the levels apart.</summary>
    private static int EventIdFor(LogEventLevel level) =>
        level >= LogEventLevel.Fatal ? ChronosEvents.ServiceCritical : ChronosEvents.ServiceError;

    private static string Describe(LogEvent logEvent)
    {
        // Invariant culture: the entry text is English whatever the machine's locale.
        var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);

        if (logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var context)
            && context is ScalarValue { Value: string source })
        {
            // The logging source, so an administrator can tell which layer failed.
            message = source + ": " + message;
        }

        if (logEvent.Exception is not null)
        {
            // ToString, not Message: the platform localises Message, and the type and stack are what help.
            message = message + Environment.NewLine + logEvent.Exception.ToString();
        }

        return message;
    }
}
