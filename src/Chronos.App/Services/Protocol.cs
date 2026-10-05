namespace Chronos.App.Services;

/// <summary>The state words <see cref="Chronos.Ipc.StatusPayload.State"/> can carry. The interface cannot reference the domain enumeration, so they are named once here.</summary>
internal static class EngineState
{
    public const string Idle = "Idle";

    public const string Active = "Active";

    public const string UnlockPending = "UnlockPending";

    /// <summary>Clearing up after a session, and over in seconds. Not a state of the product.</summary>
    public const string Ended = "Ended";

    /// <summary>Whether a block is in force, or null for an unknown word, which is not the same as no session. Ended counts as blocking: the rules are still in place while it clears up.</summary>
    public static bool? Blocks(string state) => state switch
    {
        Idle => false,
        Active or Ended or UnlockPending => true,
        _ => null,
    };
}

/// <summary>The commands the screens send, named once: a literal repeated in view models is unchecked by the compiler.</summary>
internal static class ServiceCommand
{
    /// <summary>The configured lists and settings. Not the status: StatusPayload.Sites and .Apps hold only the running session's rules and are empty when none is running.</summary>
    public const string GetConfig = "GetConfig";

    public const string StartSession = "StartSession";

    public const string AddSiteRule = "AddSiteRule";

    public const string RemoveSiteRule = "RemoveSiteRule";

    public const string AddAppRule = "AddAppRule";

    public const string RemoveAppRule = "RemoveAppRule";

    /// <summary>Longer, never shorter: the engine refuses an end time that is not past the one it holds.</summary>
    public const string ExtendSession = "ExtendSession";

    public const string RequestUnlock = "RequestUnlock";

    public const string CancelUnlock = "CancelUnlock";

    /// <summary>The settings the service keeps for the interface, the language among them. Every field is optional and only the one being changed is filled, so one client cannot undo another's change.</summary>
    public const string UpdateSettings = "UpdateSettings";
}

/// <summary>The ways an app rule can name an application. The service parses the string strictly and refuses what it does not recognise.</summary>
internal static class AppMatch
{
    public const string FileName = "FileName";

    public const string FullPath = "FullPath";
}
