namespace Chronos.Core.Rules;

public sealed record SiteRule
{
    public SiteRule(string domain, bool includeSubdomains)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        Domain = DomainMatcher.Normalize(domain);
        IncludeSubdomains = includeSubdomains;
    }

    public string Domain { get; }

    public bool IncludeSubdomains { get; }
}
