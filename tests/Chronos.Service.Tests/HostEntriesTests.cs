using Chronos.Core.Rules;
using Chronos.Service.Sites;

namespace Chronos.Service.Tests;

public sealed class HostEntriesTests
{
    private static SiteRule Rule(string domain) => new(domain, includeSubdomains: true);

    [Fact]
    public void Names_AddsTheWwwVariant()
    {
        Assert.Equal(["example.com", "www.example.com"], HostEntries.Names([Rule("example.com")]));
    }

    [Fact]
    public void Names_DoesNotStackWwwOnADomainThatAlreadyHasIt()
    {
        Assert.Equal(["www.example.com"], HostEntries.Names([Rule("www.example.com")]));
    }

    [Fact]
    public void Names_AreOrderedTheSameWhicheverOrderTheRulesArrive()
    {
        var one = HostEntries.Names([Rule("b.com"), Rule("a.com")]);
        var other = HostEntries.Names([Rule("a.com"), Rule("b.com")]);

        Assert.Equal(one, other);
    }

    [Fact]
    public void Names_CollapseDuplicates()
    {
        Assert.Equal(["example.com", "www.example.com"], HostEntries.Names([Rule("example.com"), Rule("EXAMPLE.COM.")]));
    }

    [Fact]
    public void Names_SkipDomainsThatAreNotPureAscii()
    {
        // The hosts file is written as Latin1 so foreign bytes survive; a name outside ASCII would not
        // survive that encoding, and punycode (IDN) is not supported.
        Assert.Equal(["example.com", "www.example.com"], HostEntries.Names([Rule("пример.рф"), Rule("example.com")]));
    }

    [Fact]
    public void SkippedCount_ReportsHowManyDomainsWereNotPureAscii()
    {
        Assert.Equal(1, HostEntries.SkippedCount([Rule("пример.рф"), Rule("example.com")]));
        Assert.Equal(0, HostEntries.SkippedCount([Rule("example.com")]));
    }

    [Fact]
    public void Render_EmitsBothAddressFamiliesAndNeverLoopback()
    {
        var lines = HostEntries.Render([Rule("example.com")]);

        Assert.Contains("0.0.0.0 example.com", lines);
        Assert.Contains(":: example.com", lines);
        // 127.0.0.1 would send the browser at whatever listens locally.
        Assert.DoesNotContain(lines, line => line.Contains("127.0.0.1", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_StartsWithAHeaderExplainingTheBlock()
    {
        Assert.Equal(HostEntries.Header, HostEntries.Render([Rule("example.com")])[0]);
    }

    [Fact]
    public void Header_NamesTheCommandThatRemovesTheBlock()
    {
        // Whoever opens the file gets the rollback path without asking anyone.
        Assert.Contains("chronos clean", HostEntries.Header, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ProducesNoBlankLines()
    {
        // HostsBlock.Read drops blank lines, so a blank line here would make an applied block
        // compare unequal to the desired one for ever.
        Assert.DoesNotContain(HostEntries.Render([Rule("example.com")]), string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void NamesIn_RoundTripsRender()
    {
        var rules = new[] { Rule("example.com"), Rule("other.org") };

        Assert.Equal(HostEntries.Names(rules), HostEntries.NamesIn(HostEntries.Render(rules)));
    }

    [Fact]
    public void NamesIn_IgnoresCommentsAndForeignLinesAndAcceptsTabs()
    {
        string[] lines =
        [
            "# a comment",
            "0.0.0.0\texample.com",
            "10.0.0.1 intranet",
            "nonsense",
        ];

        Assert.Equal(["example.com"], HostEntries.NamesIn(lines));
    }
}
