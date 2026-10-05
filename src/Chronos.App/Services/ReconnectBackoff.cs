namespace Chronos.App.Services;

/// <summary>How long to wait before asking for the service again. Grows so a missing service is not hammered, capped so a restart of a few seconds is found while the window is still open.</summary>
internal sealed class ReconnectBackoff
{
    private static readonly TimeSpan Shortest = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(5);

    private TimeSpan _wait = Shortest;

    public TimeSpan Next()
    {
        var current = _wait;
        var doubled = _wait + _wait;
        _wait = doubled > Longest ? Longest : doubled;

        return current;
    }

    /// <summary>Called on a connection that worked, so the next outage starts over.</summary>
    public void Reset() => _wait = Shortest;
}
