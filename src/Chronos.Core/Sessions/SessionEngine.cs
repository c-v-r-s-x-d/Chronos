using System.Globalization;
using Chronos.Core.Enforcement;
using Chronos.Core.Rules;
using Chronos.Core.Time;

namespace Chronos.Core.Sessions;

/// <summary>
/// Drives a single block session as a state machine. Instances are not thread-safe: a timer
/// and an IPC handler will both call into the same instance, so callers must serialise access.
/// <see cref="CurrentPlan"/> does not advance time by itself — call <see cref="Tick"/> first, or
/// you may get a plan that still blocks after the session's end time.
/// </summary>
public sealed class SessionEngine
{
    private readonly IClock _clock;
    private readonly IProtectedAppPolicy _protectedApps;

    public SessionEngine(IClock clock, IProtectedAppPolicy protectedApps)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(protectedApps);

        _clock = clock;
        _protectedApps = protectedApps;
    }

    public SessionState State { get; private set; } = SessionState.Idle;

    public BlockSession? Session { get; private set; }

    public CommandResult StartSession(BlockList rules, TimeSpan duration, TimeSpan coolDown)
    {
        ArgumentNullException.ThrowIfNull(rules);

        if (State is not SessionState.Idle)
        {
            return CommandResult.Reject(SessionCodes.SessionStartWrongState);
        }

        var warnings = new List<SessionNotice>();
        var allowed = FilterProtectedApps(rules, warnings);

        if (allowed.IsEmpty)
        {
            // The warnings say that protected rules were dropped; without them the caller
            // reports an empty list to a user who supplied a non-empty one.
            return CommandResult.Reject(SessionCodes.SessionEmptyBlockList, warnings);
        }

        var clampedDuration = SessionLimits.ClampSessionDuration(duration);
        if (clampedDuration.WasClamped)
        {
            warnings.Add(new SessionNotice(SessionCodes.SessionDurationClamped, [Minutes(clampedDuration.Value)]));
        }

        var clampedCoolDown = SessionLimits.ClampCoolDown(coolDown);
        if (clampedCoolDown.WasClamped)
        {
            warnings.Add(new SessionNotice(SessionCodes.SessionCoolDownClamped, [Minutes(clampedCoolDown.Value)]));
        }

        var now = _clock.UtcNow;
        Session = new BlockSession
        {
            Id = Guid.NewGuid(),
            StartedAt = now,
            EndsAt = now + clampedDuration.Value,
            Rules = allowed,
            CoolDown = clampedCoolDown.Value,
        };
        State = SessionState.Active;

        return CommandResult.Ok(warnings);
    }

    public static SessionEngine Restore(IClock clock, IProtectedAppPolicy protectedApps, BlockSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var engine = new SessionEngine(clock, protectedApps);
        engine.Session = session;
        engine.State = session.Unlock is null ? SessionState.Active : SessionState.UnlockPending;
        engine.Tick();

        return engine;
    }

    public void Tick()
    {
        if (Session is null || State is SessionState.Idle or SessionState.Ended)
        {
            return;
        }

        var now = _clock.UtcNow;

        if (now >= Session.EndsAt)
        {
            State = SessionState.Ended;
            return;
        }

        if (State is SessionState.UnlockPending && now >= Session.Unlock!.EffectiveAt)
        {
            State = SessionState.Ended;
        }
    }

    public void ConfirmCleared()
    {
        if (State is not SessionState.Ended)
        {
            // Reaching here means the caller didn't check State first — a broken protocol,
            // not a user command that can simply be rejected.
            throw new InvalidOperationException($"Cannot confirm cleanup while in state {State}.");
        }

        Session = null;
        State = SessionState.Idle;
    }

    public CommandResult RequestUnlock()
    {
        if (State is not SessionState.Active || Session is null)
        {
            return CommandResult.Reject(SessionCodes.UnlockRequestWrongState);
        }

        var now = _clock.UtcNow;
        Session = Session with { Unlock = new UnlockRequest(now, now + Session.CoolDown) };
        State = SessionState.UnlockPending;

        return CommandResult.Ok();
    }

    public CommandResult CancelUnlock()
    {
        if (State is not SessionState.UnlockPending || Session is null)
        {
            return CommandResult.Reject(SessionCodes.UnlockCancelWrongState);
        }

        Session = Session with { Unlock = null };
        State = SessionState.Active;

        return CommandResult.Ok();
    }

    public CommandResult SetEndsAt(DateTimeOffset newEndsAt)
    {
        if (State is not (SessionState.Active or SessionState.UnlockPending) || Session is null)
        {
            return CommandResult.Reject(SessionCodes.SessionExtendWrongState);
        }

        if (newEndsAt <= Session.EndsAt)
        {
            return CommandResult.Reject(SessionCodes.SessionCannotShorten);
        }

        var warnings = new List<SessionNotice>();
        var maximumEndsAt = Session.StartedAt + SessionLimits.MaxSessionDuration;
        if (newEndsAt > maximumEndsAt)
        {
            warnings.Add(new SessionNotice(SessionCodes.SessionEndClamped, [Minutes(SessionLimits.MaxSessionDuration)]));
            newEndsAt = maximumEndsAt;
        }

        Session = Session with { EndsAt = newEndsAt };

        return CommandResult.Ok(warnings);
    }

    public CommandResult AddSiteRule(SiteRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (State is not (SessionState.Active or SessionState.UnlockPending) || Session is null)
        {
            return CommandResult.Reject(SessionCodes.RulesAddWrongState);
        }

        Session = Session with { Rules = Session.Rules.WithSite(rule) };

        return CommandResult.Ok();
    }

    public CommandResult AddAppRule(AppRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (State is not (SessionState.Active or SessionState.UnlockPending) || Session is null)
        {
            return CommandResult.Reject(SessionCodes.RulesAddWrongState);
        }

        var verdict = _protectedApps.Evaluate(rule);
        if (verdict.IsProtected)
        {
            return CommandResult.Reject(verdict.Reason ?? SessionCodes.RulesAppProtected);
        }

        Session = Session with { Rules = Session.Rules.WithApp(rule) };

        return CommandResult.Ok();
    }

    public EnforcementPlan CurrentPlan()
    {
        if (Session is null || State is not (SessionState.Active or SessionState.UnlockPending))
        {
            return EnforcementPlan.Empty;
        }

        return new EnforcementPlan(Session.Id, Session.EndsAt, Session.Rules.Sites, Session.Rules.Apps);
    }

    private BlockList FilterProtectedApps(BlockList rules, List<SessionNotice> warnings)
    {
        var allowedApps = new List<AppRule>();

        foreach (var app in rules.Apps)
        {
            var verdict = _protectedApps.Evaluate(app);
            if (verdict.IsProtected)
            {
                warnings.Add(new SessionNotice(SessionCodes.SessionProtectedAppSkipped, [FileName(app.Value)]));
                continue;
            }

            allowedApps.Add(app);
        }

        return new BlockList(rules.Sites, allowedApps);
    }

    // Whole minutes, invariant culture; the reader localizes the sentence.
    private static string Minutes(TimeSpan value) =>
        ((int)value.TotalMinutes).ToString(CultureInfo.InvariantCulture);

    // File name only, never the path. Accepts either separator.
    private static string FileName(string value) => value[(value.LastIndexOfAny(['\\', '/']) + 1)..];
}
