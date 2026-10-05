using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.Time;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chronos.App.ViewModels;

/// <summary>
/// The window itself: what it says about the link, and which of the three screens is in front. It
/// holds a snapshot rather than reading through to the service, and no status at all while the
/// service is away.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    // Everything on this screen is a reading of the same snapshot, so one change moves all of it.
    private static readonly string[] Everything =
    [
        nameof(IsServiceAvailable),
        nameof(IsConnecting),
        nameof(IsServiceUnavailable),
        nameof(RetryText),
        nameof(CurrentScreen),
        nameof(ServiceMessage),
        nameof(Page),
        nameof(ShowsSidebar),
        nameof(RulesLocked),
    ];

    private readonly IServiceLink _link;

    private readonly ITicker _ticker;

    private readonly IClock _local;

    private readonly ServiceClock _clock;

    private readonly SetupViewModel _setup;

    private readonly ActiveViewModel _active;

    private readonly WaitingViewModel _waiting;

    // The list sections over each rule screen, built once next to the screens they read.
    private readonly SitesSection _setupSites;

    private readonly SitesSection _activeSites;

    private readonly AppsSection _setupApps;

    private readonly AppsSection _activeApps;

    private readonly Action<Action> _post;

    private ServiceSnapshot _snapshot;

    // When the link will next try, on this machine's clock; null until it says so.
    private DateTimeOffset? _retryAt;

    private ScreenViewModel? _screen;

    // The rule screen last in front, which the next one takes over from; kept across a wait.
    private RuleScreenViewModel? _rules;

    private Section _section = Section.Session;

    // Set while the sidebar has just been replaced. The control drops its selection with the old
    // items, and a binding passes on only a value that differs from the last one it read - so the
    // selection is read as none once, and the announcement after that is a change it passes on.
    private bool _sectionsReplaced;

    private IReadOnlyList<SectionLine> _sections = [];

    /// <param name="clock">This machine's clock; countdowns are measured against it, offset by the service's own time.</param>
    /// <param name="quit">Ends the process. Closing the window only hides it and quitting asks first, so the one way out is here and a test can watch it.</param>
    /// <param name="post">Hands work to the interface thread. Injected so a view model needs no Avalonia.</param>
    public ShellViewModel(
        IServiceLink link,
        Text text,
        IClock clock,
        ITicker ticker,
        Action? quit = null,
        Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ticker);

        _link = link;
        _ticker = ticker;
        Text = text;
        _post = post ?? (static action => action());
        _snapshot = link.Snapshot;

        _local = clock;
        _clock = new ServiceClock(clock);

        // The notification area reads the same snapshot and clock as the window: two readings would be two moments in time.
        Tray = new TrayViewModel(text, _clock);

        // The exit warning asks a question and waits for the answer, so the decision is here, not in a handler.
        Exit = new ExitViewModel(text, BlockIsInForce, quit ?? (static () => { }));

        // Built once and kept. A screen rebuilt on every status would throw away what the person
        // was in the middle of typing on it, and there are only three of them.
        _setup = new SetupViewModel(link, text, _clock);
        _active = new ActiveViewModel(link, text, _clock);
        _waiting = new WaitingViewModel(link, text, _clock);

        _setupSites = new SitesSection(_setup);
        _activeSites = new SitesSection(_active);
        _setupApps = new AppsSection(_setup);
        _activeApps = new AppsSection(_active);

        // One for both screens: the settings are the same whether a session runs or not.
        Settings = new SettingsViewModel(link, text);

        GoToCommand = new RelayCommand<Section>(section => Section = section);

        Say();

        link.Changed += OnLinkChanged;
        link.Retrying += OnRetrying;
        ticker.Ticked += OnTicked;

        // The message below is chosen here rather than named in the markup, so the language change
        // that reaches Text has to be passed on by hand for it to reach the window.
        Text.Changed += OnLanguageChanged;
    }

    /// <summary>The icon in the notification area and the words under it.</summary>
    public TrayViewModel Tray { get; }

    /// <summary>The way out, and what it costs.</summary>
    public ExitViewModel Exit { get; }

    /// <summary>The words the markup binds to directly, for everything that does not depend on state.</summary>
    public Text Text { get; }

    /// <summary>The "Settings" section, and the sidebar's protection summary.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Opens a section from anywhere on a page, the summary included.</summary>
    public IRelayCommand<Section> GoToCommand { get; }

    /// <summary>
    /// The section in front. Navigation, not state: a status never changes it, and
    /// the waiting screen, which has no sections, keeps it for when the wait is cancelled.
    /// </summary>
    public Section Section
    {
        get => _section;
        set
        {
            if (!Enum.IsDefined(value) || !SetProperty(ref _section, value))
            {
                return;
            }

            // A notice is about the page it was given on.
            ForgetNotices();

            OnPropertyChanged(nameof(SelectedSectionIndex));
            OnPropertyChanged(nameof(Page));
        }
    }

    /// <summary>The sidebar entries in words, with the lock on the lists during a session.</summary>
    public IReadOnlyList<SectionLine> Sections => _sections;

    /// <summary>
    /// <see cref="Section"/> as a position in <see cref="Sections"/>, for the list control. A position
    /// outside the list is what the control reports while its items are replaced, and is ignored.
    /// </summary>
    public int SelectedSectionIndex
    {
        get => _sectionsReplaced ? -1 : (int)_section;
        set
        {
            if (value >= 0 && value < _sections.Count)
            {
                Section = _sections[value].Section;
            }
        }
    }

    /// <summary>What fills the window beside the sidebar: the screen, or one of its sections.</summary>
    // Waiting has no sections; the section is kept for when it ends.
    public object? Page => _screen switch
    {
        null => null,
        WaitingViewModel waiting => waiting,
        var rules when _section == Section.Sites => rules == _active ? _activeSites : _setupSites,
        var rules when _section == Section.Apps => rules == _active ? _activeApps : _setupApps,
        _ when _section == Section.Settings => Settings,
        var screen => screen,
    };

    /// <summary>The sidebar is on the setup and active screens and never while waiting.</summary>
    public bool ShowsSidebar => _screen is SetupViewModel or ActiveViewModel;

    /// <summary>Whether the lists are locked against removal, for the sidebar's lock.</summary>
    public bool RulesLocked => _screen is ActiveViewModel;

    public bool IsServiceAvailable => _snapshot.State == ServiceConnectionState.Available;

    public bool IsConnecting => _snapshot.State == ServiceConnectionState.Connecting;

    public bool IsServiceUnavailable => _snapshot.State == ServiceConnectionState.Unavailable;

    /// <summary>
    /// Time to the next attempt to reach the service. Empty while the service is there
    /// or while the link has not said when it will try.
    /// </summary>
    public string RetryText
    {
        get
        {
            if (_snapshot.State != ServiceConnectionState.Unavailable || _retryAt is not { } at)
            {
                return string.Empty;
            }

            var left = at - _local.UtcNow;

            return left <= TimeSpan.Zero ? Text.ServiceRetryNow : Text.RetryIn(left);
        }
    }

    /// <summary>The screen the engine's state belongs on, and null while the service is not there to say. Absent is not a fourth screen: the window shows <see cref="ServiceMessage"/>.</summary>
    public ScreenViewModel? CurrentScreen => _screen;

    /// <summary>
    /// The one sentence on this window that the markup cannot name, because which of the three it
    /// is depends on the state. The words themselves still come from the resources.
    /// </summary>
    public string ServiceMessage => _snapshot.State switch
    {
        ServiceConnectionState.Available => Text.ServiceAvailable,
        ServiceConnectionState.Connecting => Text.ServiceConnecting,

        // What to check, and no attempt to do the service's work.
        _ => Text.ServiceUnavailableHeading,
    };

    public void Dispose()
    {
        _link.Changed -= OnLinkChanged;
        _link.Retrying -= OnRetrying;
        _ticker.Ticked -= OnTicked;
        Text.Changed -= OnLanguageChanged;

        _setup.Dispose();
        _active.Dispose();
        _waiting.Dispose();
        Settings.Dispose();
        Tray.Dispose();
    }

    /// <summary>Whether quitting now would leave a block behind. Only what the service has said counts: with no status the interface knows nothing.</summary>
    private bool BlockIsInForce() =>
        _snapshot.Status is { } status && EngineState.Blocks(status.State) is true;

    private void OnLinkChanged(object? sender, ServiceSnapshot snapshot) => _post(() => Apply(snapshot));

    private void OnRetrying(object? sender, TimeSpan wait) => _post(() =>
    {
        _retryAt = _local.UtcNow + wait;
        OnPropertyChanged(nameof(RetryText));
    });

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ServiceMessage));
        OnPropertyChanged(nameof(RetryText));
        Say();
    }

    // Raised on the interface thread by contract, so it neither needs the post nor may use it: a
    // countdown deferred to the next round of the loop would tick late and unevenly.
    private void OnTicked(object? sender, EventArgs e)
    {
        _screen?.Tick();

        if (_snapshot.State == ServiceConnectionState.Unavailable)
        {
            OnPropertyChanged(nameof(RetryText));
        }

        // The tooltip counts down too, and is the only thing saying so once the window is hidden.
        Tray.Tick();
    }

    private void Apply(ServiceSnapshot snapshot)
    {
        _snapshot = snapshot;

        if (snapshot.State != ServiceConnectionState.Unavailable)
        {
            // The next outage starts from nothing rather than from the last one's deadline.
            _retryAt = null;
        }

        if (snapshot.Status is { } status)
        {
            // Before the screen reads any moment out of the status: everything else in it is a
            // reading of the clock this line measures against.
            _clock.Follow(status.Now);

            // A word this build does not know cannot come from a version-2 service. If one ever does,
            // the setup screen is the only one that cannot lie about it: it claims no remaining time.
            var was = _screen;

            _screen = (Screen.For(status.State) ?? ScreenKind.Setup) switch
            {
                ScreenKind.Active => _active,
                ScreenKind.Waiting => _waiting,
                _ => _setup,
            };

            // Before Show, so the lists are there before this screen's own GetConfig answers.
            Handover(was);

            _screen.Show(status);

            Settings.Show(status);
        }
        else
        {
            // Nothing to draw. Leaving the last screen up would show a session that may be over.
            var was = _screen;
            _screen = null;
            Handover(was);
        }

        // After the clock has been set, and whether or not there is a status: the icon has
        // something to say about a service that is not there, and the window does not.
        Tray.Show(snapshot);

        foreach (var property in Everything)
        {
            OnPropertyChanged(property);
        }

        // Only when the lock moved: a list control handed new items drops its selection.
        if (_sections.Any(line => line.Locked) != RulesLocked)
        {
            Say();
        }
    }

    /// <summary>
    /// The screen changed. The rule screen coming in takes over the last one's lists and typing,
    /// and no notice outlives the screen it was about.
    /// </summary>
    private void Handover(ScreenViewModel? was)
    {
        if (ReferenceEquals(was, _screen))
        {
            return;
        }

        if (_screen is RuleScreenViewModel rules)
        {
            if (_rules is not null)
            {
                rules.Adopt(_rules);
            }

            _rules = rules;
        }

        ForgetNotices();
    }

    private void ForgetNotices()
    {
        _setup.ForgetNotice();
        _active.ForgetNotice();
    }

    // The sidebar in words. The list first and the selection after it, and never the list alone.
    private void Say()
    {
        var locked = RulesLocked;

        _sections =
        [
            new SectionLine(Section.Session, Text.SectionSession, false),
            new SectionLine(Section.Sites, Text.SectionSites, locked),
            new SectionLine(Section.Apps, Text.SectionApps, locked),
            new SectionLine(Section.Settings, Text.SectionSettings, false),
        ];

        // Said twice, as none and then as what it is: see the flag.
        _sectionsReplaced = true;
        try
        {
            OnPropertyChanged(nameof(Sections));
            OnPropertyChanged(nameof(SelectedSectionIndex));
        }
        finally
        {
            _sectionsReplaced = false;
        }

        OnPropertyChanged(nameof(SelectedSectionIndex));
    }
}
