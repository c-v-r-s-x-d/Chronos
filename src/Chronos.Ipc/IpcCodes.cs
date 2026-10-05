namespace Chronos.Ipc;

/// <summary>
/// Every code the service puts in <see cref="IpcResponse.Error"/> or an <see cref="IpcNotice"/>,
/// written <c>&lt;area&gt;.&lt;reason&gt;</c>. The interface and the CLI own the wording. A refusal
/// is a bare code; a warning may carry positional values, named in its comment.
/// </summary>
public static class IpcCodes
{
    public const string RequestInvalidJson = "request.invalid-json";

    public const string RequestEmpty = "request.empty";

    public const string CommandUnknown = "command.unknown";

    public const string SessionStartWrongState = "session.start-wrong-state";

    public const string SessionEmptyBlockList = "session.empty-block-list";

    public const string SessionExtendWrongState = "session.extend-wrong-state";

    public const string SessionCannotShorten = "session.cannot-shorten";

    public const string SessionExtendNeedsMinutes = "session.extend-needs-minutes";

    public const string SessionNoneToExtend = "session.none-to-extend";

    /// <summary>A warning. {0}: the duration it was clamped to, in minutes.</summary>
    public const string SessionDurationClamped = "session.duration-clamped";

    /// <summary>A warning. {0}: the cool-down it was clamped to, in minutes.</summary>
    public const string SessionCoolDownClamped = "session.cooldown-clamped";

    /// <summary>A warning. {0}: the longest a session may last, in minutes.</summary>
    public const string SessionEndClamped = "session.end-clamped";

    /// <summary>A warning, one per protected application left out. {0}: its file name, never its path.</summary>
    public const string SessionProtectedAppSkipped = "session.protected-app-skipped";

    public const string UnlockRequestWrongState = "unlock.request-wrong-state";

    public const string UnlockCancelWrongState = "unlock.cancel-wrong-state";

    public const string RulesAddWrongState = "rules.add-wrong-state";

    public const string RulesFrozen = "rules.frozen";

    public const string RulesSiteMissing = "rules.site-missing";

    public const string RulesSiteDomainBlank = "rules.site-domain-blank";

    public const string RulesSiteNotFound = "rules.site-not-found";

    public const string RulesAppMissing = "rules.app-missing";

    public const string RulesAppKindUnknown = "rules.app-kind-unknown";

    public const string RulesAppValueBlank = "rules.app-value-blank";

    public const string RulesAppNotFound = "rules.app-not-found";

    /// <summary>A protection policy that gave no reason of its own.</summary>
    public const string RulesAppProtected = "rules.app-protected";

    public const string RulesAppProtectedSystemFile = "rules.app-protected-system-file";

    public const string RulesAppProtectedSystemDirectory = "rules.app-protected-system-directory";

    /// <summary>A warning on an accepted removal during a session.</summary>
    public const string RulesRemovalWaitsForNextSession = "rules.removal-waits-for-next-session";

    public const string ConfigSettingsMissing = "config.settings-missing";

    public const string ConfigLanguageBlank = "config.language-blank";

    /// <summary>A warning. {0}: the cool-down it was clamped to, in minutes.</summary>
    public const string ConfigCoolDownClamped = "config.cooldown-clamped";

    /// <summary>A warning. {0}: the default duration it was clamped to, in minutes.</summary>
    public const string ConfigSessionDurationClamped = "config.session-duration-clamped";

    /// <summary>A warning: the running session keeps the cool-down it began with.</summary>
    public const string ConfigCoolDownAppliesNextSession = "config.cooldown-applies-next-session";
}
