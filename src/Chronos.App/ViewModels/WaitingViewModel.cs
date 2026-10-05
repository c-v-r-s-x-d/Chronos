using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.Time;
using Chronos.Ipc;
using CommunityToolkit.Mvvm.Input;

namespace Chronos.App.ViewModels;

/// <summary>The unlock has been asked for and has not taken effect yet. The screen shows how long is left and a plain way to change your mind; cancelling is a first-class button because the way out must not be awkward.</summary>
public sealed class WaitingViewModel : ScreenViewModel
{
    private static readonly string[] Countdown = [nameof(Remaining), nameof(RemainingDigits), nameof(RemainingText), nameof(Progress)];

    private readonly IServiceLink _link;

    private readonly ServiceClock _clock;

    private DateTimeOffset? _liftsAt;

    // How long the wait is in all, so the sand is measured against the cool-down and not the session.
    private TimeSpan _coolDown;

    public WaitingViewModel(IServiceLink link, Text text, ServiceClock clock)
        : base(text)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(clock);

        _link = link;
        _clock = clock;
        CancelCommand = new AsyncRelayCommand(CancelAsync);
    }

    public IAsyncRelayCommand CancelCommand { get; }

    /// <summary>To the moment the block lifts, which is not the moment the session ends.</summary>
    public TimeSpan Remaining => _clock.Until(_liftsAt);

    /// <summary>The sentence, for a screen reader; the page shows the digits under its label.</summary>
    public string RemainingText => Text.UnlockRemaining(Remaining);

    public string RemainingDigits => Duration.Digits(Remaining);

    /// <summary>The sand that has fallen, 0..1, of the wait.</summary>
    public double Progress => Elapsed.Share(_liftsAt - _coolDown, _liftsAt, _clock.Now);

    public override void Show(StatusPayload status)
    {
        ArgumentNullException.ThrowIfNull(status);

        _liftsAt = status.UnlockEffectiveAt;
        _coolDown = TimeSpan.FromMinutes(status.CoolDownMinutes);
        Tick();
    }

    public override void Tick()
    {
        foreach (var property in Countdown)
        {
            OnPropertyChanged(property);
        }
    }

    /// <summary>
    /// The remaining time itself is a span and does not care what language it is read in; the
    /// sentence around it does.
    /// </summary>
    public override void Reword() => OnPropertyChanged(nameof(RemainingText));


    private async Task CancelAsync()
    {
        await _link.SendAsync(
            new IpcRequest { Command = ServiceCommand.CancelUnlock },
            CancellationToken.None).ConfigureAwait(true);
    }
}
