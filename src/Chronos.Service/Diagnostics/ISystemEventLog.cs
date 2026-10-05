namespace Chronos.Service.Diagnostics;

/// <summary>Maps onto the Windows event log's three levels.</summary>
public enum SystemEventLevel
{
    Information,

    Warning,

    Error,
}

/// <summary>The Windows event log, as much of it as Chronos uses.</summary>
public interface ISystemEventLog
{
    /// <summary>
    /// Writes one entry; returns whether it was written. Never throws, since callers include the
    /// recovery task. A blank message is written as a placeholder.
    /// </summary>
    bool Write(SystemEventLevel level, int eventId, string message);

    /// <summary>Whether the source is registered. False when it cannot tell.</summary>
    bool SourceExists();

    /// <summary>Registers the source. Administrator only; an install-time action.</summary>
    void EnsureSource();

    /// <summary>Unregisters the source. Administrator only; an uninstall-time action.</summary>
    void RemoveSource();
}

/// <summary>The event identifiers Chronos writes. Never renumber: administrators filter by them.</summary>
public static class ChronosEvents
{
    public const int Installed = 1000;

    public const int Uninstalled = 1001;

    /// <summary>Never written: the recovery task runs every boot and would add a daily "all is well". Reserved.</summary>
    public const int RecoveryFoundServiceHealthy = 1010;

    public const int RecoveryCleared = 1011;

    public const int RecoveryFailed = 1012;

    /// <summary>A diagnostic package was collected. The entry carries no path and no domain.</summary>
    public const int DiagnosticsCollected = 1013;

    /// <summary>Any <c>Error</c> the service logs; the cause is in the text.</summary>
    public const int ServiceError = 2000;

    /// <summary>Any <c>Fatal</c> the service logs, and a start failure that never reaches Serilog.</summary>
    public const int ServiceCritical = 2001;
}
