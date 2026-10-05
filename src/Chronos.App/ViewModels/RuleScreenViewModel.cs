using System.Globalization;
using Chronos.App.Apps;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.Ipc;
using CommunityToolkit.Mvvm.Input;
using Built = Chronos.App.Presets;

namespace Chronos.App.ViewModels;

/// <summary>A screen that shows the block lists and lets them be edited: the settings screen allows everything, the active screen allows adding but not removing.</summary>
public abstract class RuleScreenViewModel : ScreenViewModel
{
    /// <summary>The sentences this screen composes from the resources. <see cref="HasNotice"/> cannot change with the language: no translation turns an empty notice into one.</summary>
    private static readonly string[] Words = [nameof(RemovalUnavailable), nameof(Notice)];

    /// <summary>What <see cref="Adopt"/> takes from the other screen; the lines are said by BuildLines.</summary>
    private static readonly string[] Handed =
    [
        nameof(Sites),
        nameof(Apps),
        nameof(SiteFilter),
        nameof(AppFilter),
        nameof(NewSiteDomain),
        nameof(ShowsSiteSearch),
        nameof(ShowsAppSearch),
    ];

    private readonly IServiceLink _link;

    private IReadOnlyList<SiteRuleMessage> _sites = [];

    private IReadOnlyList<AppRuleMessage> _apps = [];

    private string _newSiteDomain = string.Empty;

    // Whether a session is running, as the last status said. Not a constant per screen: the shell
    // puts an unrecognised state on the settings screen, and that screen must not offer a removal
    // the service is about to refuse to act on.
    private bool _inASession;

    // What the service said about the last command, as its codes: a refusal first, then the
    // warnings. Kept unworded so a language change rewrites it.
    private IReadOnlyList<IpcNotice>? _serviceSaid;

    // The interface's own refusal, kept as the name it refused rather than as a finished sentence,
    // so a language change rewrites it instead of leaving it in the previous one.
    private string? _refusedApp;

    private IReadOnlyList<PresetLine> _presets = [];

    // What the last preset did, as the two numbers and not as a sentence, so a language change
    // rewords it. The service's own words, when it had any, come before it.
    private (int Added, int Already)? _presetOutcome;

    private IReadOnlyList<SiteLine> _siteLines = [];

    private IReadOnlyList<AppLine> _appLines = [];

    // What the person typed into the search boxes. Kept here and not in the control: a status every
    // 15 s replaces the lists, and must not take the typing with them.
    private string _siteFilter = string.Empty;

    private string _appFilter = string.Empty;

    // The search appears above this many rules; a shorter list is quicker to read than to search.
    private const int SearchFrom = 8;

    protected RuleScreenViewModel(IServiceLink link, Text text)
        : base(text)
    {
        ArgumentNullException.ThrowIfNull(link);

        _link = link;

        _presets = BuildPresets();
        ApplyPresetCommand = new AsyncRelayCommand<Preset>(ApplyPresetAsync);
        AddSiteCommand = new AsyncRelayCommand(AddSiteAsync);
        RemoveSiteCommand = new AsyncRelayCommand<SiteRuleMessage>(RemoveSiteAsync);
        RemoveAppCommand = new AsyncRelayCommand<AppRuleMessage>(RemoveAppAsync);
    }

    /// <summary>Copies the preset's domains into the user's list, one <c>AddSiteRule</c> each. Only what the list lacks is sent, and it works during a session too: adding never loosens anything.</summary>
    public IAsyncRelayCommand<Preset> ApplyPresetCommand { get; }

    public IReadOnlyList<PresetLine> Presets => _presets;

    /// <summary>The site rules as lines, narrowed by <see cref="SiteFilter"/>.</summary>
    public IReadOnlyList<SiteLine> SiteLines => _siteLines;

    public IReadOnlyList<AppLine> AppLines => _appLines;

    public string SiteFilter
    {
        get => _siteFilter;
        set
        {
            var was = _siteFilter.Length > 0;

            if (SetProperty(ref _siteFilter, value ?? string.Empty))
            {
                if (was != _siteFilter.Length > 0)
                {
                    OnPropertyChanged(nameof(ShowsSiteSearch));
                }

                BuildLines();
            }
        }
    }

    public string AppFilter
    {
        get => _appFilter;
        set
        {
            var was = _appFilter.Length > 0;

            if (SetProperty(ref _appFilter, value ?? string.Empty))
            {
                if (was != _appFilter.Length > 0)
                {
                    OnPropertyChanged(nameof(ShowsAppSearch));
                }

                BuildLines();
            }
        }
    }

    public bool ShowsSiteSearch => _sites.Count > SearchFrom || _siteFilter.Length > 0;

    public bool ShowsAppSearch => _apps.Count > SearchFrom || _appFilter.Length > 0;

    public IAsyncRelayCommand AddSiteCommand { get; }

    public IAsyncRelayCommand<SiteRuleMessage> RemoveSiteCommand { get; }

    public IAsyncRelayCommand<AppRuleMessage> RemoveAppCommand { get; }

    /// <summary>The configured lists, from <c>GetConfig</c> and never from the status.</summary>
    public IReadOnlyList<SiteRuleMessage> Sites => _sites;

    public IReadOnlyList<AppRuleMessage> Apps => _apps;

    public string NewSiteDomain
    {
        get => _newSiteDomain;
        set => SetProperty(ref _newSiteDomain, value);
    }

    /// <summary>False while a session is running: its rules were fixed at start. The control stays on the screen, turned off, with <see cref="RemovalUnavailable"/> beside it.</summary>
    public bool CanRemoveRules => !_inASession;

    /// <summary>Why it is off. Empty while it is on, so nothing is explained that needs no explaining.</summary>
    public string RemovalUnavailable => _inASession ? Text.RemovalUnavailable : string.Empty;

    /// <summary>What became of the last thing asked for, when not visible in the lists: a refusal, or a warning about something the service did anyway. The service sends codes; this end words them.</summary>
    public string Notice => _refusedApp is { } name
        ? string.Format(CultureInfo.CurrentUICulture, Text.ProtectedAppFormat, name)
        : Join(_serviceSaid?.Select(said => ServiceNotice.Text(said?.Code, said?.Arguments)) ?? [])
            ?? (_presetOutcome is var (added, already)
                ? string.Format(CultureInfo.CurrentUICulture, Text.PresetOutcomeFormat, added, already)
                : string.Empty);

    public bool HasNotice => Notice.Length > 0;

    internal Task Loading { get; private set; } = Task.CompletedTask;

    protected IServiceLink Link => _link;

    public override void Show(StatusPayload status)
    {
        ArgumentNullException.ThrowIfNull(status);

        // Ended included. The rules are still in force and the service refuses to change them at
        // all while it clears up, so this is no moment to offer a removal either.
        _inASession = status.State is EngineState.Active or EngineState.UnlockPending or EngineState.Ended;

        OnPropertyChanged(nameof(CanRemoveRules));
        OnPropertyChanged(nameof(RemovalUnavailable));

        // Asked for again on every status rather than read once. The status carries the session's
        // rules and never the configured ones, so nothing else would ever say that another client -
        // or the person, in the file - had changed the lists.
        Loading = ReadConfigurationAsync();
    }

    /// <summary>Nothing on this half of a screen is read off a clock. The active screen overrides this for its countdown.</summary>
    public override void Tick()
    {
    }

    public override void Reword()
    {
        // Made again so a language change rewrites the names; once per change, not once per second.
        _presets = BuildPresets();

        OnPropertyChanged(nameof(Presets));

        // The kind on each app line is a word.
        BuildLines();

        foreach (var property in Words)
        {
            OnPropertyChanged(property);
        }
    }

    /// <summary>One app rule, however it was come by. The interface's half of the double check: the service checks again, so this only buys an explanation without a round trip.</summary>
    /// <returns>Whether the rule is now in the configuration.</returns>
    public async Task<bool> AddAppRuleAsync(AppRuleMessage rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (ProtectedApps.Refuses(rule) is { } name)
        {
            Refuse(name);

            return false;
        }

        var answer = await SendAsync(new IpcRequest { Command = ServiceCommand.AddAppRule, App = rule })
            .ConfigureAwait(true);

        return answer is { Accepted: true };
    }

    /// <summary>
    /// The configuration as the service has it after whatever was just asked of it. Null means the
    /// service could not be reached or refused; what is on the screen then stays as it was.
    /// </summary>
    protected void Take(ConfigPayload? config)
    {
        // A list that lost an element on the wire would hand a null to a remove command.
        if (config is null || !AnswerCheck.IsUsable(config))
        {
            return;
        }

        _sites = config.Sites;
        _apps = config.Apps;

        // The language lives in the configuration, since the service owns it. Applied here rather
        // than on the settings screen so an interface that starts mid-session still comes up in the
        // chosen language. Choosing what is already in use announces nothing, which keeps this off
        // the per-status path.
        Text.SwitchTo(config.Language);

        Took(config);

        OnPropertyChanged(nameof(Sites));
        OnPropertyChanged(nameof(Apps));
        OnPropertyChanged(nameof(ShowsSiteSearch));
        OnPropertyChanged(nameof(ShowsAppSearch));
        BuildLines();
    }

    private static IReadOnlyList<PresetLine> BuildPresets() =>
        [.. Built.All.Select(preset => new PresetLine(preset, Built.Name(preset)))];

    // The filter is applied here and kept as typed, so the next status finds it where it was left.
    private void BuildLines()
    {
        _siteLines =
        [
            .. _sites
                .Where(rule => rule.Domain.Contains(_siteFilter, StringComparison.OrdinalIgnoreCase))
                .Select(rule => new SiteLine(rule, Letter(rule.Domain), Removal(rule.Domain))),
        ];

        _appLines =
        [
            .. _apps
                .Where(rule => rule.Value.Contains(_appFilter, StringComparison.OrdinalIgnoreCase))
                .Select(rule => new AppLine(rule, Letter(Path.GetFileName(rule.Value)), AppMatchKinds.Name(rule.MatchKind), Removal(rule.Value))),
        ];

        OnPropertyChanged(nameof(SiteLines));
        OnPropertyChanged(nameof(AppLines));
    }

    // A remove button as a screen reader says it; rebuilt with the lines on a language change.
    private string Removal(string rule) =>
        string.Format(CultureInfo.CurrentUICulture, Text.RemoveRuleFormat, rule);

    // The first letter or digit of the name, for the square beside it; a name with none gets a dot.
    private static string Letter(string name)
    {
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                return char.ToUpperInvariant(c).ToString();
            }
        }

        return "•";
    }

    /// <summary>Takes over from the other rule screen when the state moves between the two: its lists and typed filters. They are there before this screen's own GetConfig answers.</summary>
    public void Adopt(RuleScreenViewModel other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (ReferenceEquals(other, this))
        {
            return;
        }

        _sites = other._sites;
        _apps = other._apps;
        _siteFilter = other._siteFilter;
        _appFilter = other._appFilter;
        _newSiteDomain = other._newSiteDomain;

        foreach (var property in Handed)
        {
            OnPropertyChanged(property);
        }

        BuildLines();
        Adopted();
    }

    /// <summary>Anything else a screen reads off the lists it was handed.</summary>
    protected virtual void Adopted()
    {
    }

    /// <summary>Anything else a screen reads out of the configuration.</summary>
    protected virtual void Took(ConfigPayload config)
    {
    }

    protected async Task<IpcResponse?> SendAsync(IpcRequest request)
    {
        var answer = await _link.SendAsync(request, CancellationToken.None).ConfigureAwait(true);

        Report(answer);

        // Warnings ride on an answer the service accepted (a removal it wrote, a value it clamped),
        // so a screen must not read them as a failure.
        Take(answer?.Config);

        return answer;
    }

    /// <summary>What the service said, if anything. An accepted answer with nothing to add clears the last notice.</summary>
    private void Report(IpcResponse? answer)
    {
        _refusedApp = null;
        _presetOutcome = null;

        _serviceSaid = answer switch
        {
            // The link could not reach the service at all: that is the window's news.
            null => null,
            // The refusal carries no values of its own; it leads, and the warnings that explain it
            // follow in the order they came.
            { Accepted: false, Error: { } error } refused => [new IpcNotice(error, []), .. refused.Warnings ?? []],
            var other => other.Warnings,
        };

        Said();
    }

    private void Refuse(string name)
    {
        _presetOutcome = null;
        _serviceSaid = null;
        _refusedApp = name;

        Said();
    }

    /// <summary>Lets go of the last notice; it is not about the next command.</summary>
    public void ForgetNotice()
    {
        if (!HasNotice)
        {
            return;
        }

        _presetOutcome = null;
        _serviceSaid = null;
        _refusedApp = null;

        Said();
    }

    private void Said()
    {
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(HasNotice));
    }

    private static string? Join(IEnumerable<string?> lines)
    {
        var said = string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));

        return said.Length == 0 ? null : said;
    }

    private async Task ApplyPresetAsync(Preset? preset)
    {
        if (preset is null)
        {
            return;
        }

        // The list as it was before the first command: each answer replaces it, and "already there"
        // is about what the person had, not about what the preset has put there since.
        var present = _sites.Select(site => site.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = preset.Domains.Where(domain => !present.Contains(domain)).ToArray();
        var added = 0;

        // Nothing said yet: an earlier notice standing over this outcome would be worse than none.
        Report(IpcResponse.Ok());

        IpcResponse? last = null;
        foreach (var domain in missing)
        {
            // Subdomains included: a preset names a service, and a rule that let through the "m."
            // and "www." of the same site would be a preset that does not do what it says.
            last = await SendAsync(new IpcRequest
            {
                Command = ServiceCommand.AddSiteRule,
                Site = new SiteRuleMessage(domain, true),
            }).ConfigureAwait(true);

            if (last is not { Accepted: true })
            {
                break;
            }

            added++;
        }

        // A refusal is the service's to word, and it has: the count would sit over its explanation.
        // No answer at all is the window's news and only the count of what got in is ours.
        if (last is { Accepted: false })
        {
            return;
        }

        _presetOutcome = (added, preset.Domains.Count - missing.Length);
        Said();
    }

    private async Task AddSiteAsync()
    {
        var domain = NewSiteDomain.Trim();
        if (domain.Length == 0)
        {
            return;
        }

        // Subdomains included, for the same reason a preset includes them: a block that let through
        // the "m." and "www." of the same site is not a block of that site.
        var answer = await SendAsync(new IpcRequest
        {
            Command = ServiceCommand.AddSiteRule,
            Site = new SiteRuleMessage(domain, true),
        }).ConfigureAwait(true);

        if (answer is { Accepted: true })
        {
            NewSiteDomain = string.Empty;
        }
    }

    private async Task RemoveSiteAsync(SiteRuleMessage? rule)
    {
        // The button is there and turned off; this is what happens if something reaches
        // it anyway. The service would accept the removal and warn that it waits for the next
        // session - which is a fine answer to a question this screen is not asking.
        if (rule is null || !CanRemoveRules)
        {
            return;
        }

        await SendAsync(new IpcRequest { Command = ServiceCommand.RemoveSiteRule, Site = rule })
            .ConfigureAwait(true);
    }

    private async Task RemoveAppAsync(AppRuleMessage? rule)
    {
        if (rule is null || !CanRemoveRules)
        {
            return;
        }

        await SendAsync(new IpcRequest { Command = ServiceCommand.RemoveAppRule, App = rule })
            .ConfigureAwait(true);
    }

    private async Task ReadConfigurationAsync()
    {
        var answer = await _link.SendAsync(
            new IpcRequest { Command = ServiceCommand.GetConfig },
            CancellationToken.None).ConfigureAwait(true);

        Take(answer?.Config);
    }
}
