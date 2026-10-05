namespace Chronos.Ipc;

public static class IpcEventKind
{
    /// <summary>The session state changed without this client asking for it.</summary>
    public const string StatusChanged = "StatusChanged";

    /// <summary>A blocked application was terminated, and the block screen is what comes next.</summary>
    public const string AppBlocked = "AppBlocked";

    /// <summary>A blocked domain was asked for, and L2 answered NXDOMAIN.</summary>
    public const string SiteBlocked = "SiteBlocked";
}

/// <summary>One line of the subscription stream. Each event carries the whole status, so a dropped event is harmless.</summary>
/// <param name="Domain">The name a refused attempt asked for. It was observed, not configured, so it is logged only at Debug.</param>
public sealed record IpcEvent(
    string Kind, DateTimeOffset At, StatusPayload? Status, string? AppName, string? Domain)
{
    // At comes from the status so the event and its status agree on the time.
    public static IpcEvent StatusChanged(StatusPayload status) =>
        new(IpcEventKind.StatusChanged, Moment(status), status, null, null);

    /// <summary>The status travels with the event so the block screen needs no extra round trip.</summary>
    public static IpcEvent AppBlocked(StatusPayload status, string appName) =>
        new(IpcEventKind.AppBlocked, Moment(status), status, appName, null);

    /// <summary>A refused site, with the status for the same reason as <see cref="AppBlocked"/>.</summary>
    public static IpcEvent SiteBlocked(StatusPayload status, string domain) =>
        new(IpcEventKind.SiteBlocked, Moment(status), status, null, domain);

    private static DateTimeOffset Moment(StatusPayload status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return status.Now;
    }
}
