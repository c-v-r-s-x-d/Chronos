using Chronos.Core.Rules;

namespace Chronos.Core.Sessions;

public sealed record BlockSession
{
    public required Guid Id { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset EndsAt { get; init; }

    public required BlockList Rules { get; init; }

    public required TimeSpan CoolDown { get; init; }

    public UnlockRequest? Unlock { get; init; }
}
