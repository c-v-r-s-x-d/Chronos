using Chronos.Service.Apps;

namespace Chronos.Service.Tests;

internal sealed class FakeProcessEvents : IProcessEvents
{
    private Action<ProcessStarted>? _onStarted;

    public string SourceName { get; init; } = "fake";

    public bool IsStarted { get; private set; }

    /// <summary>How often Start was called. Starting twice is a defect, not a redundancy.</summary>
    public int StartCalls { get; private set; }

    public bool IsDisposed { get; private set; }

    public bool IsFaulted { get; set; }

    public Exception? FailToStartWith { get; set; }

    public void Start(Action<ProcessStarted> onStarted)
    {
        StartCalls++;

        if (FailToStartWith is not null)
        {
            throw FailToStartWith;
        }

        _onStarted = onStarted;
        IsStarted = true;
    }

    public void Raise(ProcessStarted started) => _onStarted?.Invoke(started);

    public void Dispose()
    {
        IsDisposed = true;
        IsStarted = false;
        _onStarted = null;
    }
}
