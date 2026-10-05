using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Chronos.Core.Rules;
using Chronos.Core.Sessions;
using Chronos.Core.Time;
using Chronos.Ipc;
using Chronos.Service.Configuration;
using Chronos.Service.Reconciliation;
using Chronos.Service.Sessions;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Ipc;

public sealed class CommandDispatcher(
    SessionEngine engine,
    ConfigStore config,
    ReconcileScheduler scheduler,
    LayerStatusRegistry layers,
    SessionGate gate,
    IClock clock,
    IProtectedAppPolicy protectedApps,
    EventBus events,
    ILogger<CommandDispatcher> logger)
{
    /// <summary>A removal during a session is accepted; this notice tells the UI it takes effect next session.</summary>
    private static readonly IpcNotice RemovalWaitsForTheNextSession = new(IpcCodes.RulesRemovalWaitsForNextSession, []);

    private bool InASession => engine.State is SessionState.Active or SessionState.UnlockPending;

    public IpcResponse Dispatch(IpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ProtocolVersion != IpcProtocol.Version)
        {
            // A sentence, not a code: a client of another version may not know this version's codes.
            return IpcResponse.Fail(
                $"Unsupported protocol version {request.ProtocolVersion}; this service speaks version {IpcProtocol.Version}.");
        }

        lock (gate.Sync)
        {
            // Before anything reads State: an expired session must count as Ended.
            engine.Tick();

            return request.Command switch
            {
                "GetStatus" => IpcResponse.Ok(CurrentStatus()),
                // Answered here so the version check applies; the server then turns the connection into a stream.
                IpcProtocol.SubscribeCommand => IpcResponse.Ok(CurrentStatus()),
                "GetConfig" => IpcResponse.Ok(CurrentStatus(), CurrentConfig(config.Load())),
                "StartSession" => StartSession(request),
                "ExtendSession" => ExtendSession(request),
                "RequestUnlock" => Complete(engine.RequestUnlock(), "unlock requested"),
                "CancelUnlock" => Complete(engine.CancelUnlock(), "unlock cancelled"),
                "AddSiteRule" => AddSiteRule(request),
                "RemoveSiteRule" => RemoveSiteRule(request),
                "AddAppRule" => AddAppRule(request),
                "RemoveAppRule" => RemoveAppRule(request),
                "UpdateSettings" => UpdateSettings(request),
                _ => IpcResponse.Fail(IpcCodes.CommandUnknown),
            };
        }
    }

    private IpcResponse AddSiteRule(IpcRequest request)
    {
        if (ReadSite(request, out var rule, out var refusal))
        {
            if (RulesAreFrozen() is { } frozen)
            {
                return frozen;
            }

            // Engine first: nothing is written to the file if it refuses.
            if (InASession && Refused(engine.AddSiteRule(rule)) is { } refused)
            {
                return refused;
            }

            var settings = config.Load();
            var stored = Without(settings.Sites, rule);
            stored.Add(new SiteRuleDto(rule.Domain, rule.IncludeSubdomains));
            config.Save(settings with { Sites = stored });

            return Applied("site rule added", [], enforcementChanged: InASession);
        }

        return refusal;
    }

    private IpcResponse RemoveSiteRule(IpcRequest request)
    {
        if (ReadSite(request, out var rule, out var refusal))
        {
            if (RulesAreFrozen() is { } frozen)
            {
                return frozen;
            }

            var settings = config.Load();
            var kept = Without(settings.Sites, rule);

            if (kept.Count == settings.Sites.Count)
            {
                return IpcResponse.Fail(IpcCodes.RulesSiteNotFound);
            }

            config.Save(settings with { Sites = kept });

            // The session's rules are fixed at start; a removal never loosens a block in force.
            return Applied(
                "site rule removed",
                InASession ? [RemovalWaitsForTheNextSession] : [],
                enforcementChanged: false);
        }

        return refusal;
    }

    private IpcResponse AddAppRule(IpcRequest request)
    {
        if (ReadApp(request, out var rule, out var refusal))
        {
            // Runs in every state: while idle the engine refuses on state alone and never checks
            // protection, so the config would accept explorer.exe.
            var verdict = protectedApps.Evaluate(rule);
            if (verdict.IsProtected)
            {
                return IpcResponse.Fail(verdict.Reason ?? IpcCodes.RulesAppProtected);
            }

            if (RulesAreFrozen() is { } frozen)
            {
                return frozen;
            }

            if (InASession && Refused(engine.AddAppRule(rule)) is { } refused)
            {
                return refused;
            }

            var settings = config.Load();
            var stored = Without(settings.Apps, rule);
            stored.Add(new AppRuleDto(rule.Kind.ToString(), rule.Value));
            config.Save(settings with { Apps = stored });

            return Applied("app rule added", [], enforcementChanged: InASession);
        }

        return refusal;
    }

    private IpcResponse RemoveAppRule(IpcRequest request)
    {
        if (ReadApp(request, out var rule, out var refusal))
        {
            if (RulesAreFrozen() is { } frozen)
            {
                return frozen;
            }

            var settings = config.Load();
            var kept = Without(settings.Apps, rule);

            if (kept.Count == settings.Apps.Count)
            {
                return IpcResponse.Fail(IpcCodes.RulesAppNotFound);
            }

            config.Save(settings with { Apps = kept });

            return Applied(
                "app rule removed",
                InASession ? [RemovalWaitsForTheNextSession] : [],
                enforcementChanged: false);
        }

        return refusal;
    }

    private IpcResponse UpdateSettings(IpcRequest request)
    {
        if (request.Settings is not { } message)
        {
            return IpcResponse.Fail(IpcCodes.ConfigSettingsMissing);
        }

        var settings = config.Load();
        var warnings = new List<IpcNotice>();

        if (message.CoolDownMinutes is { } coolDown)
        {
            // Clamped here as well as in ConfigStore so the file never holds an out-of-range value.
            var clamped = SessionLimits.ClampCoolDown(TimeSpan.FromMinutes(coolDown));
            if (clamped.WasClamped)
            {
                warnings.Add(new IpcNotice(IpcCodes.ConfigCoolDownClamped, [Minutes(clamped.Value)]));
            }

            settings = settings with { CoolDownMinutes = (int)clamped.Value.TotalMinutes };
        }

        if (message.DefaultSessionMinutes is { } duration)
        {
            var clamped = SessionLimits.ClampSessionDuration(TimeSpan.FromMinutes(duration));
            if (clamped.WasClamped)
            {
                warnings.Add(new IpcNotice(IpcCodes.ConfigSessionDurationClamped, [Minutes(clamped.Value)]));
            }

            settings = settings with { DefaultSessionMinutes = (int)clamped.Value.TotalMinutes };
        }

        if (message.Language is { } language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return IpcResponse.Fail(IpcCodes.ConfigLanguageBlank);
            }

            settings = settings with { Language = language.Trim() };
        }

        if (message.VerboseLogging is { } verbose)
        {
            settings = settings with { VerboseLogging = verbose };
        }

        if (message.WfpEnabled is { } wfpEnabled)
        {
            settings = settings with { WfpEnabled = wfpEnabled };
        }

        config.Save(settings);

        // The running session keeps the cool-down it started with.
        if (engine.Session is { } running
            && message.CoolDownMinutes is not null
            && (int)running.CoolDown.TotalMinutes != settings.CoolDownMinutes)
        {
            warnings.Add(new IpcNotice(IpcCodes.ConfigCoolDownAppliesNextSession, []));
        }

        // WfpEnabled reaches the address layer through the config file, so a pass is needed.
        return Applied("settings updated", warnings, enforcementChanged: true);
    }

    /// <summary>The engine's own refusal, its code and its warnings, or null if it accepted.</summary>
    private static IpcResponse? Refused(CommandResult result) =>
        result.Accepted
            ? null
            : new IpcResponse { Accepted = false, Error = result.RejectionReason, Warnings = Wire(result.Warnings) };

    // Whole minutes, invariant: the reader puts the number in a sentence of its own language.
    private static string Minutes(TimeSpan value) =>
        ((int)value.TotalMinutes).ToString(CultureInfo.InvariantCulture);

    /// <summary>While Ended the layers still hold the block, so rule changes are refused.</summary>
    private IpcResponse? RulesAreFrozen() =>
        engine.State is SessionState.Ended
            ? IpcResponse.Fail(IpcCodes.RulesFrozen)
            : null;

    private static bool ReadSite(IpcRequest request, out SiteRule rule, [NotNullWhen(false)] out IpcResponse? refusal)
    {
        rule = null!;

        if (request.Site is not { } message)
        {
            refusal = IpcResponse.Fail(IpcCodes.RulesSiteMissing);

            return false;
        }

        // SiteRule throws on a blank domain, so check here.
        if (string.IsNullOrWhiteSpace(message.Domain))
        {
            refusal = IpcResponse.Fail(IpcCodes.RulesSiteDomainBlank);

            return false;
        }

        rule = new SiteRule(message.Domain, message.IncludeSubdomains);
        refusal = null;

        return true;
    }

    private static bool ReadApp(IpcRequest request, out AppRule rule, [NotNullWhen(false)] out IpcResponse? refusal)
    {
        rule = null!;

        if (request.App is not { } message)
        {
            refusal = IpcResponse.Fail(IpcCodes.RulesAppMissing);

            return false;
        }

        // As in ChronosConfig.ToBlockList: Enum.TryParse accepts any number, so check IsDefined.
        if (!Enum.TryParse<AppMatchKind>(message.MatchKind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
        {
            refusal = IpcResponse.Fail(IpcCodes.RulesAppKindUnknown);

            return false;
        }

        if (string.IsNullOrWhiteSpace(message.Value))
        {
            refusal = IpcResponse.Fail(IpcCodes.RulesAppValueBlank);

            return false;
        }

        rule = new AppRule(kind, message.Value);
        refusal = null;

        return true;
    }

    // Identity is the normalised domain, not the pair with the subdomain flag.
    private static List<SiteRuleDto> Without(IEnumerable<SiteRuleDto> stored, SiteRule rule) =>
    [
        .. stored.Where(candidate =>
            string.IsNullOrWhiteSpace(candidate.Domain)
            || !string.Equals(DomainMatcher.Normalize(candidate.Domain), rule.Domain, StringComparison.Ordinal)),
    ];

    // Case-insensitive: Windows paths and file names are.
    private static List<AppRuleDto> Without(IEnumerable<AppRuleDto> stored, AppRule rule) =>
    [
        .. stored.Where(candidate =>
            !Enum.TryParse<AppMatchKind>(candidate.MatchKind, ignoreCase: true, out var kind)
            || !Enum.IsDefined(kind)
            || kind != rule.Kind
            || !string.Equals(candidate.Value?.Trim(), rule.Value, StringComparison.OrdinalIgnoreCase)),
    ];

    private IpcResponse Applied(string action, IReadOnlyList<IpcNotice> warnings, bool enforcementChanged)
    {
        // Log the action, never the rule: domains and paths stay out of Information and above.
        using (logger.BeginScope(new Dictionary<string, object?> { ["SessionId"] = engine.Session?.Id }))
        {
            logger.LogInformation("Accepted command: {Action}.", action);
        }

        if (enforcementChanged)
        {
            scheduler.RequestImmediate(action);
        }

        // One snapshot for both the answer and the event.
        var status = CurrentStatus();
        events.Publish(IpcEvent.StatusChanged(status));

        return IpcResponse.Ok(status, CurrentConfig(config.Load()), warnings);
    }

    private static ConfigPayload CurrentConfig(ChronosConfig settings)
    {
        // Through ToBlockList so only rules that will actually be enforced are reported.
        var list = settings.ToBlockList();

        return new ConfigPayload(
            Wire(list.Sites),
            Wire(list.Apps),
            settings.CoolDownMinutes,
            settings.DefaultSessionMinutes,
            settings.Language,
            settings.VerboseLogging,
            settings.WfpEnabled);
    }

    private IpcResponse StartSession(IpcRequest request)
    {
        var settings = config.Load();
        var minutes = request.DurationMinutes ?? settings.DefaultSessionMinutes;

        var result = engine.StartSession(
            settings.ToBlockList(),
            TimeSpan.FromMinutes(minutes),
            TimeSpan.FromMinutes(settings.CoolDownMinutes));

        return Complete(result, "session started");
    }

    private IpcResponse ExtendSession(IpcRequest request)
    {
        if (request.DurationMinutes is not { } minutes || minutes <= 0)
        {
            return IpcResponse.Fail(IpcCodes.SessionExtendNeedsMinutes);
        }

        if (engine.Session is not { } session)
        {
            return IpcResponse.Fail(IpcCodes.SessionNoneToExtend);
        }

        return Complete(engine.SetEndsAt(session.EndsAt.AddMinutes(minutes)), "session extended");
    }

    private IpcResponse Complete(CommandResult result, string action)
    {
        if (Refused(result) is { } refused)
        {
            return refused;
        }

        using (logger.BeginScope(new Dictionary<string, object?> { ["SessionId"] = engine.Session?.Id }))
        {
            logger.LogInformation("Accepted command: {Action}.", action);
        }

        // The caller expects the block to change now, not at the next tick.
        scheduler.RequestImmediate(action);

        var status = CurrentStatus();
        events.Publish(IpcEvent.StatusChanged(status));

        return IpcResponse.Ok(status, Wire(result.Warnings));
    }

    /// <summary>The status, taken under the session gate. For callers that must not touch the engine.</summary>
    public StatusPayload Snapshot()
    {
        lock (gate.Sync)
        {
            return CurrentStatus();
        }
    }

    private StatusPayload CurrentStatus()
    {
        // The engine does not advance itself and the reconcile loop can be 15 seconds away. Tick only
        // ever enters Ended, so it cannot break the runner's assumption about the state it saw.
        engine.Tick();

        var session = engine.Session;

        return new StatusPayload(
            engine.State.ToString(),
            clock.UtcNow,
            session?.StartedAt,
            session?.EndsAt,
            session?.Unlock?.EffectiveAt,
            // A running session keeps its own cool-down; otherwise the file's value.
            session is { } running ? (int)running.CoolDown.TotalMinutes : config.Load().CoolDownMinutes,
            Wire(session?.Rules.Sites),
            Wire(session?.Rules.Apps),
            layers.Current());
    }

    // Sorted: BlockList is a hash set, and string hashing is randomised per process.
    private static IReadOnlyList<SiteRuleMessage> Wire(IEnumerable<SiteRule>? sites) =>
    [
        .. (sites ?? [])
            .OrderBy(site => site.Domain, StringComparer.Ordinal)
            .Select(site => new SiteRuleMessage(site.Domain, site.IncludeSubdomains)),
    ];

    private static IReadOnlyList<AppRuleMessage> Wire(IEnumerable<AppRule>? apps) =>
    [
        .. (apps ?? [])
            .OrderBy(app => app.Kind)
            .ThenBy(app => app.Value, StringComparer.Ordinal)
            .Select(app => new AppRuleMessage(app.Kind.ToString(), app.Value)),
    ];

    // The engine's codes are the wire's (a test holds the two lists together); only the type differs.
    private static IReadOnlyList<IpcNotice> Wire(IReadOnlyList<SessionNotice> notices) =>
        [.. notices.Select(notice => new IpcNotice(notice.Code, notice.Arguments))];
}
