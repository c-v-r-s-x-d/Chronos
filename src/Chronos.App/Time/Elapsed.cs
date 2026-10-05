namespace Chronos.App.Time;

/// <summary>How much of a stretch of time has gone: the sand that has fallen.</summary>
public static class Elapsed
{
    /// <summary>
    /// The share of start..end that is behind <paramref name="now"/>, kept inside 0..1. A missing
    /// moment or an end not after the start is no sand rather than a guess.
    /// </summary>
    public static double Share(DateTimeOffset? start, DateTimeOffset? end, DateTimeOffset now)
    {
        if (start is not { } from || end is not { } to || to <= from)
        {
            return 0;
        }

        return Math.Clamp((now - from) / (to - from), 0, 1);
    }
}
