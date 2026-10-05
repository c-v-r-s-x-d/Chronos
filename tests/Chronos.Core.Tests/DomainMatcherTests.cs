using Chronos.Core.Rules;

namespace Chronos.Core.Tests;

public sealed class DomainMatcherTests
{
    [Theory]
    [InlineData("Example.COM", "example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("  example.com  ", "example.com")]
    public void Normalize_LowercasesAndTrims(string input, string expected)
    {
        Assert.Equal(expected, DomainMatcher.Normalize(input));
    }

    [Fact]
    public void Matches_ExactDomain()
    {
        var rule = new SiteRule("example.com", includeSubdomains: false);

        Assert.True(DomainMatcher.Matches(rule, "example.com"));
    }

    [Fact]
    public void Matches_IsCaseInsensitiveAndIgnoresTrailingDot()
    {
        var rule = new SiteRule("example.com", includeSubdomains: false);

        Assert.True(DomainMatcher.Matches(rule, "EXAMPLE.com."));
    }

    [Fact]
    public void Matches_SubdomainOnlyWhenFlagIsSet()
    {
        var withSubdomains = new SiteRule("example.com", includeSubdomains: true);
        var withoutSubdomains = new SiteRule("example.com", includeSubdomains: false);

        Assert.True(DomainMatcher.Matches(withSubdomains, "cdn.media.example.com"));
        Assert.False(DomainMatcher.Matches(withoutSubdomains, "cdn.media.example.com"));
    }

    [Fact]
    public void Matches_DoesNotTreatSuffixAsSubdomain()
    {
        var rule = new SiteRule("example.com", includeSubdomains: true);

        Assert.False(DomainMatcher.Matches(rule, "notexample.com"));
    }

    [Fact]
    public void Matches_RejectsEmptyQuery()
    {
        var rule = new SiteRule("example.com", includeSubdomains: true);

        Assert.False(DomainMatcher.Matches(rule, "   "));
    }

    [Fact]
    public void SiteRule_RejectsEmptyDomain()
    {
        Assert.Throws<ArgumentException>(() => new SiteRule("   ", includeSubdomains: false));
    }

    [Fact]
    public void Normalize_StripsLeadingDot()
    {
        Assert.Equal("example.com", DomainMatcher.Normalize(".example.com"));
    }

    [Fact]
    public void Matches_LeadingDotRuleMatchesBareDomainAndSubdomains()
    {
        var rule = new SiteRule(".example.com", includeSubdomains: true);

        Assert.True(DomainMatcher.Matches(rule, "example.com"));
        Assert.True(DomainMatcher.Matches(rule, "cdn.example.com"));
    }
}
