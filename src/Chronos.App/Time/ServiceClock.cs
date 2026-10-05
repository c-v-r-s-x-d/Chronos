namespace Chronos.App.Time;

/// <summary>
/// The service's clock as this machine can tell it. Moments in a status are readings of the
/// service's clock, which may differ from this one. The difference is measured against the
/// <c>Now</c> each status carries and re-measured on every status; the local clock supplies the ticking.
/// </summary>
public sealed class ServiceClock
{
    private readonly IClock _local;

    private TimeSpan _difference;

    public ServiceClock(IClock local)
    {
        ArgumentNullException.ThrowIfNull(local);

        _local = local;
    }

    /// <summary>What time it is where the service is.</summary>
    public DateTimeOffset Now => _local.UtcNow + _difference;

    public void Follow(DateTimeOffset serviceNow) => _difference = serviceNow - _local.UtcNow;

    /// <summary>Time left until a moment on the service's clock, floored at zero so the screen does not count through a state change not yet announced. A null moment is a countdown to nothing.</summary>
    public TimeSpan Until(DateTimeOffset? moment)
    {
        if (moment is not { } deadline)
        {
            return TimeSpan.Zero;
        }

        var left = deadline - Now;

        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }
}
