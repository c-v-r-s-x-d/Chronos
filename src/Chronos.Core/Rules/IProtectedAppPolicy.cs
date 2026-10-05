namespace Chronos.Core.Rules;

public sealed record ProtectionVerdict(bool IsProtected, string? Reason)
{
    public static ProtectionVerdict Allowed { get; } = new(false, null);

    public static ProtectionVerdict Protect(string reason) => new(true, reason);
}

/// <summary>
/// Decides whether an application may be blocked at all. Implemented by the platform
/// layer, which knows the system directories and processes that must never be killed.
/// </summary>
public interface IProtectedAppPolicy
{
    ProtectionVerdict Evaluate(AppRule rule);
}
