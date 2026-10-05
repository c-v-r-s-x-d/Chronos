using System.ComponentModel;
using System.Globalization;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.Time;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Chronos.App.ViewModels;

/// <summary>
/// Which application was closed or site refused, and how much of the session is left. Both come
/// from the event: this object has no link, since asking again would cost a round trip at the
/// busiest moment and answer about a later moment than the block.
/// </summary>
public sealed class BlockViewModel : ObservableObject, IDisposable
{
    /// <summary>What the clock changes. The heading names what was refused and never moves.</summary>
    private static readonly string[] Countdown = [nameof(Remaining), nameof(RemainingText), nameof(Progress)];

    /// <summary>Both sentences on this window are built here rather than named in the markup.</summary>
    private static readonly string[] Words = [nameof(Heading), nameof(RemainingText), nameof(EndsAtText)];

    private readonly IClock _clock;

    private readonly ITicker _ticker;

    /// <summary>How much was left when the service closed the application: the difference between two moments the service read together, so a skewed local clock does not matter.</summary>
    private readonly TimeSpan _leftWhenBlocked;

    // A site and an application share the window and differ only in the sentence at its top.
    private readonly bool _isSite;

    // The session's own two moments from the event, and the service's clock at the block. Neither
    // is compared with this machine's clock; only the time since the window opened is.
    private readonly DateTimeOffset? _startedAt;

    private readonly DateTimeOffset? _endsAt;

    private readonly DateTimeOffset _serviceNow;

    /// <summary>This machine's clock at the moment the screen appeared, for the ticking down.</summary>
    private readonly DateTimeOffset _opened;

    public BlockViewModel(BlockEvent block, Text text, IClock clock, ITicker ticker)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ticker);

        _clock = clock;
        _ticker = ticker;
        Text = text;
        Target = block.Target;
        _isSite = block is SiteBlock;

        _startedAt = block.Status.StartedAt;
        _endsAt = block.Status.EndsAt;
        _serviceNow = block.Status.Now;
        _leftWhenBlocked = Left(block);
        _opened = clock.UtcNow;

        ticker.Ticked += OnTicked;
        Text.Changed += OnLanguageChanged;
    }

    /// <summary>The words the markup binds to directly.</summary>
    public Text Text { get; }

    /// <summary>
    /// What was refused, as the service reported it: an executable's name off the person's own list,
    /// or a domain from an attempt. Both are shown; only the first may be logged.
    /// </summary>
    public string Target { get; }

    public string Heading => string.Format(Screen, _isSite ? Text.BlockedSiteFormat : Text.BlockedAppFormat, Target);

    /// <summary>
    /// Counted down from what the event said, never below zero. A window left open past the end of
    /// the session would otherwise start counting up through a state nobody has announced.
    /// </summary>
    public TimeSpan Remaining
    {
        get
        {
            var gone = _clock.UtcNow - _opened;
            var left = _leftWhenBlocked - gone;

            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    public string RemainingText => Text.SessionRemaining(Remaining);

    /// <summary>The sand that has fallen, 0..1, on the service's clock moved on by the time the window has been open.</summary>
    public double Progress => Elapsed.Share(_startedAt, _endsAt, _serviceNow + (_clock.UtcNow - _opened));

    /// <summary>When the session ends, as a time of day. Empty with no end to name.</summary>
    public string EndsAtText => _endsAt is { } end
        ? string.Format(Screen, Text.EndsAtFormat, end.ToLocalTime().ToString("t", Screen))
        : string.Empty;

    public void Dispose()
    {
        _ticker.Ticked -= OnTicked;
        Text.Changed -= OnLanguageChanged;
    }

    private static CultureInfo Screen => CultureInfo.CurrentUICulture;

    /// <summary>
    /// The two moments the event carried, subtracted from one another. A block with no end time is
    /// a session that has none, and a countdown to nothing is zero rather than a guess.
    /// </summary>
    private static TimeSpan Left(BlockEvent block)
    {
        if (block.Status.EndsAt is not { } ends)
        {
            return TimeSpan.Zero;
        }

        var left = ends - block.Status.Now;

        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private void OnTicked(object? sender, EventArgs e) => Announce(Countdown);

    // A language change reaches these only if this says so - and once, rather than
    // once for each of the labels the markup binds by name.
    private void OnLanguageChanged(object? sender, EventArgs e) => Announce(Words);

    private void Announce(string[] properties)
    {
        foreach (var property in properties)
        {
            OnPropertyChanged(property);
        }
    }
}
