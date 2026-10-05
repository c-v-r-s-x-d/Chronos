using System.Net;
using Chronos.Service.Wfp;

namespace Chronos.Service.Tests;

public sealed class AddressPlanTests
{
    private static IPAddress Ip(string value) => IPAddress.Parse(value);

    // One resolving pass: every address equally recent.
    private static Dictionary<string, IReadOnlyList<SeenAddress>> Resolved(
        params (string Domain, IPAddress[] Addresses)[] entries)
    {
        var result = new Dictionary<string, IReadOnlyList<SeenAddress>>();
        foreach (var (domain, addresses) in entries)
        {
            result[domain] = [.. addresses.Select(address => new SeenAddress(address, 1))];
        }

        return result;
    }

    private static SeenAddress[] SeenOn(long pass, params string[] addresses) =>
        [.. addresses.Select(address => new SeenAddress(Ip(address), pass))];

    [Fact]
    public void Build_GivesOneRecordPerResolvedAddress()
    {
        var addresses = new[] { Ip("93.184.216.1"), Ip("93.184.216.2"), Ip("93.184.216.3") };

        var plan = AddressPlan.Build(
            Resolved(("example.com", addresses)), includeDoh: false, new ProtectedAddresses());

        Assert.Equal(3, plan.Addresses.Count);
        Assert.All(plan.Addresses, entry => Assert.Equal("example.com", entry.Domain));
        Assert.Equal(addresses.ToHashSet(), plan.Addresses.Select(entry => entry.Address).ToHashSet());
    }

    [Fact]
    public void Build_CapsAddressesPerDomainAtThirtyTwoAndWarnsNamingTheDomain()
    {
        var addresses = Enumerable.Range(1, 40).Select(i => Ip($"93.184.216.{i}")).ToArray();

        var plan = AddressPlan.Build(
            Resolved(("big.example.com", addresses)), includeDoh: false, new ProtectedAddresses());

        Assert.Equal(32, plan.Addresses.Count);
        Assert.All(plan.Addresses, entry => Assert.Equal("big.example.com", entry.Domain));
        var warning = Assert.Single(plan.Warnings);
        Assert.Contains("big.example.com", warning.Message, StringComparison.Ordinal);
    }

    // 2000 filters is 1000 addresses: each address costs two filters (ordinary + QUIC).
    // 35 domains of 32 addresses (1120 candidates) cross 1000 partway through the run.
    [Fact]
    public void Build_CapsTotalAddressesAtOneThousandFilters()
    {
        var entries = Enumerable.Range(0, 35)
            .Select(d => (
                Domain: $"d{d:D2}.example.com",
                Addresses: Enumerable.Range(1, 32).Select(k => Ip($"{20 + d}.0.0.{k}")).ToArray()))
            .ToArray();

        var plan = AddressPlan.Build(Resolved(entries), includeDoh: false, new ProtectedAddresses());

        Assert.Equal(1000, plan.Addresses.Count);
        Assert.Single(plan.Warnings);
    }

    // The twelve built-in DoH endpoints are reserved inside the ceiling before any domain
    // address, so a full ceiling cannot crowd them out.
    [Fact]
    public void Build_ReservesTheDohEndpointsInsideTheCeilingBeforeDomainAddresses()
    {
        var plan = AddressPlan.Build(OverflowingDomains(), includeDoh: true, new ProtectedAddresses());

        var doh = plan.Addresses
            .Where(entry => string.Equals(entry.Domain, AddressPlan.DohSourceLabel, StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(1000, plan.Addresses.Count);
        Assert.Equal(DohEndpoints.All.ToHashSet(), doh.Select(entry => entry.Address).ToHashSet());

        Assert.Equal(1000 - DohEndpoints.All.Count, plan.Addresses.Count - doh.Length);
    }

    // The warning must name the domain, so the log shows which side gave way.
    [Fact]
    public void Build_SaysInTheCapWarningThatTheDohEndpointsHoldTheirReservedShare()
    {
        var plan = AddressPlan.Build(OverflowingDomains(), includeDoh: true, new ProtectedAddresses());

        var warning = Assert.Single(plan.Warnings);

        Assert.Contains("d30.example.com", warning.Message, StringComparison.Ordinal);
        Assert.Contains("DNS-over-HTTPS", warning.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, IReadOnlyList<SeenAddress>> OverflowingDomains() => Resolved(
    [
        .. Enumerable.Range(0, 35).Select(d => (
            Domain: $"d{d:D2}.example.com",
            Addresses: Enumerable.Range(1, 32).Select(k => Ip($"{20 + d}.0.0.{k}")).ToArray())),
    ]);

    // The domain ceiling is the total minus the DoH reserve and can reach zero or below.
    // A '==' check would then never fire and every domain address would be taken silently.
    // At a ceiling of 10 with twelve endpoints, domains contribute nothing and the warning
    // still names the domain.
    [Fact]
    public void Build_TakesNoDomainAddressWhenTheDohReserveLeavesNoRoomBelowTheCeiling()
    {
        var addresses = Enumerable.Range(1, 30).Select(i => Ip($"93.184.216.{i}")).ToArray();

        var plan = AddressPlan.Build(
            Resolved(("example.com", addresses)),
            includeDoh: true,
            new ProtectedAddresses(),
            maxAddressesTotal: 10);

        Assert.Equal(DohEndpoints.All.Count, plan.Addresses.Count);
        Assert.All(plan.Addresses, entry => Assert.Equal(AddressPlan.DohSourceLabel, entry.Domain));

        var warning = Assert.Single(plan.Warnings);
        Assert.Equal("example.com", warning.Domain);
    }

    // Above the cap the most recently seen win, so the set follows a CDN changing its addresses.
    [Fact]
    public void Build_AboveThePerDomainCapKeepsTheMostRecentlySeenAddresses()
    {
        var oldest = SeenOn(1, [.. Enumerable.Range(1, 30).Select(i => $"93.184.216.{i}")]);
        var newest = SeenOn(2, [.. Enumerable.Range(1, 10).Select(i => $"93.184.217.{i}")]);

        var plan = AddressPlan.Build(
            new Dictionary<string, IReadOnlyList<SeenAddress>> { ["cdn.example.com"] = [.. oldest, .. newest] },
            includeDoh: false,
            new ProtectedAddresses());

        var kept = plan.Addresses.Select(entry => entry.Address).ToHashSet();

        Assert.Equal(32, kept.Count);
        Assert.All(newest, seen => Assert.Contains(seen.Address, kept));

        // Which twenty-two of the older thirty survive is decided by address, not resolver order.
        Assert.Equal(22, oldest.Count(seen => kept.Contains(seen.Address)));
    }

    // Addresses of one pass tie on recency; the cap must then pick by address, whatever the arrival order.
    [Fact]
    public void Build_BreaksARecencyTieByAddressSoOneResolvingPassIsOrderIndependent()
    {
        var addresses = Enumerable.Range(1, 40).Select(i => $"93.184.216.{i}").ToArray();

        var forward = AddressPlan.Build(
            new Dictionary<string, IReadOnlyList<SeenAddress>> { ["big.example.com"] = SeenOn(7, addresses) },
            includeDoh: false,
            new ProtectedAddresses());
        var reversed = AddressPlan.Build(
            new Dictionary<string, IReadOnlyList<SeenAddress>> { ["big.example.com"] = SeenOn(7, [.. addresses.Reverse()]) },
            includeDoh: false,
            new ProtectedAddresses());

        Assert.Equal(forward.Addresses, reversed.Addresses);
    }

    [Fact]
    public void Build_DropsAddressesFromTheProtectedList()
    {
        var plan = AddressPlan.Build(
            Resolved(("intranet.example.com", [Ip("192.168.1.1"), Ip("93.184.216.9")])),
            includeDoh: false,
            new ProtectedAddresses());

        Assert.Equal([Ip("93.184.216.9")], plan.Addresses.Select(entry => entry.Address));
    }

    // Includes one address on each side of every range boundary.
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("93.184.215.14", false)]
    [InlineData("126.255.255.255", false)]
    [InlineData("127.0.0.0", true)]
    [InlineData("127.255.255.255", true)]
    [InlineData("128.0.0.0", false)]
    [InlineData("169.253.255.255", false)]
    [InlineData("169.254.0.0", true)]
    [InlineData("169.254.255.255", true)]
    [InlineData("169.255.0.0", false)]
    [InlineData("9.255.255.255", false)]
    [InlineData("10.255.255.255", true)]
    [InlineData("11.0.0.0", false)]
    [InlineData("172.15.255.255", false)]
    [InlineData("172.16.0.0", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.0", false)]
    [InlineData("192.167.255.255", false)]
    [InlineData("192.168.0.0", true)]
    [InlineData("192.168.255.255", true)]
    [InlineData("192.169.0.0", false)]
    [InlineData("fe80::", true)]
    [InlineData("febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [InlineData("fec0::", false)]

    // "This network" (RFC 1122): a filtering DNS answers 0.0.0.0 for a refused name.
    [InlineData("0.0.0.0", true)]
    [InlineData("0.255.255.255", true)]
    [InlineData("1.0.0.0", false)]

    [InlineData("::", true)]

    // Carrier-grade NAT (RFC 6598).
    [InlineData("100.63.255.255", false)]
    [InlineData("100.64.0.0", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.128.0.0", false)]

    // Multicast, both families: blocking it breaks mDNS, SSDP and local discovery.
    [InlineData("223.255.255.255", false)]
    [InlineData("224.0.0.0", true)]
    [InlineData("239.255.255.255", true)]
    [InlineData("240.0.0.0", false)]
    [InlineData("ff02::1", true)]
    [InlineData("feff::", false)]
    public void IsProtected_MatchesTheDocumentedRangesAndTheirBoundaries(string address, bool expected)
    {
        Assert.Equal(expected, new ProtectedAddresses().IsProtected(Ip(address)));
    }

    // Characterises BCL behaviour: IPNetwork.Contains already treats a mapped IPv6 address as IPv4,
    // so these pass even without Unmap. The Add_... pair below is the real guard on Unmap.
    [Theory]
    [InlineData("::ffff:10.1.2.3", true)] // mapped RFC 1918
    [InlineData("::ffff:127.0.0.1", true)] // mapped loopback
    [InlineData("::ffff:93.184.215.14", false)] // mapped public
    public void IsProtected_UnwrapsAnIPv4MappedAddressBeforeCheckingTheStaticRanges(string address, bool expected)
    {
        Assert.Equal(expected, new ProtectedAddresses().IsProtected(Ip(address)));
    }

    // Unwrapping must hold on the runtime Add path both ways; uses TEST-NET-3 (RFC 5737) addresses
    // unused elsewhere in this suite.
    [Fact]
    public void Add_OfAMappedAddressProtectsThePlainForm()
    {
        var protectedAddresses = new ProtectedAddresses();
        protectedAddresses.Add(Ip("::ffff:203.0.113.10"));

        Assert.True(protectedAddresses.IsProtected(Ip("203.0.113.10")));
    }

    [Fact]
    public void Add_OfAPlainAddressProtectsTheMappedForm()
    {
        var protectedAddresses = new ProtectedAddresses();
        protectedAddresses.Add(Ip("203.0.113.20"));

        Assert.True(protectedAddresses.IsProtected(Ip("::ffff:203.0.113.20")));
    }

    [Fact]
    public void Add_MakesARuntimeLearnedAddressProtected()
    {
        var protectedAddresses = new ProtectedAddresses();
        var address = Ip("198.51.100.77");

        Assert.False(protectedAddresses.IsProtected(address));

        protectedAddresses.Add(address);

        Assert.True(protectedAddresses.IsProtected(address));
    }

    // Catalogue contents are DohEndpointsTests' job; deriving the expectation from DohEndpoints.All is fine here.
    [Fact]
    public void Build_IncludesEveryDohEndpointWhenRequested()
    {
        var plan = AddressPlan.Build(
            Resolved(("example.com", [Ip("93.184.216.20")])), includeDoh: true, new ProtectedAddresses());

        var dohEntries = plan.Addresses.Where(entry => DohEndpoints.All.Contains(entry.Address)).ToArray();

        var expected = DohEndpoints.All.ToHashSet();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, dohEntries.Select(entry => entry.Address).ToHashSet());

        var labels = dohEntries.Select(entry => entry.Domain).Distinct().ToArray();
        var label = Assert.Single(labels);
        Assert.Contains("doh", label, StringComparison.OrdinalIgnoreCase);
    }

    // WfpEnforcer reads the kind of an address off this field, never off the Domain label: a resolved
    // address is closed whole, a DoH endpoint only on port 443.
    [Fact]
    public void Build_MarksAResolvedAddressForAWholeAddressBlock()
    {
        var plan = AddressPlan.Build(
            Resolved(("example.com", [Ip("93.184.216.20")])), includeDoh: false, new ProtectedAddresses());

        var entry = Assert.Single(plan.Addresses);
        Assert.Equal(BlockScope.WholeAddress, entry.Scope);
    }

    [Fact]
    public void Build_MarksEveryDohEndpointForAPortFourFortyThreeBlock()
    {
        var plan = AddressPlan.Build(
            Resolved(("example.com", [Ip("93.184.216.20")])), includeDoh: true, new ProtectedAddresses());

        var dohEntries = plan.Addresses.Where(entry => DohEndpoints.All.Contains(entry.Address)).ToArray();

        Assert.NotEmpty(dohEntries);
        Assert.All(dohEntries, entry => Assert.Equal(BlockScope.Https443, entry.Scope));
        Assert.All(
            plan.Addresses.Where(entry => !DohEndpoints.All.Contains(entry.Address)),
            entry => Assert.Equal(BlockScope.WholeAddress, entry.Scope));
    }

    [Fact]
    public void Build_OmitsDohEndpointsWhenNotRequested()
    {
        var plan = AddressPlan.Build(
            Resolved(("example.com", [Ip("93.184.216.20")])), includeDoh: false, new ProtectedAddresses());

        Assert.DoesNotContain(plan.Addresses, entry => DohEndpoints.All.Contains(entry.Address));
    }

    // The protected list does not apply to a filter closing port 443: it exists to keep the machine
    // connected, and 443 carries neither name resolution nor routing. Skipping an endpoint that is
    // also the machine's DNS server would leave it unblocked on 443.
    [Fact]
    public void Build_KeepsADohEndpointThatIsAlsoOnTheProtectedList()
    {
        var address = Ip("9.9.9.9"); // Quad9, one of DohEndpoints.All, and this machine's resolver
        var protectedAddresses = new ProtectedAddresses();
        protectedAddresses.Add(address);

        var plan = AddressPlan.Build(
            Resolved(("example.com", [Ip("93.184.216.31")])), includeDoh: true, protectedAddresses);

        var entry = Assert.Single(plan.Addresses, candidate => candidate.Address.Equals(address));
        Assert.Equal(BlockScope.Https443, entry.Scope);
    }

    // Scope follows the address, not how it became known. Blocking one.one.one.one by hand must
    // not close port 53 on 1.1.1.1, the pre-resolver's first default server.
    [Fact]
    public void Build_MarksADohAddressReachedByResolvingADomainForAPortFourFortyThreeBlock()
    {
        var address = Ip("1.1.1.1");

        var plan = AddressPlan.Build(
            Resolved(("one.one.one.one", [address])), includeDoh: false, new ProtectedAddresses());

        var entry = Assert.Single(plan.Addresses);
        Assert.Equal(address, entry.Address);
        Assert.Equal("one.one.one.one", entry.Domain);
        Assert.Equal(BlockScope.Https443, entry.Scope);
    }

    // ScopeFor must check the DoH catalogue before the protected list, or a DoH endpoint that is
    // also the DNS server gets no filter. Other cases pass either way because the built-in loop adds
    // the endpoints; here the address arrives by resolution and the loop does not run.
    [Fact]
    public void Build_KeepsAResolvedDohAddressThatIsAlsoProtectedWithTheBuiltInListSwitchedOff()
    {
        var address = Ip("1.1.1.1"); // A DoH endpoint, and this machine's own DNS server.
        var protectedAddresses = new ProtectedAddresses();
        protectedAddresses.Add(address);

        var plan = AddressPlan.Build(
            Resolved(("one.one.one.one", [address])), includeDoh: false, protectedAddresses);

        var entry = Assert.Single(plan.Addresses);
        Assert.Equal(address, entry.Address);
        Assert.Equal("one.one.one.one", entry.Domain);
        Assert.Equal(BlockScope.Https443, entry.Scope);
    }

    // A protected non-DoH address still produces nothing, whether protected by a static range or at runtime.
    [Fact]
    public void Build_DropsAResolvedAddressProtectedAtRuntimeThatIsNotADohEndpoint()
    {
        var protectedAddresses = new ProtectedAddresses();
        protectedAddresses.Add(Ip("198.51.100.9"));

        var plan = AddressPlan.Build(
            Resolved(("example.com", [Ip("198.51.100.9"), Ip("93.184.216.32")])),
            includeDoh: false,
            protectedAddresses);

        var entry = Assert.Single(plan.Addresses);
        Assert.Equal(Ip("93.184.216.32"), entry.Address);
        Assert.Equal(BlockScope.WholeAddress, entry.Scope);
    }

    // Output is deterministic whatever the input order, or reconcile would rewrite filters every pass.
    [Fact]
    public void Build_ProducesTheSameOrderRegardlessOfInputOrder()
    {
        var a1 = Ip("93.184.216.5");
        var a2 = Ip("93.184.216.9");
        var b1 = Ip("93.184.217.2");

        var first = Resolved(
            ("zzz.example.com", [b1]),
            ("aaa.example.com", [a2, a1]));
        var second = Resolved(
            ("aaa.example.com", [a1, a2]),
            ("zzz.example.com", [b1]));

        var planA = AddressPlan.Build(first, includeDoh: true, new ProtectedAddresses());
        var planB = AddressPlan.Build(second, includeDoh: true, new ProtectedAddresses());

        Assert.Equal(planA.Addresses, planB.Addresses);
        Assert.Equal(planA.Warnings, planB.Warnings);
    }

    [Fact]
    public void Build_OfEmptyInputGivesAnEmptyPlan()
    {
        var plan = AddressPlan.Build(
            new Dictionary<string, IReadOnlyList<SeenAddress>>(), includeDoh: false, new ProtectedAddresses());

        Assert.Empty(plan.Addresses);
        Assert.Empty(plan.Warnings);
    }

    // A resolver repeating an address for one domain must count once against the per-domain cap.
    [Fact]
    public void Build_CountsADuplicateAddressWithinADomainOnce()
    {
        var address = Ip("93.184.216.44");

        var plan = AddressPlan.Build(
            Resolved(("dup.example.com", [address, address])), includeDoh: false, new ProtectedAddresses());

        Assert.Single(plan.Addresses);
    }

    // ::ffff:a.b.c.d and a.b.c.d are one host but IPAddress.Equals differs; a mapped AAAA would take
    // two cap slots and a V6 filter that can never match IPv4. The plan must unmap, as ProtectedAddresses does.
    [Fact]
    public void Build_CollapsesTheMappedAndPlainFormsOfOneAddress()
    {
        var plan = AddressPlan.Build(
            Resolved(("example.com", [Ip("::ffff:93.184.216.34"), Ip("93.184.216.34")])),
            includeDoh: false,
            new ProtectedAddresses());

        var entry = Assert.Single(plan.Addresses);
        Assert.Equal(Ip("93.184.216.34"), entry.Address);
    }

    [Fact]
    public void Build_RejectsANullResolvedDictionary()
    {
        Assert.Throws<ArgumentNullException>(
            () => AddressPlan.Build(null!, includeDoh: false, new ProtectedAddresses()));
    }

    [Fact]
    public void Build_RejectsANullProtectedAddresses()
    {
        Assert.Throws<ArgumentNullException>(
            () => AddressPlan.Build(new Dictionary<string, IReadOnlyList<SeenAddress>>(), includeDoh: false, null!));
    }

    [Fact]
    public void IsProtected_RejectsANullAddress()
    {
        Assert.Throws<ArgumentNullException>(() => new ProtectedAddresses().IsProtected(null!));
    }

    [Fact]
    public void Add_RejectsANullAddress()
    {
        Assert.Throws<ArgumentNullException>(() => new ProtectedAddresses().Add(null!));
    }
}
