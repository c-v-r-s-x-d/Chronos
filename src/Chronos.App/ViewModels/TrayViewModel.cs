using System.ComponentModel;
using System.Globalization;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.Time;
using Chronos.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Chronos.App.ViewModels;

/// <summary>Which picture sits in the notification area. The third answer is for the third situation: calling it either of the others would be a claim the interface cannot make.</summary>
public enum TrayGlyph
{
    /// <summary>Nothing is blocked.</summary>
    Idle,

    /// <summary>A block is in force.</summary>
    Blocking,

    /// <summary>The service has not said, so neither of the other two is true.</summary>
    Unknown,
}

/// <summary>
/// The notification area, worked out here rather than on the control, so it is testable without a
/// windowing system and uses the same plural forms as the screens. The tooltip is all the product
/// says about itself while the window is hidden.
/// </summary>
public sealed class TrayViewModel : ObservableObject, IDisposable
{
    private readonly ServiceClock _clock;

    private ServiceSnapshot _snapshot = ServiceSnapshot.Connecting;

    /// <param name="clock">The same reading of the service's clock the window counts by.</param>
    public TrayViewModel(Text text, ServiceClock clock)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(clock);

        Text = text;
        _clock = clock;

        Text.Changed += OnTextChanged;
    }

    public Text Text { get; }

    public TrayGlyph Glyph => Blocking switch
    {
        true => TrayGlyph.Blocking,
        false => TrayGlyph.Idle,
        _ => TrayGlyph.Unknown,
    };

    /// <summary>
    /// The product's name and what it is doing, in one line. Windows keeps 127 characters of a
    /// tooltip and drops the rest without saying so, which is why the parts are kept short and why
    /// a test measures every one of them.
    /// </summary>
    public string Tooltip => string.Create(Screen, $"{Text.AppTitle}: {Line}");

    public void Show(ServiceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _snapshot = snapshot;

        // The only one of the three things that move this that can change the picture. The glyph
        // is a state: not a word, and not a reading of a clock.
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(Tooltip));
    }

    /// <summary>Re-reads the clock. The remaining time in the tooltip has to keep going down.</summary>
    public void Tick() => OnPropertyChanged(nameof(Tooltip));

    public void Dispose() => Text.Changed -= OnTextChanged;

    private static CultureInfo Screen => CultureInfo.CurrentUICulture;

    /// <summary>Whether a block is in force, or null when the interface cannot answer (no service, or an unknown state word). Unknown is not idle: an icon saying nothing is blocked during a session is worse than one saying it does not know.</summary>
    private bool? Blocking => _snapshot.Status is { } status ? EngineState.Blocks(status.State) : null;

    private string Line => _snapshot.State switch
    {
        ServiceConnectionState.Connecting => Text.TrayConnecting,
        ServiceConnectionState.Available when _snapshot.Status is { } status => Session(status),
        _ => Text.TrayUnknown,
    };

    private string Session(StatusPayload status) => status.State switch
    {
        // A sign that a session is on, and how long is left.
        EngineState.Active =>
            Text.TrayActive(_clock.Until(status.EndsAt)),

        // Ended is the clearing up. Its remaining time is zero by definition, and a tooltip
        // counting zero would be the one line about time refusing to say anything about it.
        EngineState.Ended => Text.SessionFinishing,

        // The waiting screen's own question, and a different field of the status: what matters
        // while the unlock is pending is when the block lifts, not when the session would have
        // ended anyway.
        EngineState.UnlockPending =>
            Text.UnlockRemaining(_clock.Until(status.UnlockEffectiveAt)),

        EngineState.Idle => Text.TrayIdle,

        _ => Text.TrayUnknown,
    };

    // The notification area needs the new words too, and the icon has no redraw of its own to pick
    // them up. Once per change of language, not once per label.
    private void OnTextChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(Tooltip));
}
