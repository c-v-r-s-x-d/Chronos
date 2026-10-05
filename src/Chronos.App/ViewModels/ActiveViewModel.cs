using System.Globalization;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.Time;
using Chronos.Ipc;
using CommunityToolkit.Mvvm.Input;

namespace Chronos.App.ViewModels;

/// <summary>A session is running: remaining time, the way to ask for it to stop, the lists it enforces. The Ended state lands here too, with a word saying the session is finishing.</summary>
public sealed class ActiveViewModel : RuleScreenViewModel
{
    /// <summary>A quarter of an hour in the box to start with, and the person can change it.</summary>
    public const int DefaultExtendMinutes = 15;

    /// <summary>What the clock changes, and the whole of what a tick may touch.</summary>
    private static readonly string[] Countdown = [nameof(Remaining), nameof(RemainingDigits), nameof(RemainingText), nameof(Progress)];

    /// <summary>What is built here out of words rather than named in the markup.</summary>
    private static readonly string[] Words =
    [
        nameof(RemainingText),
        nameof(CoolDownNotice),
        nameof(ShorteningUnavailable),
        nameof(EndsAtText),
        nameof(RemainingCaption),
    ];

    /// <summary>The lengths on offer as chips; the person can type another one under "Custom".</summary>
    private static readonly int[] Offered = [15, 30, 60];

    /// <summary>And what a new status changes: the countdown's target, and these.</summary>
    private static readonly string[] News = [nameof(IsFinishing), nameof(CoolDownNotice), nameof(EndsAtText), nameof(RemainingCaption), nameof(Progress)];

    private readonly ServiceClock _clock;

    private DateTimeOffset? _startedAt;

    private DateTimeOffset? _endsAt;

    private bool _isFinishing;

    private int _coolDownMinutes;

    private int _extendMinutes = DefaultExtendMinutes;

    private bool _isCustomExtendOpen;

    private IReadOnlyList<DurationLine> _extendChoices;

    public ActiveViewModel(IServiceLink link, Text text, ServiceClock clock)
        : base(link, text)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
        RequestUnlockCommand = new AsyncRelayCommand(RequestUnlockAsync);
        ExtendCommand = new AsyncRelayCommand(ExtendAsync);
        ExtendByCommand = new AsyncRelayCommand<DurationLine>(ExtendByAsync);
        _extendChoices = Choices();
    }

    /// <summary>Asks for the block to be lifted. The wait itself is the waiting screen's business.</summary>
    public IAsyncRelayCommand RequestUnlockCommand { get; }

    /// <summary>Allowed, and the only direction the end time ever moves.</summary>
    public IAsyncRelayCommand ExtendCommand { get; }

    /// <summary>One chip: lengthens the session by its own length, with nothing to type.</summary>
    public IAsyncRelayCommand<DurationLine> ExtendByCommand { get; }

    /// <summary>Counted here rather than taken from the last status, or the timer stands still.</summary>
    public TimeSpan Remaining => _clock.Until(_endsAt);

    /// <summary>The sentence: the tray and a screen reader say this, the page shows the digits.</summary>
    public string RemainingText => Text.SessionRemaining(Remaining);

    /// <summary>The timer on the page, as clock digits.</summary>
    public string RemainingDigits => Duration.Digits(Remaining);

    /// <summary>Under the digits: "left · until 14:13", or "left" with no end to name.</summary>
    public string RemainingCaption => EndsAtText is { Length: > 0 } until
        ? string.Format(CultureInfo.CurrentUICulture, Text.RemainingCaptionFormat, until)
        : Text.RemainingCaption;

    /// <summary>The Ended state, said in a word instead of in a screen.</summary>
    public bool IsFinishing => _isFinishing;

    /// <summary>The sand that has fallen, 0..1: all of it once the session is finishing.</summary>
    public double Progress => _isFinishing ? 1 : Elapsed.Share(_startedAt, _endsAt, _clock.Now);

    /// <summary>When it ends, as a time of day. Empty with no end to name.</summary>
    public string EndsAtText => _endsAt is { } end
        ? string.Format(CultureInfo.CurrentUICulture, Text.EndsAtFormat, end.ToLocalTime().ToString("t", CultureInfo.CurrentUICulture))
        : string.Empty;

    /// <summary>Made again on a language change, so the names follow it.</summary>
    public IReadOnlyList<DurationLine> ExtendChoices => _extendChoices;

    /// <summary>Whether the box for a length of one's own is open.</summary>
    public bool IsCustomExtendOpen
    {
        get => _isCustomExtendOpen;
        set => SetProperty(ref _isCustomExtendOpen, value);
    }

    public int ExtendMinutes
    {
        get => _extendMinutes;
        set => SetProperty(ref _extendMinutes, value);
    }

    /// <summary>Why the box only goes one way, said where the extending is.</summary>
    public string ShorteningUnavailable => Text.ShorteningUnavailable;

    /// <summary>What asking for the block to be lifted will cost in waiting, said before asking.</summary>
    public string CoolDownNotice => Text.CoolDownNotice(TimeSpan.FromMinutes(_coolDownMinutes));

    public override void Show(StatusPayload status)
    {
        ArgumentNullException.ThrowIfNull(status);

        _startedAt = status.StartedAt;
        _endsAt = status.EndsAt;
        _isFinishing = status.State == EngineState.Ended;

        // The running session's cool-down, which is the one it began with whatever the file says
        // now. With no session it is the file's, and this screen is not shown then.
        _coolDownMinutes = status.CoolDownMinutes;

        base.Show(status);

        foreach (var property in News)
        {
            OnPropertyChanged(property);
        }

        Tick();
    }

    public override void Tick()
    {
        foreach (var property in Countdown)
        {
            OnPropertyChanged(property);
        }
    }

    public override void Reword()
    {
        _extendChoices = Choices();
        OnPropertyChanged(nameof(ExtendChoices));

        foreach (var property in Words)
        {
            OnPropertyChanged(property);
        }

        base.Reword();
    }


    private async Task RequestUnlockAsync()
    {
        await SendAsync(new IpcRequest { Command = ServiceCommand.RequestUnlock }).ConfigureAwait(true);
    }

    private Task ExtendAsync() => ExtendByMinutesAsync(ExtendMinutes);

    private Task ExtendByAsync(DurationLine? chip) => chip is null ? Task.CompletedTask : ExtendByMinutesAsync(chip.Minutes);

    private IReadOnlyList<DurationLine> Choices() =>
        [.. Offered.Select(minutes => new DurationLine(minutes, Duration.Say(TimeSpan.FromMinutes(minutes)), false))];

    private async Task ExtendByMinutesAsync(int minutes)
    {
        // Only extensions leave this screen; the engine would refuse anything else, and a foreseeable
        // refusal looks like a broken button.
        if (minutes <= 0)
        {
            return;
        }

        await SendAsync(new IpcRequest
        {
            Command = ServiceCommand.ExtendSession,
            DurationMinutes = minutes,
        }).ConfigureAwait(true);
    }
}
