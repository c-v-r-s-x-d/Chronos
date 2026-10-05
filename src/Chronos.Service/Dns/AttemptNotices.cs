using Chronos.Core.Time;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Dns;

/// <summary>
/// Passes on at most one blocked attempt per domain per five minutes.
/// </summary>
/// <remarks>
/// Runs on the resolver's answer path, so <see cref="Notify"/> never throws; sink exceptions are caught.
/// </remarks>
public sealed class AttemptNotices
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>Domains remembered at once. Past it, with none expired, new domains are dropped.</summary>
    public const int Capacity = 4096;

    private readonly Action<string> _sink;
    private readonly IClock _clock;
    private readonly ILogger<AttemptNotices> _logger;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSent = new(StringComparer.OrdinalIgnoreCase);

    public AttemptNotices(Action<string> sink, IClock clock, ILogger<AttemptNotices> logger)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _sink = sink;
        _clock = clock;
        _logger = logger;
    }

    public void Notify(string domain)
    {
        if (domain is null)
        {
            return;
        }

        var now = _clock.UtcNow;

        lock (_sync)
        {
            if (_lastSent.TryGetValue(domain, out var last))
            {
                if (now - last < Interval)
                {
                    return;
                }
            }
            else if (_lastSent.Count >= Capacity && !MadeRoom(now))
            {
                return;
            }

            // Spent before the sink runs, so a sink that throws is not asked again on every retry.
            _lastSent[domain] = now;
        }

        try
        {
            _sink(domain);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "The notice of a blocked attempt at {Domain} was not delivered.", domain);
        }
    }

    private bool MadeRoom(DateTimeOffset now)
    {
        foreach (var expired in _lastSent.Where(entry => now - entry.Value >= Interval).Select(entry => entry.Key).ToList())
        {
            _lastSent.Remove(expired);
        }

        return _lastSent.Count < Capacity;
    }
}
