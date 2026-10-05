namespace Chronos.Core.Sessions;

public sealed record UnlockRequest(DateTimeOffset RequestedAt, DateTimeOffset EffectiveAt);
