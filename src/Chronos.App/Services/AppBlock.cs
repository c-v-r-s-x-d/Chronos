using Chronos.Ipc;

namespace Chronos.App.Services;

/// <summary>Something the service refused, and the session it refused it in. They travel together because a later status could differ.</summary>
public abstract record BlockEvent(StatusPayload Status)
{
    /// <summary>What the rate limit counts by and the screen names.</summary>
    public abstract string Target { get; }
}

/// <summary>An application the service closed.</summary>
public sealed record AppBlock(string AppName, StatusPayload Status) : BlockEvent(Status)
{
    public override string Target => AppName;
}

/// <summary>A site the resolver refused. The domain came from an attempt, so it is shown and never logged above Debug.</summary>
public sealed record SiteBlock(string Domain, StatusPayload Status) : BlockEvent(Status)
{
    public override string Target => Domain;
}
