using Chronos.App.Time;

namespace Chronos.App.Blocking;

/// <summary>
/// Decides which block events are worth a window. The limit is per target: a global one would hide
/// a second program's block behind the first. Not thread-safe; <see cref="BlockNotices"/> calls it
/// from the interface thread only.
/// </summary>
public sealed class BlockScreenRate
{
    /// <summary>"Several minutes", read as five.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly IClock _clock;

    private readonly TimeSpan _window;

    // Ordinal-ignore-case: Windows file names, steam.exe and Steam.exe are one program.
    private readonly Dictionary<string, DateTimeOffset> _lastShown =
        new(StringComparer.OrdinalIgnoreCase);

    public BlockScreenRate(IClock clock, TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
        _window = window ?? Window;
    }

    /// <summary>Whether a screen is due for this target. Answering true records the showing.</summary>
    public bool Allow(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var now = _clock.UtcNow;

        // Entries older than the window never suppress again. Swept here so a program that renames
        // itself on every launch cannot grow the map.
        Forget(now);

        if (_lastShown.TryGetValue(target, out var shown) && now - shown < _window)
        {
            return false;
        }

        _lastShown[target] = now;

        return true;
    }

    private void Forget(DateTimeOffset now)
    {
        if (_lastShown.Count == 0)
        {
            return;
        }

        var stale = _lastShown
            .Where(entry => now - entry.Value >= _window)
            .Select(entry => entry.Key)
            .ToArray();

        foreach (var target in stale)
        {
            _lastShown.Remove(target);
        }
    }
}
