using Chronos.Service.Diagnostics;

namespace Chronos.Service.Tests;

/// <summary>The Windows event log, remembered instead of written: registering a source writes under HKLM and a test entry is one an administrator must read past.</summary>
internal sealed class FakeSystemEventLog : ISystemEventLog
{
    internal sealed record Entry(SystemEventLevel Level, int EventId, string Message);

    /// <summary>Every write that was attempted, whether or not <see cref="WriteAnswers"/> let it succeed.</summary>
    public List<Entry> Entries { get; } = [];

    /// <summary>Where the two calls that change the machine write their names, for order tests.</summary>
    public List<string> Steps { get; init; } = [];

    /// <summary>Whether anything was written after the source had gone. Uninstall must write its entry before removing the source.</summary>
    public bool WroteWithoutSource { get; private set; }

    /// <summary>What <see cref="Write"/> answers. False is a machine whose log cannot be written.</summary>
    public bool WriteAnswers { get; set; } = true;

    public bool SourceIsRegistered { get; set; } = true;

    public int EnsureSourceCalls { get; private set; }

    public int RemoveSourceCalls { get; private set; }

    /// <summary>A source that could not be registered: creating one writes under HKLM, and an installation without the rights must stop.</summary>
    public Exception? EnsureSourceThrows { get; set; }

    public bool Write(SystemEventLevel level, int eventId, string message)
    {
        Entries.Add(new Entry(level, eventId, message));
        WroteWithoutSource |= !SourceIsRegistered;

        return WriteAnswers;
    }

    public bool SourceExists() => SourceIsRegistered;

    public void EnsureSource()
    {
        Steps.Add("event-source");

        if (EnsureSourceThrows is { } failure)
        {
            throw failure;
        }

        EnsureSourceCalls++;
        SourceIsRegistered = true;
    }

    public void RemoveSource()
    {
        Steps.Add("remove-source");
        RemoveSourceCalls++;
        SourceIsRegistered = false;
    }
}
