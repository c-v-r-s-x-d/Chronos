namespace Chronos.Ipc;

public static class IpcProtocol
{
    // Bump when the wire changes incompatibly; clients on another version are refused.
    // 3 added SiteBlocked and IpcEvent.Domain. 4 made Error and Warnings codes instead of sentences.
    public const int Version = 4;

    public const string DefaultPipeName = "chronos";

    /// <summary>The command that turns the connection into an event stream: after it is accepted the server writes events and the client stops asking.</summary>
    public const string SubscribeCommand = "Subscribe";
}

public sealed record IpcRequest
{
    public int ProtocolVersion { get; init; } = IpcProtocol.Version;

    public string Command { get; init; } = string.Empty;

    public int? DurationMinutes { get; init; }

    public SiteRuleMessage? Site { get; init; }

    public AppRuleMessage? App { get; init; }

    public SettingsMessage? Settings { get; init; }
}

/// <summary>A change to the settings. Null leaves a field alone, so concurrent clients do not overwrite each other.</summary>
public sealed record SettingsMessage(
    int? CoolDownMinutes = null,
    int? DefaultSessionMinutes = null,
    string? Language = null,
    bool? VerboseLogging = null,
    bool? WfpEnabled = null);

/// <summary>
/// A blocking layer as the last reconcile pass found it. A layer no pass has reported on is absent
/// from the status, not shown as available. <see cref="ReasonCode"/> is a stable identifier such as
/// "wfp.engine-unavailable" that the interface maps to its own text.
/// </summary>
public sealed record LayerStatus(
    string Name,
    bool IsAvailable,
    string? ReasonCode,
    string LastOutcome,
    DateTimeOffset? ObservedAt);

// The wire form of a rule, versioned with the protocol. SiteRuleDto and AppRuleDto are the file form.
public sealed record SiteRuleMessage(string Domain, bool IncludeSubdomains);

public sealed record AppRuleMessage(string MatchKind, string Value);

public sealed record StatusPayload(
    string State,
    // The service's clock, so clients count down from EndsAt without trusting their own.
    DateTimeOffset Now,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset? UnlockEffectiveAt,
    int CoolDownMinutes,
    IReadOnlyList<SiteRuleMessage> Sites,
    IReadOnlyList<AppRuleMessage> Apps,
    IReadOnlyList<LayerStatus> Layers);

/// <summary>The configuration as the next session will read it, not the file verbatim: unparseable lines are left out. Same rule order as <see cref="StatusPayload"/>.</summary>
public sealed record ConfigPayload(
    IReadOnlyList<SiteRuleMessage> Sites,
    IReadOnlyList<AppRuleMessage> Apps,
    int CoolDownMinutes,
    int DefaultSessionMinutes,
    string Language,
    bool VerboseLogging,
    bool WfpEnabled);

/// <summary>
/// The answer to one request. Which payload is filled depends on the command:
/// <see cref="Status"/> on every accepted command; <see cref="Config"/> on <c>GetConfig</c> and the
/// commands that write configuration (it is null for session commands); neither on a refusal, which
/// carries a code in <see cref="Error"/> (a protocol version mismatch is an English sentence, since
/// the reader may not know the codes). <see cref="Warnings"/> may appear on any answer.
/// </summary>
public sealed record IpcResponse
{
    public int ProtocolVersion { get; init; } = IpcProtocol.Version;

    public bool Accepted { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<IpcNotice> Warnings { get; init; } = [];

    public StatusPayload? Status { get; init; }

    public ConfigPayload? Config { get; init; }

    public static IpcResponse Ok(StatusPayload? status = null, IReadOnlyList<IpcNotice>? warnings = null) =>
        new() { Accepted = true, Status = status, Warnings = warnings ?? [] };

    public static IpcResponse Ok(
        StatusPayload status,
        ConfigPayload config,
        IReadOnlyList<IpcNotice>? warnings = null) =>
        new() { Accepted = true, Status = status, Config = config, Warnings = warnings ?? [] };

    public static IpcResponse Fail(string error) => new() { Accepted = false, Error = error };
}

/// <summary>A warning: a code from <see cref="IpcCodes"/> plus invariant-culture values such as minutes. Never a domain or a path.</summary>
public sealed record IpcNotice(string Code, IReadOnlyList<string> Arguments);

/// <summary>The names of the layers in <see cref="LayerStatus.Name"/>, where both sides test for one.</summary>
public static class LayerNames
{
    /// <summary>L2, the resolver: the only layer that sees an attempt at a site.</summary>
    public const string Dns = "dns";
}
