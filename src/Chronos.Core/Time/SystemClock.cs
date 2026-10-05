namespace Chronos.Core.Time;

/// <summary>The only place in Chronos.Core allowed to read the system clock.</summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
