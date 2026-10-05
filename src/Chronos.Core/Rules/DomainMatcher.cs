namespace Chronos.Core.Rules;

public static class DomainMatcher
{
    public static string Normalize(string domain)
    {
        ArgumentNullException.ThrowIfNull(domain);

        return domain.Trim().Trim('.').ToLowerInvariant();
    }

    public static bool Matches(SiteRule rule, string queriedDomain)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(queriedDomain);

        var query = Normalize(queriedDomain);
        if (query.Length == 0)
        {
            return false;
        }

        if (string.Equals(query, rule.Domain, StringComparison.Ordinal))
        {
            return true;
        }

        // The leading dot is what separates a subdomain from a domain that merely
        // ends with the same characters: notexample.com must not match example.com.
        return rule.IncludeSubdomains
            && query.EndsWith("." + rule.Domain, StringComparison.Ordinal);
    }
}
