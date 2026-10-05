using System.Reflection;
using Chronos.App.Blocking;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Chronos.App.ViewModels;

/// <summary>What the sidebar says about protection: on or off, and whether something is wrong.</summary>
public enum ProtectionState
{
    /// <summary>The service has reported on no mechanism yet.</summary>
    Unknown,

    /// <summary>No session; every mechanism could work.</summary>
    Off,

    /// <summary>No session, and a mechanism could not work if one started.</summary>
    OffWithProblem,

    /// <summary>A session is blocking and every mechanism works.</summary>
    On,

    /// <summary>A session is blocking and a mechanism does not work.</summary>
    Partial,
}

/// <summary>
/// The "Settings" section: what this machine can actually block, the
/// sidebar's summary of it, and the language. One instance for both the
/// setup and the active screen, fed every status.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly IServiceLink _link;

    private IReadOnlyList<LayerStatus> _reported = [];

    // Whether a session is blocking: it decides whether the mechanisms are "working" or only "available".
    private bool _blocking;

    private IReadOnlyList<LayerLine> _layers = [];

    // The three choices in words. Never replaced: a language change is most often the control's
    // own doing, and a control handed new items in the middle of writing its selection puts the
    // old one back. One of the three names is a translated phrase, and is rewritten in place.
    private readonly IReadOnlyList<LanguageLine> _languages = LanguageChoice.Lines();

    // The position last announced. The stored choice can move without the words changing - to
    // following a machine that is in the language already in use - and a status then says so.
    private int _saidLanguage = -1;

    public SettingsViewModel(IServiceLink link, Text text)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(text);

        _link = link;
        Text = text;
        Text.Changed += OnLanguageChanged;

        Say();
    }

    /// <summary>The words the markup binds to directly.</summary>
    public Text Text { get; }

    /// <summary>The layers the service has reported on, in words. A layer it has not is not here.</summary>
    public IReadOnlyList<LayerLine> Layers => _layers;

    /// <summary>
    /// False when no pass has reported on anything yet, which is not the same as nothing working.
    /// The screen says which of the two it is instead of showing an empty list that reads as either.
    /// </summary>
    public bool AnyLayerReported => _layers.Count > 0;

    /// <summary>
    /// Whether notices about sites can come, read off the dns layer's state. False while
    /// it is unavailable or unreported, and the screen then says why.
    /// </summary>
    public bool SiteNoticesAvailable => BlockNotices.SitesAreNoticed(_reported);

    public ProtectionState Protection => (_reported.Count, _blocking, _reported.All(l => l.IsAvailable)) switch
    {
        (0, _, _) => ProtectionState.Unknown,
        (_, false, true) => ProtectionState.Off,
        (_, false, false) => ProtectionState.OffWithProblem,
        (_, true, true) => ProtectionState.On,
        _ => ProtectionState.Partial,
    };

    /// <summary>On or off in words; no counts, since nothing is protected before a session starts.</summary>
    public string ProtectionSummary => Protection switch
    {
        ProtectionState.Unknown => Text.ProtectionUnknown,
        ProtectionState.On => Text.ProtectionOn,
        ProtectionState.Partial => Text.ProtectionPartial,
        _ => Text.ProtectionOff,
    };

    /// <summary>The build's version, without the commit suffix the SDK appends.</summary>
    public string Version { get; } =
        (typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? string.Empty).Split('+')[0];

    /// <summary>"Protection" during a session, "Blocking methods" before one.</summary>
    public string ProtectionHeading => _blocking ? Text.ProtectionHeading : Text.BlockingMethodsHeading;

    public bool ProtectionOn => Protection == ProtectionState.On;

    public bool ProtectionWarning => Protection is ProtectionState.Partial or ProtectionState.OffWithProblem;

    public bool ProtectionNeutral => Protection is ProtectionState.Off or ProtectionState.Unknown;

    /// <summary>
    /// The languages on offer, English and Russian and following the machine, each
    /// naming itself.
    /// </summary>
    public IReadOnlyList<LanguageLine> Languages => _languages;

    /// <summary>
    /// Which one is chosen, as a position in <see cref="Languages"/>. Read straight off the switch,
    /// since the language can change without this control being touched. Setting it changes the
    /// interface language at once and then asks the service to keep the choice.
    /// </summary>
    public int SelectedLanguage
    {
        get => LanguageChoice.IndexOf(Text.Language);
        set
        {
            if (value < 0 || value >= _languages.Count)
            {
                // What a list control reports while its items are being replaced. The choice it is
                // about to be asked for again has not changed, so there is nothing to do about it.
                return;
            }

            var choice = _languages[value].Choice;

            if (string.Equals(choice, Text.Language, StringComparison.Ordinal))
            {
                return;
            }

            Text.SwitchTo(choice);
            Saving = SaveLanguageAsync(choice);
        }
    }

    /// <summary>
    /// The round trip that keeps the language, for a test to wait on. The screen does not: the
    /// words have already changed by the time this starts.
    /// </summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    /// <summary>A new status. Not called by the tick: nothing here is read off a clock.</summary>
    public void Show(StatusPayload status)
    {
        ArgumentNullException.ThrowIfNull(status);

        // Kept as the service sent it and turned into words on every redraw, so a language change
        // rewrites the lines rather than leaving them in the language of the last status.
        _reported = status.Layers;
        _blocking = EngineState.Blocks(status.State) is true;
        Say();

        if (_saidLanguage != SelectedLanguage)
        {
            SayLanguage();
        }
    }

    public void Dispose() => Text.Changed -= OnLanguageChanged;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Say();

        foreach (var line in _languages)
        {
            line.Name = LanguageChoice.Name(line.Choice);
        }

        SayLanguage();
    }

    private void SayLanguage()
    {
        _saidLanguage = SelectedLanguage;
        OnPropertyChanged(nameof(SelectedLanguage));
    }

    private void Say()
    {
        _layers = [.. _reported.Select(layer => LayerReason.Describe(layer, _blocking))];

        OnPropertyChanged(nameof(Layers));
        OnPropertyChanged(nameof(AnyLayerReported));
        OnPropertyChanged(nameof(SiteNoticesAvailable));
        OnPropertyChanged(nameof(Protection));
        OnPropertyChanged(nameof(ProtectionSummary));
        OnPropertyChanged(nameof(ProtectionHeading));
        OnPropertyChanged(nameof(ProtectionOn));
        OnPropertyChanged(nameof(ProtectionWarning));
        OnPropertyChanged(nameof(ProtectionNeutral));
    }

    private async Task SaveLanguageAsync(string choice)
    {
        await _link.SendAsync(
            new IpcRequest
            {
                Command = ServiceCommand.UpdateSettings,
                Settings = new SettingsMessage(Language: choice),
            },
            CancellationToken.None).ConfigureAwait(true);
    }
}
