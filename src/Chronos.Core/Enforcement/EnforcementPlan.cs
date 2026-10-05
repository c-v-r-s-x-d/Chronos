using Chronos.Core.Rules;

namespace Chronos.Core.Enforcement;

public sealed record EnforcementPlan(
    Guid SessionId,
    DateTimeOffset? EndsAt,
    IReadOnlyCollection<SiteRule> Sites,
    IReadOnlyCollection<AppRule> Apps)
{
    public static EnforcementPlan Empty { get; } = new(Guid.Empty, null, [], []);

    public bool IsEmpty => Sites.Count == 0 && Apps.Count == 0;
}
