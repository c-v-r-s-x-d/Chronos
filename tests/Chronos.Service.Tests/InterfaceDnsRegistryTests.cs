using System.Net.NetworkInformation;
using Chronos.Service.Dns;
using Microsoft.Win32;

namespace Chronos.Service.Tests;

/// <summary>
/// What the registry says each interface resolves through, and which interfaces are asked about.
/// Filtering, separators and mode are checked through the seam <c>Describe</c> takes, with values
/// this file hands over. Which values the seam is fed from is what must not be wrong: answering with
/// the DHCP list where the static one belongs would restore a static interface back to DHCP with its
/// list gone. So the reader is also run against a scratch key under HKCU (a suite does not write to
/// HKLM), since the only way to show which value it reads is to write the value it reads back.
/// </summary>
public sealed class InterfaceDnsRegistryTests
{
    /// <summary>What a registry with nothing in it for any interface answers.</summary>
    private static readonly Func<string, (string? Static, string? Dhcp)> NoKey = _ => (null, null);

    [Fact]
    public void TheValuesReadAreTheOnesWindowsKeepsTheInterfaceSettingsIn()
    {
        // Written out rather than taken from the class: a typo in this path reads as every interface having no resolvers, which looks like a machine with a filter miniport.
        Assert.Equal(
            @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces",
            InterfaceDnsRegistry.InterfacesKey);
        Assert.Equal("NameServer", InterfaceDnsRegistry.StaticValue);
        Assert.Equal("DhcpNameServer", InterfaceDnsRegistry.DhcpValue);
    }

    [Fact]
    public void Describe_ReadsAStaticListInTheOrderItIsWrittenIn()
    {
        var described = InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Ethernet", up: true, addresses: 1)],
            _ => (Static: "8.8.8.8,8.8.4.4", Dhcp: null));

        var one = Assert.Single(described);
        Assert.False(one.IsDhcp);
        Assert.Equal(["8.8.8.8", "8.8.4.4"], one.Servers);
    }

    [Fact]
    public void Describe_ReadsAStaticListThatIsSeparatedBySpacesInstead()
    {
        var described = InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Ethernet", up: true, addresses: 1)],
            _ => (Static: "8.8.8.8 8.8.4.4", Dhcp: null));

        Assert.Equal(["8.8.8.8", "8.8.4.4"], Assert.Single(described).Servers);
    }

    [Fact]
    public void Describe_DropsTheSpaceWindowsLeavesAfterASeparator()
    {
        var described = InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Ethernet", up: true, addresses: 1)],
            _ => (Static: "8.8.8.8, 8.8.4.4,", Dhcp: null));

        // Not "8.8.8.8", " 8.8.4.4", "": a server with a leading space is refused by the command builder, and an empty one reaches netsh as a missing argument.
        Assert.Equal(["8.8.8.8", "8.8.4.4"], Assert.Single(described).Servers);
    }

    [Fact]
    public void Describe_ReadsAnInterfaceWithAnEmptyStaticValueAsDhcp()
    {
        var described = InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Wi-Fi", up: true, addresses: 1)],
            _ => (Static: string.Empty, Dhcp: "172.20.10.1"));

        var one = Assert.Single(described);
        Assert.True(one.IsDhcp);
        Assert.Equal(["172.20.10.1"], one.Servers);
    }

    [Fact]
    public void Describe_ReadsAnInterfaceWhoseStaticValueIsNothingButSpacesAsDhcp()
    {
        // Windows leaves NameServer behind as a blank string when an interface goes back onto DHCP. Read as
        // a static list it would be a static list of no servers, and the restore would use a mode it was never in.
        var described = InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Wi-Fi", up: true, addresses: 1)],
            _ => (Static: "   ", Dhcp: "172.20.10.1"));

        var one = Assert.Single(described);
        Assert.True(one.IsDhcp);
        Assert.Equal(["172.20.10.1"], one.Servers);
    }

    [Fact]
    public void Describe_ReadsAnInterfaceWithOneStaticServerAsStatic()
    {
        // The other side of the blank value: one character of a real address makes this a static interface, and restoring it must not hand it to DHCP.
        var described = InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Wi-Fi", up: true, addresses: 1)],
            _ => (Static: "1.1.1.1", Dhcp: "172.20.10.1"));

        var one = Assert.Single(described);
        Assert.False(one.IsDhcp);

        // And the list is the static one: the DHCP value stays in the registry under a static configuration, so reading it would back up servers the interface does not use.
        Assert.Equal(["1.1.1.1"], one.Servers);
    }

    [Fact]
    public void Describe_SplitsTheDhcpListOnEitherSeparator()
    {
        // Windows writes this one space-separated; the static value is comma-separated.
        var described = InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Wi-Fi", up: true, addresses: 1)],
            _ => (Static: null, Dhcp: "1.1.1.1 9.9.9.9"));

        Assert.Equal(["1.1.1.1", "9.9.9.9"], Assert.Single(described).Servers);
    }

    [Fact]
    public void Describe_SplitsACommaSeparatedDhcpListToo()
    {
        var described = InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Wi-Fi", up: true, addresses: 1)],
            _ => (Static: null, Dhcp: "1.1.1.1,9.9.9.9"));

        Assert.Equal(["1.1.1.1", "9.9.9.9"], Assert.Single(described).Servers);
    }

    [Fact]
    public void Describe_ReadsADhcpInterfaceThatWasHandedNoServersAtAll()
    {
        // Why IsDhcp is carried rather than derived from the list: this interface and one with no key at
        // all report the same servers, and the list cannot say that both are to be handed back to DHCP.
        var described = InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Wi-Fi", up: true, addresses: 1)],
            _ => (Static: string.Empty, Dhcp: string.Empty));

        var one = Assert.Single(described);
        Assert.True(one.IsDhcp);
        Assert.Empty(one.Servers);
    }

    [Fact]
    public void Describe_LeavesOutAnInterfaceThatIsDown()
    {
        Assert.Empty(InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Ethernet 2", up: false, addresses: 1)], _ => ("8.8.8.8", null)));
    }

    [Fact]
    public void Describe_KeepsTheSameInterfaceOnceItIsUp()
    {
        // The pair of the test above: everything else is the same, so being down is the whole reason it was left out.
        Assert.Single(InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Ethernet 2", up: true, addresses: 1)], _ => ("8.8.8.8", null)));
    }

    [Fact]
    public void Describe_LeavesOutAnInterfaceWithNoAddressOfItsOwn()
    {
        // The filter miniports: most "interfaces" are up and carry no address, and they buried the one real finding in the diagnostic package's [dns] section.
        Assert.Empty(InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "WFP Native MAC Layer LightWeight Filter", up: true, addresses: 0)],
            _ => ("8.8.8.8", null)));
    }

    [Fact]
    public void Describe_KeepsAnInterfaceHoldingASingleAddress()
    {
        // One address is enough: the question is whether a DNS query can leave over this interface.
        Assert.Single(InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Ethernet", up: true, addresses: 1)], _ => ("8.8.8.8", null)));
    }

    [Fact]
    public void Describe_LeavesOutTheLoopback()
    {
        Assert.Empty(InterfaceDnsRegistry.Describe([Loopback("{aaa}")], _ => ("fec0:0:0:ffff::1", null)));
    }

    [Fact]
    public void Describe_KeepsAnAdapterOfEveryOtherKind()
    {
        // The pair of the test above, and the kind that matters most: the wireless adapter carries the traffic.
        Assert.Single(InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Wi-Fi", up: true, addresses: 1, kind: NetworkInterfaceType.Wireless80211)],
            _ => ("8.8.8.8", null)));
    }

    [Fact]
    public void Describe_LeavesOutAnInterfaceWithNoIPv4StackToName()
    {
        // The values above are the IPv4 ones, written by the "interface ipv4" command and addressed by the
        // IPv4 interface index. An adapter with none has nothing to say here.
        Assert.Empty(InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Ethernet", up: true, addresses: 1, ipv4: false)], _ => ("8.8.8.8", null)));
    }

    [Fact]
    public void Describe_LeavesOutAnInterfaceThePlatformWillNotDescribe()
    {
        // Unlike the diagnostic package, which keeps such an adapter so a reader sees it, this list is
        // what gets written back: an adapter whose settings could not be read has no backup, and taking
        // it over would leave nothing to restore.
        Assert.Empty(InterfaceDnsRegistry.Describe([Unreadable("{aaa}")], _ => ("8.8.8.8", null)));
    }

    [Fact]
    public void Describe_KeepsAnInterfaceWhoseRegistryKeyIsMissing()
    {
        // An adapter with no key of its own has no resolvers, which is a finding, not a reason to report one adapter fewer.
        var described = InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Ethernet", up: true, addresses: 1)], NoKey);

        var one = Assert.Single(described);
        Assert.Empty(one.Servers);
        Assert.True(one.IsDhcp);
    }

    [Fact]
    public void Describe_AsksTheRegistryForEachAdaptersOwnKeyName()
    {
        var asked = new List<string>();

        InterfaceDnsRegistry.Describe(
            [
                Adapter("{11111111-2222-3333-4444-555555555555}", "Ethernet", up: true, addresses: 1),
                Adapter("{66666666-7777-8888-9999-000000000000}", "Wi-Fi", up: true, addresses: 1),
            ],
            guid =>
            {
                asked.Add(guid);

                return (null, null);
            });

        Assert.Equal(
            ["{11111111-2222-3333-4444-555555555555}", "{66666666-7777-8888-9999-000000000000}"],
            asked);
    }

    [Fact]
    public void Describe_AsksAboutNothingItIsGoingToLeaveOut()
    {
        // Most keys under the interfaces key name no servers; the adapters are what is walked, and the registry is only consulted for the ones that survive filtering.
        var asked = new List<string>();

        InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Filter", up: true, addresses: 0), Loopback("{bbb}"), Adapter("{ccc}", "Down", up: false, addresses: 1)],
            guid =>
            {
                asked.Add(guid);

                return (null, null);
            });

        Assert.Empty(asked);
    }

    [Fact]
    public void Describe_CarriesTheIndexThatNetshWillBeGivenAndTheNamesThatIdentifyTheAdapter()
    {
        var described = InterfaceDnsRegistry.Describe(
            [Adapter("{aaa}", "Wi-Fi", up: true, addresses: 1, index: 22)], NoKey);

        var one = Assert.Single(described);
        Assert.Equal(22, one.Index);
        Assert.Equal("{aaa}", one.Guid);
        Assert.Equal("Wi-Fi", one.Name);
    }

    [Fact]
    public void Describe_KeepsTheAdaptersApartAndInTheOrderTheyArrive()
    {
        var described = InterfaceDnsRegistry.Describe(
            [
                Adapter("{aaa}", "Ethernet", up: true, addresses: 1, index: 12),
                Adapter("{bbb}", "Wi-Fi", up: true, addresses: 1, index: 22),
            ],
            guid => guid == "{aaa}" ? ("8.8.8.8", null) : (null, "172.20.10.1"));

        Assert.Equal(["{aaa}", "{bbb}"], described.Select(state => state.Guid));
        Assert.Equal([12, 22], described.Select(state => state.Index));
        Assert.Equal([false, true], described.Select(state => state.IsDhcp));
        Assert.Equal(["8.8.8.8"], described[0].Servers);
        Assert.Equal(["172.20.10.1"], described[1].Servers);
    }

    [Fact]
    public void Describe_RefusesToBeCalledWithoutAdaptersOrWithoutARegistry()
    {
        Assert.Throws<ArgumentNullException>(() => InterfaceDnsRegistry.Describe(null!, NoKey));
        Assert.Throws<ArgumentNullException>(
            () => InterfaceDnsRegistry.Describe([Adapter("{aaa}", "Ethernet", up: true, addresses: 1)], null!));
    }

    [Fact]
    public void ReadNamesOnlyAdaptersThisMachineHas()
    {
        // The wiring of Read onto the live seams, which Describe alone says nothing about. Read only.
        //
        // One enumeration, handed to the reader and then checked against: enumerating twice would compare
        // two moments of a machine whose adapters come and go.
        var adapters = NetworkInterface.GetAllNetworkInterfaces();

        foreach (var state in new InterfaceDnsRegistry().Read(adapters))
        {
            var adapter = Assert.Single(adapters, candidate => candidate.Id == state.Guid);
            Assert.Equal(adapter.Name, state.Name);
            Assert.NotEqual(NetworkInterfaceType.Loopback, adapter.NetworkInterfaceType);
            Assert.Equal(OperationalStatus.Up, adapter.OperationalStatus);

            // Windows numbers interfaces from 1, and this number is how netsh is told which adapter to change.
            Assert.True(state.Index >= 1, $"'{state.Name}' was reported with interface index {state.Index}.");
        }
    }

    [RequiresNetwork]
    public void ReadFindsTheInterfacesOfAMachineThatHasOne()
    {
        // All this one can be held to: the default root, key path and live adapter list together describe
        // something. An adapter carrying IPv4 traffic is what the attribute asks the machine for.
        //
        // Which values are read and what they mean is the scratch key's question below; the ones under
        // HKLM are whatever this machine holds today.
        Assert.NotEmpty(new InterfaceDnsRegistry().Read());
    }

    [Fact]
    public void ReadTakesTheStaticListFromTheValueWindowsWritesItIn()
    {
        // Both values are written, and they differ: a reader with the two the wrong way round would report
        // the right shape with the wrong addresses and mode, and a restore would put a static interface
        // back onto DHCP, losing its list.
        using var scratch = new ScratchInterfaces();
        scratch.Write("{aaa}", ("NameServer", "8.8.8.8,8.8.4.4"), ("DhcpNameServer", "172.20.10.1"));

        var one = Assert.Single(scratch.Read(Adapter("{aaa}", "Ethernet", up: true, addresses: 1)));

        Assert.False(one.IsDhcp);
        Assert.Equal(["8.8.8.8", "8.8.4.4"], one.Servers);
    }

    [Fact]
    public void ReadTakesTheDhcpListFromTheValueWindowsWritesThatOneIn()
    {
        // The dangerous direction: this interface is on DHCP, and a reader that read the static value would call it static and never hand it back.
        using var scratch = new ScratchInterfaces();
        scratch.Write("{aaa}", ("NameServer", string.Empty), ("DhcpNameServer", "1.1.1.1 9.9.9.9"));

        var one = Assert.Single(scratch.Read(Adapter("{aaa}", "Wi-Fi", up: true, addresses: 1)));

        Assert.True(one.IsDhcp);

        // And the space-separated shape, read out of a registry: Windows writes the DHCP list this way and the static one comma-separated.
        Assert.Equal(["1.1.1.1", "9.9.9.9"], one.Servers);
    }

    [Fact]
    public void ReadLooksAtNothingUnderTheKeyButThoseTwoValues()
    {
        // The key holds a dozen other values (domain, addresses, lease times), none of them a list of
        // servers. A mistyped name would report a domain name as a server to put back.
        using var scratch = new ScratchInterfaces();
        scratch.Write(
            "{aaa}",
            ("NameServer", "8.8.8.8"),
            ("Domain", "corp.example"),
            ("DhcpDomain", "lan.example"),
            ("DhcpIPAddress", "172.20.10.4"));

        var one = Assert.Single(scratch.Read(Adapter("{aaa}", "Ethernet", up: true, addresses: 1)));

        Assert.Equal(["8.8.8.8"], one.Servers);
    }

    [Fact]
    public void ReadAsksTheKeyOfTheInterfaceAndNotTheOneEveryInterfaceHangsFrom()
    {
        // The parent holds one subkey per adapter and values of its own that belong to no adapter. Opening
        // it instead would hand every interface the same list.
        using var scratch = new ScratchInterfaces();
        scratch.WriteOnTheParent(("NameServer", "203.0.113.1"));
        scratch.Write("{aaa}", ("NameServer", "8.8.8.8"));

        var one = Assert.Single(scratch.Read(Adapter("{aaa}", "Ethernet", up: true, addresses: 1)));

        Assert.Equal(["8.8.8.8"], one.Servers);
    }

    [Fact]
    public void ReadReportsAnInterfaceWithNoKeyOfItsOwnAsOneWithNoServers()
    {
        // Nothing is written for this adapter, so there is no key to open. No key means no resolvers, a
        // state to record, not a reason to report one adapter fewer.
        using var scratch = new ScratchInterfaces();
        scratch.Write("{aaa}", ("NameServer", "8.8.8.8"));

        var one = Assert.Single(scratch.Read(Adapter("{bbb}", "Ethernet", up: true, addresses: 1)));

        Assert.Empty(one.Servers);
        Assert.True(one.IsDhcp);
    }

    [Fact]
    public void ReadTakesAValueThatIsNotAStringAsNoListAtAll()
    {
        // A REG_DWORD where the list belongs is not a list of servers; guessing at one would drive a restore by a number.
        using var scratch = new ScratchInterfaces();
        scratch.WriteNumber("{aaa}", "NameServer", 134744072);

        var one = Assert.Single(scratch.Read(Adapter("{aaa}", "Ethernet", up: true, addresses: 1)));

        Assert.True(one.IsDhcp);
        Assert.Empty(one.Servers);
    }

    [Fact]
    public void ReadTakesADhcpValueThatIsNotAStringAsNoListAtAll()
    {
        using var scratch = new ScratchInterfaces();
        scratch.WriteNumber("{aaa}", "DhcpNameServer", 134744072);

        var one = Assert.Single(scratch.Read(Adapter("{aaa}", "Ethernet", up: true, addresses: 1)));

        Assert.Empty(one.Servers);
    }

    [Fact]
    public void HandedReadsTheDhcpListWhileAStaticListIsInForce()
    {
        // Windows keeps DhcpNameServer current while the takeover's static list is on.
        using var scratch = new ScratchInterfaces();
        scratch.Write("{aaa}", ("NameServer", "127.0.0.1,192.168.1.1"), ("DhcpNameServer", "10.0.0.1 10.0.0.2"));

        Assert.Equal(["10.0.0.1", "10.0.0.2"], scratch.Handed("{aaa}"));
    }

    [Fact]
    public void HandedIsNothingForAnAdapterWithNoKeyOrNoDhcpList()
    {
        using var scratch = new ScratchInterfaces();
        scratch.Write("{aaa}", ("NameServer", "8.8.8.8"));

        Assert.Empty(scratch.Handed("{aaa}"));
        Assert.Empty(scratch.Handed("{bbb}"));
    }

    [Fact]
    public void Describe_AsksEachAdapterForItsPropertiesOnce()
    {
        // The reconcile loop walks the adapters every fifteen seconds against a 20 ms budget, and each ask is a call into the IP helper.
        var adapter = new FakeAdapter(
            "{aaa}",
            "Ethernet",
            OperationalStatus.Up,
            NetworkInterfaceType.Ethernet,
            new FakeProperties(new FakeAddresses(1), new FakeIPv4(7)));

        Assert.Single(InterfaceDnsRegistry.Describe([adapter], _ => ("8.8.8.8", null)));

        Assert.Equal(1, adapter.PropertiesAsked);
    }

    [Fact]
    public void IndexesNameEveryAdapterWithAnIPv4StackUpOrDown()
    {
        // A restore writes through these, and an adapter that is off is still on the machine.
        var indexes = InterfaceDnsRegistry.IndexesOf(
        [
            Adapter("{aaa}", "Wi-Fi", up: false, addresses: 0, index: 12),
            Adapter("{bbb}", "Ethernet", up: true, addresses: 1, index: 7),
            Adapter("{ccc}", "Tunnel", up: true, addresses: 1, ipv4: false),
            Unreadable("{ddd}"),
        ]);

        Assert.Equal(2, indexes.Count);
        Assert.Equal(12, indexes["{AAA}"]);
        Assert.Equal(7, indexes["{bbb}"]);
    }

    [Fact]
    public void IndexesKeepTheFirstOfTwoAdaptersUnderOneGuid()
    {
        var indexes = InterfaceDnsRegistry.IndexesOf(
            [Adapter("{aaa}", "One", up: true, addresses: 1, index: 3), Adapter("{aaa}", "Two", up: true, addresses: 1, index: 4)]);

        Assert.Equal(3, indexes["{aaa}"]);
        Assert.Throws<ArgumentNullException>(() => InterfaceDnsRegistry.IndexesOf(null!));
        Assert.NotNull(new InterfaceDnsRegistry().Indexes());
    }

    [Fact]
    public void ThereIsNoReaderWithoutARootOrWithoutAKeyToReadUnder()
    {
        Assert.Throws<ArgumentNullException>(
            () => new InterfaceDnsRegistry(null!, InterfaceDnsRegistry.InterfacesKey));
        Assert.Throws<ArgumentException>(() => new InterfaceDnsRegistry(Registry.CurrentUser, "   "));
    }

    private static NetworkInterface Adapter(
        string guid,
        string name,
        bool up,
        int addresses,
        int index = 7,
        NetworkInterfaceType kind = NetworkInterfaceType.Ethernet,
        bool ipv4 = true) =>
        new FakeAdapter(
            guid,
            name,
            up ? OperationalStatus.Up : OperationalStatus.Down,
            kind,
            new FakeProperties(new FakeAddresses(addresses), ipv4 ? new FakeIPv4(index) : null));

    private static NetworkInterface Loopback(string guid) =>
        Adapter(guid, "Loopback Pseudo-Interface 1", up: true, addresses: 1, kind: NetworkInterfaceType.Loopback);

    /// <summary>An adapter the platform refuses to describe, as a dying one does.</summary>
    private static NetworkInterface Unreadable(string guid) =>
        new FakeAdapter(guid, "Ethernet", OperationalStatus.Up, NetworkInterfaceType.Ethernet, properties: null);

    /// <summary>
    /// An interfaces key of this test's own: one key per adapter, as Windows keeps them, under a name
    /// nothing else uses and removed afterwards pass or fail. Under HKCU because a suite does not write
    /// under HKLM. The reader is shown the shape of the real key, not the real key.
    /// </summary>
    private sealed class ScratchInterfaces : IDisposable
    {
        private const string Root = @"Software\Chronos.Service.Tests";

        private readonly string _branch = $@"{Root}\{Guid.NewGuid():N}";

        public ScratchInterfaces() => Registry.CurrentUser.CreateSubKey(_branch).Dispose();

        /// <summary>The reader itself, pointed at this key instead of the machine's.</summary>
        public IReadOnlyList<InterfaceDnsState> Read(params NetworkInterface[] adapters) =>
            new InterfaceDnsRegistry(Registry.CurrentUser, _branch).Read(adapters);

        public IReadOnlyList<string> Handed(string guid) =>
            new InterfaceDnsRegistry(Registry.CurrentUser, _branch).Handed(guid);

        /// <summary>Values on one adapter's own key, named after the adapter as Windows names it.</summary>
        public void Write(string guid, params (string Name, string Value)[] values) =>
            Set($@"{_branch}\{guid}", values);

        /// <summary>A number where Windows writes a list, as a damaged or hand-edited key holds one.</summary>
        public void WriteNumber(string guid, string name, int value)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{_branch}\{guid}");

            key.SetValue(name, value, RegistryValueKind.DWord);
        }

        /// <summary>Values on the key the adapters' keys hang from, which belong to no adapter.</summary>
        public void WriteOnTheParent(params (string Name, string Value)[] values) => Set(_branch, values);

        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(_branch, throwOnMissingSubKey: false);

            using var root = Registry.CurrentUser.OpenSubKey(Root, writable: false);
            if (root is not null && root.SubKeyCount == 0 && root.ValueCount == 0)
            {
                root.Dispose();
                try
                {
                    Registry.CurrentUser.DeleteSubKey(Root, throwOnMissingSubKey: false);
                }
                catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException or IOException)
                {
                    // Another class just put its own key under the shared root; whoever finishes last removes it.
                }
            }
        }

        private static void Set(string path, (string Name, string Value)[] values)
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);

            foreach (var (name, value) in values)
            {
                // String, as Windows writes these and the reader takes them: any other kind is not a list of servers and reads as none.
                key.SetValue(name, value, RegistryValueKind.String);
            }
        }
    }
}
