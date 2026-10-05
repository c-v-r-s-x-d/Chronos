namespace Chronos.Core.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
