namespace Chronos.Core.Sessions;

public readonly record struct ClampResult(TimeSpan Value, bool WasClamped);

public static class SessionLimits
{
    public static TimeSpan MinSessionDuration { get; } = TimeSpan.FromMinutes(5);

    public static TimeSpan MaxSessionDuration { get; } = TimeSpan.FromHours(24);

    public static TimeSpan DefaultSessionDuration { get; } = TimeSpan.FromMinutes(60);

    public static TimeSpan MinCoolDown { get; } = TimeSpan.FromMinutes(1);

    public static TimeSpan MaxCoolDown { get; } = TimeSpan.FromMinutes(60);

    public static TimeSpan DefaultCoolDown { get; } = TimeSpan.FromMinutes(1);

    public static ClampResult ClampSessionDuration(TimeSpan value) =>
        Clamp(value, MinSessionDuration, MaxSessionDuration);

    public static ClampResult ClampCoolDown(TimeSpan value) =>
        Clamp(value, MinCoolDown, MaxCoolDown);

    private static ClampResult Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
        {
            return new ClampResult(min, WasClamped: true);
        }

        return value > max
            ? new ClampResult(max, WasClamped: true)
            : new ClampResult(value, WasClamped: false);
    }
}
