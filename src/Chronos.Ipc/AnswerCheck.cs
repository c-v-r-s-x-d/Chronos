namespace Chronos.Ipc;

/// <summary>Whether a parsed answer is usable: a field left out arrives as null in a non-nullable property. Shared by the CLI and the interface.</summary>
public static class AnswerCheck
{
    /// <summary>
    /// An accepted answer carries the status, and the configuration too when <paramref name="wantsConfig"/>.
    /// A refusal carries neither, so only what is present is checked.
    /// </summary>
    public static bool IsUsable(IpcResponse? response, bool wantsConfig = false)
    {
        if (response is null)
        {
            return false;
        }

        if (response.Accepted && (response.Status is null || (wantsConfig && response.Config is null)))
        {
            return false;
        }

        return (response.Status is null || IsUsable(response.Status))
            && (response.Config is null || IsUsable(response.Config));
    }

    /// <summary>A status whose state, lists and every element of them are there.</summary>
    public static bool IsUsable(StatusPayload? status) =>
        status is { State: not null, Sites: not null, Apps: not null, Layers: not null }
        && status.Sites.All(site => site is not null)
        && status.Apps.All(app => app is not null)
        && status.Layers.All(layer => layer?.Name is not null);

    public static bool IsUsable(ConfigPayload? config) =>
        config is { Sites: not null, Apps: not null }
        && config.Sites.All(site => site is not null)
        && config.Apps.All(app => app is not null);
}
