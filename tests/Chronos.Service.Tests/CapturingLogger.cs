using Microsoft.Extensions.Logging;

namespace Chronos.Service.Tests;

internal sealed class CapturingLogger<T> : ILogger<T>
{
    // Guarded, and handed out as a copy: tests may log from several threads, and a bare List<T>
    // drops entries or throws instead of failing honestly.
    private readonly Lock _sync = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    /// <summary>Everything logged so far, as of this call. A snapshot, not a live view.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_sync)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Forgets everything logged so far, so a test can assert about one phase only.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var entry = (logLevel, formatter(state, exception));

        lock (_sync)
        {
            _entries.Add(entry);
        }
    }
}
