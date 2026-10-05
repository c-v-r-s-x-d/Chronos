using Chronos.Core.Time;

namespace Chronos.Service.Tests;

internal sealed class ServiceTestClock : IClock
{
    // Not a primary constructor parameter: the start is both the first reading and the point Elapsed is measured from, and a parameter used for an initialiser and captured as state is CS9124.
    private readonly DateTimeOffset _start;

    public ServiceTestClock(DateTimeOffset start)
    {
        _start = start;
        UtcNow = start;
    }

    public DateTimeOffset UtcNow { get; set; }

    /// <summary>How much time the test has let pass; what deadline tests assert on, without spending that time.</summary>
    public TimeSpan Elapsed => UtcNow - _start;

    public void Advance(TimeSpan delta) => UtcNow += delta;
}
