namespace Chronos.Service.Apps;

public readonly record struct ProcessStarted(int ProcessId, string ImagePath);

public readonly record struct RunningProcess(int ProcessId, string ImagePath);

/// <summary>A source of process-start notifications.</summary>
public interface IProcessEvents : IDisposable
{
    /// <summary>Which source is in use, for the log and for diagnostics.</summary>
    string SourceName { get; }

    /// <summary>True once the source has stopped delivering on its own.</summary>
    bool IsFaulted { get; }

    /// <summary>Subscribes the handler and begins delivery. A second call does nothing.</summary>
    void Start(Action<ProcessStarted> onStarted);
}

public enum KillOutcome
{
    Terminated,

    /// <summary>The process ended between the event and the kill.</summary>
    AlreadyGone,

    /// <summary>Alive, but this account may not terminate it.</summary>
    Denied,
}

public interface IProcessControl
{
    IReadOnlyList<RunningProcess> List();

    KillOutcome Kill(int processId);

    /// <summary>The full image path of a running process, or null when it cannot be read.</summary>
    string? ImagePathOf(int processId);
}
