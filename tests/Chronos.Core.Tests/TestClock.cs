using Chronos.Core.Time;

namespace Chronos.Core.Tests;

internal sealed class TestClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = start;

    public void Advance(TimeSpan delta) => UtcNow += delta;
}
