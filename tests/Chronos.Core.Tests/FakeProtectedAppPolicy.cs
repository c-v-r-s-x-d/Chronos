using Chronos.Core.Rules;

namespace Chronos.Core.Tests;

internal sealed class FakeProtectedAppPolicy : IProtectedAppPolicy
{
    /// <summary>The reason this policy gives, so a test can tell it from the engine's own fallback.</summary>
    public const string Code = "test.protected-by-policy";

    private readonly HashSet<string> _protectedValues = new(StringComparer.OrdinalIgnoreCase);

    public static FakeProtectedAppPolicy AllowAll() => new();

    public FakeProtectedAppPolicy Protecting(string value)
    {
        _protectedValues.Add(value);
        return this;
    }

    public ProtectionVerdict Evaluate(AppRule rule) =>
        _protectedValues.Contains(rule.Value)
            ? ProtectionVerdict.Protect(Code)
            : ProtectionVerdict.Allowed;
}
