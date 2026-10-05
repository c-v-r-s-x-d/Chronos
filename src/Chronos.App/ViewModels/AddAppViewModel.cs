using System.Globalization;
using Chronos.App.Apps;
using Chronos.App.Services;
using Chronos.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chronos.App.ViewModels;

/// <summary>The ways of naming a program, one tab each, in the order the dialog shows them.</summary>
public enum AddAppTab
{
    Running,
    Executable,
    Shortcut,
    Name,
}

/// <summary>The "Add application" dialog. It holds only what has been picked, so a status leaves it alone; the rule and any refusal go through the screen it was opened over.</summary>
public sealed class AddAppViewModel : ObservableObject, IDisposable
{
    private readonly Func<IReadOnlyList<RunningApp>> _listRunning;

    // The two kinds in words, rebuilt on a language change so it reaches them.
    private IReadOnlyList<MatchKindLine> _matchKinds = AppMatchKinds.Lines();

    // Which of them, as a position rather than a line: the lines are rebuilt on a language change.
    private int _selectedMatchKind;

    private IReadOnlyList<RunningApp> _running = [];

    private IReadOnlyList<RunningApp> _visibleRunning = [];

    private string _runningFilter = string.Empty;

    private RunningApp? _selectedRunning;

    private string? _chosenExecutable;

    private string? _chosenShortcut;

    // The program the shortcut starts, once it has been followed to one that is there.
    private string? _shortcutTarget;

    // A shortcut that could not be followed: what went wrong, not the sentence, so a language
    // change rewrites it instead of leaving it in the previous one.
    private ShortcutOutcome? _refusedShortcut;

    private string _name = string.Empty;

    private AddAppTab _tab;

    // Set while a list has just been replaced. The control drops its selection with the old items,
    // and a binding passes on only a value that differs from the last one it read - so the
    // selection is read as none once, and the announcement after that is a change it passes on.
    private bool _kindsReplaced;

    private bool _runningReplaced;

    /// <param name="running">What is running now; the machine's own list unless a test says otherwise.</param>
    public AddAppViewModel(RuleScreenViewModel rules, Func<IReadOnlyList<RunningApp>>? running = null)
    {
        ArgumentNullException.ThrowIfNull(rules);

        Rules = rules;
        _listRunning = running ?? RunningProcesses.Now;

        AddCommand = new AsyncRelayCommand(AddAsync, () => CanAdd);
        LoadRunningCommand = new AsyncRelayCommand(LoadRunningAsync);

        Rules.Text.Changed += OnLanguageChanged;

        // Opened clean: whatever the page last said is not a refusal of anything picked here.
        Rules.ForgetNotice();
    }

    /// <summary>Raised once the rule is in the configuration. A refusal or a lost service does not raise it.</summary>
    public event EventHandler? Done;

    /// <summary>The screen the rule is added through, and whose notice says why it was not.</summary>
    public RuleScreenViewModel Rules { get; }

    public IAsyncRelayCommand AddCommand { get; }

    /// <summary>Fills <see cref="RunningApps"/>.</summary>
    public IAsyncRelayCommand LoadRunningCommand { get; }

    public AddAppTab Tab
    {
        get => _tab;
        set
        {
            if (!Enum.IsDefined(value) || !SetProperty(ref _tab, value))
            {
                return;
            }

            OnPropertyChanged(nameof(TabIndex));

            // A typed name has no path to match by, so the choice goes back to the name.
            if (value == AddAppTab.Name && AppMatchKinds.Kinds[_selectedMatchKind] == AppMatch.FullPath)
            {
                _selectedMatchKind = AppMatchKinds.Kinds.ToList().IndexOf(AppMatch.FileName);

                OnPropertyChanged(nameof(SelectedMatchKind));
                OnPropertyChanged(nameof(MatchKindExplanation));
            }

            OnPropertyChanged(nameof(CanChooseFullPath));
            Chosen();
        }
    }

    /// <summary>
    /// <see cref="Tab"/> as a position, for the tab control. A position outside the four is
    /// ignored: that is what the control reports while it has no selection.
    /// </summary>
    public int TabIndex
    {
        get => (int)_tab;
        set => Tab = (AddAppTab)value;
    }

    /// <summary>What is running now, once it has been asked for.</summary>
    public IReadOnlyList<RunningApp> RunningApps => _running;

    /// <summary>
    /// False until the list has been asked for, which is not the same as nothing running.
    /// </summary>
    public bool AnyRunningListed => _running.Count > 0;

    /// <summary>What the person typed to narrow the running list, by name.</summary>
    public string RunningFilter
    {
        get => _runningFilter;
        set
        {
            if (SetProperty(ref _runningFilter, value ?? string.Empty))
            {
                Narrow();
            }
        }
    }

    /// <summary><see cref="RunningApps"/>, narrowed by <see cref="RunningFilter"/>.</summary>
    public IReadOnlyList<RunningApp> VisibleRunning => _visibleRunning;

    /// <summary>
    /// The program picked out of the running list. Null is ignored rather than kept: that is what
    /// a list control reports while its items are being replaced, and a pick is only let go of
    /// here, when the program is no longer on the list.
    /// </summary>
    public RunningApp? SelectedRunning
    {
        get => _runningReplaced ? null : _selectedRunning;
        set
        {
            if (value is not null && SetProperty(ref _selectedRunning, value))
            {
                Chosen();
            }
        }
    }

    public string? ChosenExecutable => _chosenExecutable;

    public string? ChosenShortcut => _chosenShortcut;

    /// <summary>The program the chosen shortcut starts, shown before anything is added; null when there is none.</summary>
    public string? ShortcutTarget => _shortcutTarget;

    /// <summary><see cref="ShortcutTarget"/> as a sentence; empty when there is none.</summary>
    public string ShortcutLeadsTo => _shortcutTarget is { } target
        ? string.Format(CultureInfo.CurrentUICulture, Rules.Text.ShortcutPointsTo, target)
        : string.Empty;

    /// <summary>Why the chosen shortcut cannot be added; empty when it can.</summary>
    public string ShortcutRefusal => _refusedShortcut is { } outcome ? Shortcut.Refusal(outcome) : string.Empty;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? string.Empty))
            {
                Chosen();
            }
        }
    }

    /// <summary>The ways a rule can name a program, each with a sentence on what choosing it means.</summary>
    public IReadOnlyList<MatchKindLine> MatchKinds => _matchKinds;

    /// <summary>
    /// Which one is chosen, as a position in <see cref="MatchKinds"/>. A position outside the list
    /// is ignored rather than kept: that is what a list control reports while nothing is selected,
    /// and the next add has to name a kind either way. So is the full path while it is not on offer.
    /// </summary>
    public int SelectedMatchKind
    {
        get => _kindsReplaced ? -1 : _selectedMatchKind;
        set
        {
            if (value < 0 || value >= _matchKinds.Count
                || (!CanChooseFullPath && AppMatchKinds.Kinds[value] == AppMatch.FullPath)
                || !SetProperty(ref _selectedMatchKind, value))
            {
                return;
            }

            OnPropertyChanged(nameof(MatchKindExplanation));
        }
    }

    /// <summary>The chosen kind's sentence: the difference between the kinds otherwise shows up months later.</summary>
    public string MatchKindExplanation => _matchKinds[_selectedMatchKind].Explanation;

    /// <summary>False on the name tab: "only this file" needs a file, and a typed name is not one.</summary>
    public bool CanChooseFullPath => Tab != AddAppTab.Name;

    /// <summary>Whether the tab in front has something to add. What is chosen on another tab does not count.</summary>
    public bool CanAdd => Tab switch
    {
        AddAppTab.Running => _selectedRunning is not null,
        AddAppTab.Executable => !string.IsNullOrWhiteSpace(ChosenExecutable),
        AddAppTab.Shortcut => ShortcutTarget is not null,
        AddAppTab.Name => !string.IsNullOrWhiteSpace(Name),
        _ => false,
    };

    /// <summary>The second way: a program picked out of a file dialog. The path arrives as an argument because a picker is a control and would need a windowing system to test.</summary>
    public void ChooseExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _chosenExecutable = path.Trim();

        OnPropertyChanged(nameof(ChosenExecutable));
        Chosen();
    }

    /// <summary>The shortcut is followed first and the rule made for what it found: a rule naming the <c>.lnk</c> would be accepted and block nothing.</summary>
    public void ChooseShortcut(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var target = Shortcut.Resolve(path.Trim());

        _chosenShortcut = path.Trim();
        _shortcutTarget = target.IsResolved ? target.Path : null;
        _refusedShortcut = target.IsResolved ? null : target.Outcome;

        OnPropertyChanged(nameof(ChosenShortcut));
        OnPropertyChanged(nameof(ShortcutTarget));
        OnPropertyChanged(nameof(ShortcutLeadsTo));
        OnPropertyChanged(nameof(ShortcutRefusal));
        Chosen();
    }

    public void Dispose() => Rules.Text.Changed -= OnLanguageChanged;

    // Everything CanAdd reads ends here, so the button follows each of them - and a refusal does
    // not outlive the choice it was about.
    private void Chosen()
    {
        Rules.ForgetNotice();

        OnPropertyChanged(nameof(CanAdd));
        AddCommand.NotifyCanExecuteChanged();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // Made again rather than kept, so a language change rewrites the names and the sentences
        // instead of leaving the previous language's on the screen.
        _matchKinds = AppMatchKinds.Lines();

        // The list and then its selection, never the list alone: replacing the items makes the
        // control report no selection and write back -1, which the setter refuses, so nothing would
        // tell the control what the selection still is. See the flag.
        _kindsReplaced = true;
        OnPropertyChanged(nameof(MatchKinds));
        OnPropertyChanged(nameof(SelectedMatchKind));
        _kindsReplaced = false;

        OnPropertyChanged(nameof(SelectedMatchKind));
        OnPropertyChanged(nameof(MatchKindExplanation));

        OnPropertyChanged(nameof(ShortcutLeadsTo));
        OnPropertyChanged(nameof(ShortcutRefusal));
    }

    /// <summary>Asked for rather than kept current: naming a process means opening it, and doing that on each status would be hundreds of system calls a second.</summary>
    private async Task LoadRunningAsync()
    {
        // Off the interface thread: a few hundred processes would freeze the window.
        _running = await Task.Run(_listRunning).ConfigureAwait(true);

        OnPropertyChanged(nameof(RunningApps));
        OnPropertyChanged(nameof(AnyRunningListed));
        Narrow();
    }

    // The filter is kept as typed and applied to whatever list arrives.
    private void Narrow()
    {
        _visibleRunning =
        [
            .. _running.Where(app => app.Name.Contains(_runningFilter, StringComparison.OrdinalIgnoreCase)),
        ];

        // A program no longer on the list is not something the button may still add.
        if (_selectedRunning is { } picked && !_visibleRunning.Contains(picked))
        {
            _selectedRunning = null;
        }

        // The list and then its selection, as none and then as what it is: see the flag.
        _runningReplaced = true;
        OnPropertyChanged(nameof(VisibleRunning));
        OnPropertyChanged(nameof(SelectedRunning));
        _runningReplaced = false;

        OnPropertyChanged(nameof(SelectedRunning));
        Chosen();
    }

    private async Task AddAsync()
    {
        // The button is off while this is false; this is what happens if something runs it anyway.
        if (!CanAdd)
        {
            return;
        }

        var rule = Tab == AddAppTab.Name
            ? new AppRuleMessage(AppMatch.FileName, Name.Trim())
            : Rule(Tab switch
            {
                AddAppTab.Running => _selectedRunning!.Path,
                AddAppTab.Executable => ChosenExecutable!,
                _ => ShortcutTarget!,
            });

        // Closed only once the rule is in. A refusal is shown here, and a service that did not
        // answer leaves what was picked where it is.
        if (await Rules.AddAppRuleAsync(rule).ConfigureAwait(true))
        {
            Done?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>One program, however it was picked, named the way the user chose. The ways meet here and go through <see cref="RuleScreenViewModel.AddAppRuleAsync"/>, so the protected list is checked once for all.</summary>
    private AppRuleMessage Rule(string path)
    {
        var kind = AppMatchKinds.Kinds[_selectedMatchKind];

        // By name means the name wherever it lives, so the path is dropped on purpose.
        var value = kind == AppMatch.FullPath ? path : Path.GetFileName(path);

        return new AppRuleMessage(kind, value);
    }
}
