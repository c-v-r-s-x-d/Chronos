namespace Chronos.Core.Enforcement;

public sealed record EnforcerAvailability(bool IsAvailable, string? Reason)
{
    public static EnforcerAvailability Available { get; } = new(true, null);

    public static EnforcerAvailability Unavailable(string reason) => new(false, reason);
}
