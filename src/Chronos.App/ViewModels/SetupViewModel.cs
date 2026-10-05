using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.Time;
using Chronos.Ipc;
using CommunityToolkit.Mvvm.Input;

namespace Chronos.App.ViewModels;

/// <summary>
/// No session is running. The screen where one is set up and started, and where the person's lists
/// are edited freely. What this machine can block is on <see cref="SettingsViewModel"/>.
///
/// <para>The rules are asked for with <c>GetConfig</c>, because <see cref="StatusPayload.Sites"/>
/// and <see cref="StatusPayload.Apps"/> are the rules of the running session and are empty when
/// there is none.</para>
/// </summary>
public sealed class SetupViewModel : RuleScreenViewModel
{
    /// <summary>
    /// The length in the box before the service has said what it defaults to. A starting point in a
    /// box the person can change, not a rule: the length that reaches the service is whatever is in
    /// the box, and the service clamps it.
    /// </summary>
    public const int DefaultMinutes = 60;

    /// <summary>
    /// The service's shortest and longest session (SessionLimits), copied: the interface may not
    /// reference the domain. SessionBoundsTests keeps the copies equal.
    /// </summary>
    public const int MinMinutes = 5;

    public const int MaxMinutes = 1440;

    private static readonly int[] Offered = [30, 60, 120, 240];

    private readonly ServiceClock _clock;

    private IReadOnlyList<DurationLine> _durations = [];

    private int _selected;

    private int _minutes = DefaultMinutes;

    // Whether the number in the box is the person's answer or the service's suggestion. Without
    // this, every status arriving while somebody decides how long to sit down for puts it back.
    private bool _lengthIsTheirs;

    // Set while the chips have just been replaced. The control drops its selection with the old
    // items, and a binding passes on only a value that differs from the last one it read - so the
    // selection is read as none once, and the announcement after that is a change it passes on.
    private bool _durationsReplaced;

    public SetupViewModel(IServiceLink link, Text text, ServiceClock clock)
        : base(link, text)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
        _selected = IndexOf(_minutes);
        StartCommand = new AsyncRelayCommand(StartAsync);

        Say();
    }

    public IAsyncRelayCommand StartCommand { get; }

    public int Minutes
    {
        get => _minutes;
        set
        {
            _lengthIsTheirs = true;

            if (SetProperty(ref _minutes, Clamp(value)))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    /// <summary>The four lengths on offer and the one that means "type your own".</summary>
    public IReadOnlyList<DurationLine> Durations => _durations;

    /// <summary>A position in <see cref="Durations"/>, for the list control.</summary>
    public int SelectedDuration
    {
        get => _durationsReplaced ? -1 : _selected;
        set
        {
            // A list control handed new items reports no selection and writes back -1.
            if (value < 0 || value >= _durations.Count || !SetProperty(ref _selected, value))
            {
                return;
            }

            _lengthIsTheirs = true;

            if (!_durations[value].IsCustom)
            {
                _minutes = _durations[value].Minutes;
                OnPropertyChanged(nameof(Minutes));
            }

            OnPropertyChanged(nameof(IsCustomDuration));
            OnPropertyChanged(nameof(Summary));
        }
    }

    public bool IsCustomDuration => _selected == Offered.Length;

    /// <summary>Both lists are empty: a session would start and block nothing.</summary>
    public bool NothingToBlock => Sites.Count == 0 && Apps.Count == 0;

    /// <summary>What a session started now would block and until when; empty when it would block nothing.</summary>
    public string Summary => NothingToBlock
        ? string.Empty
        : Text.Summary(Sites.Count, Apps.Count, _clock.Now + TimeSpan.FromMinutes(Minutes));

    /// <summary>The end moment moves with the clock and nothing else on this screen does.</summary>
    public override void Tick() => OnPropertyChanged(nameof(Summary));

    /// <summary>
    /// Everything on this screen is words and lists of words, so a language change rebuilds all of
    /// it.
    /// </summary>
    public override void Reword()
    {
        Say();
        base.Reword();
        OnPropertyChanged(nameof(Summary));
    }

    protected override void Adopted()
    {
        OnPropertyChanged(nameof(NothingToBlock));
        OnPropertyChanged(nameof(Summary));
    }

    protected override void Took(ConfigPayload config)
    {
        if (!_lengthIsTheirs && _minutes != Clamp(config.DefaultSessionMinutes))
        {
            _minutes = Clamp(config.DefaultSessionMinutes);
            _selected = IndexOf(_minutes);
            OnPropertyChanged(nameof(Minutes));
            OnPropertyChanged(nameof(SelectedDuration));
            OnPropertyChanged(nameof(IsCustomDuration));
        }

        OnPropertyChanged(nameof(NothingToBlock));
        OnPropertyChanged(nameof(Summary));
    }

    // The service's range, which the box allows too; outside it the summary would promise an end
    // the session will not have.
    private static int Clamp(int minutes) => Math.Clamp(minutes, MinMinutes, MaxMinutes);

    // The chip with this length, or the custom one at the end.
    private static int IndexOf(int minutes)
    {
        var at = Array.IndexOf(Offered, minutes);

        return at >= 0 ? at : Offered.Length;
    }

    private void Say()
    {
        _durations =
        [
            .. Offered.Select(minutes => new DurationLine(minutes, Duration.Say(TimeSpan.FromMinutes(minutes)), false)),
            new DurationLine(_minutes, Text.CustomDuration, true),
        ];

        // The list first and the selection right after it: the control drops its selection when
        // the items are replaced. Said twice, as none and then as what it is: see the flag.
        _durationsReplaced = true;
        try
        {
            OnPropertyChanged(nameof(Durations));
            OnPropertyChanged(nameof(SelectedDuration));
        }
        finally
        {
            _durationsReplaced = false;
        }

        OnPropertyChanged(nameof(SelectedDuration));
        OnPropertyChanged(nameof(IsCustomDuration));
    }

    private async Task StartAsync()
    {
        // Nothing is done with the answer, and that is on purpose: the screen changes when the
        // subscription carries the new state, so there is one path from a state to a screen instead
        // of two that can disagree.
        await SendAsync(new IpcRequest { Command = ServiceCommand.StartSession, DurationMinutes = Minutes })
            .ConfigureAwait(true);
    }
}
