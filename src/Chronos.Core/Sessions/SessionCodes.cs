namespace Chronos.Core.Sessions;

/// <summary>
/// The codes the engine refuses and warns with. The wire's list is <c>Chronos.Ipc.IpcCodes</c>,
/// which the domain does not reference; a service test holds the two to the same values.
/// </summary>
public static class SessionCodes
{
    public const string SessionStartWrongState = "session.start-wrong-state";

    public const string SessionEmptyBlockList = "session.empty-block-list";

    public const string SessionExtendWrongState = "session.extend-wrong-state";

    public const string SessionCannotShorten = "session.cannot-shorten";

    public const string SessionDurationClamped = "session.duration-clamped";

    public const string SessionCoolDownClamped = "session.cooldown-clamped";

    public const string SessionEndClamped = "session.end-clamped";

    public const string SessionProtectedAppSkipped = "session.protected-app-skipped";

    public const string UnlockRequestWrongState = "unlock.request-wrong-state";

    public const string UnlockCancelWrongState = "unlock.cancel-wrong-state";

    public const string RulesAddWrongState = "rules.add-wrong-state";

    public const string RulesAppProtected = "rules.app-protected";
}

/// <summary>
/// A warning as a code and the values it is worded around, in the invariant culture: minutes, or
/// an application's file name. Never a domain or a path.
/// </summary>
public sealed record SessionNotice(string Code, IReadOnlyList<string> Arguments)
{
    public SessionNotice(string code)
        : this(code, [])
    {
    }
}
