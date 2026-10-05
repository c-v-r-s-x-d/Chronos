using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Chronos.Service.Setup;

namespace Chronos.Service.Tests;

/// <summary>
/// Reading the endpoint tables (UDP and TCP). Most tests feed hand-written byte blocks laid out as the platform does,
/// so a wrong row size, offset or port byte order fails here. The last few ask this machine and bind their own
/// loopback socket, never port 53.
/// </summary>
public sealed class PortOwnerTests
{
    private const int Dns = 53;

    /// <summary>How <see cref="PortOwner.Describe"/> names the process this test is running in.</summary>
    private static readonly string Own =
        string.Create(CultureInfo.InvariantCulture, $"process {Environment.ProcessId}");

    [Theory]
    [InlineData(typeof(UdpTableInterop.MIB_UDPROW_OWNER_PID), 12)]
    [InlineData(typeof(UdpTableInterop.MIB_UDP6ROW_OWNER_PID), 28)]
    [InlineData(typeof(TcpTableInterop.MIB_TCPROW_OWNER_PID), 24)]
    [InlineData(typeof(TcpTableInterop.MIB_TCP6ROW_OWNER_PID), 56)]
    public void EveryRowHasTheSizeThePlatformWrites(Type type, int expected)
    {
        Assert.Equal(expected, Marshal.SizeOf(type));
    }

    [Fact]
    public void TheIpv4LayoutIsTheOneTheStructureDeclares()
    {
        // The layout is what the reader uses and the structure is what the header says; the two are checked against each other.
        Assert.Equal(Marshal.SizeOf<UdpTableInterop.MIB_UDPROW_OWNER_PID>(), PortOwner.UdpIpv4.Size);
        Assert.Equal(
            (int)Marshal.OffsetOf<UdpTableInterop.MIB_UDPROW_OWNER_PID>("dwLocalPort"), PortOwner.UdpIpv4.PortOffset);
        Assert.Equal(
            (int)Marshal.OffsetOf<UdpTableInterop.MIB_UDPROW_OWNER_PID>("dwOwningPid"), PortOwner.UdpIpv4.OwnerOffset);
    }

    [Fact]
    public void TheIpv6LayoutIsTheOneTheStructureDeclares()
    {
        // Sixteen bytes of address and a scope id precede the port in the second table.
        Assert.Equal(Marshal.SizeOf<UdpTableInterop.MIB_UDP6ROW_OWNER_PID>(), PortOwner.UdpIpv6.Size);
        Assert.Equal(
            (int)Marshal.OffsetOf<UdpTableInterop.MIB_UDP6ROW_OWNER_PID>("dwLocalPort"), PortOwner.UdpIpv6.PortOffset);
        Assert.Equal(
            (int)Marshal.OffsetOf<UdpTableInterop.MIB_UDP6ROW_OWNER_PID>("dwOwningPid"), PortOwner.UdpIpv6.OwnerOffset);
    }

    [Fact]
    public void TheTcpIpv4LayoutIsTheOneTheStructureDeclares()
    {
        // The TCP row is not the UDP row renamed: the state comes first, so the port is two DWORDs in, and UDP offsets would read an address as a port.
        Assert.Equal(Marshal.SizeOf<TcpTableInterop.MIB_TCPROW_OWNER_PID>(), PortOwner.TcpIpv4.Size);
        Assert.Equal(
            (int)Marshal.OffsetOf<TcpTableInterop.MIB_TCPROW_OWNER_PID>("dwLocalPort"), PortOwner.TcpIpv4.PortOffset);
        Assert.Equal(
            (int)Marshal.OffsetOf<TcpTableInterop.MIB_TCPROW_OWNER_PID>("dwOwningPid"), PortOwner.TcpIpv4.OwnerOffset);
    }

    [Fact]
    public void TheTcpIpv6LayoutIsTheOneTheStructureDeclares()
    {
        // The six-column TCP row is not the UDP one either: the owner is the last DWORD of fifty-six bytes.
        Assert.Equal(Marshal.SizeOf<TcpTableInterop.MIB_TCP6ROW_OWNER_PID>(), PortOwner.TcpIpv6.Size);
        Assert.Equal(
            (int)Marshal.OffsetOf<TcpTableInterop.MIB_TCP6ROW_OWNER_PID>("dwLocalPort"), PortOwner.TcpIpv6.PortOffset);
        Assert.Equal(
            (int)Marshal.OffsetOf<TcpTableInterop.MIB_TCP6ROW_OWNER_PID>("dwOwningPid"), PortOwner.TcpIpv6.OwnerOffset);
    }

    [Fact]
    public void ThePortIsReadOutOfNetworkByteOrder()
    {
        // 53 arrives as 0x00 0x35; read as it stands it is 13568, a port nobody is bound to.
        var row = Row(port: [0x00, 0x35], owner: 1234);

        Assert.Equal(Dns, PortOwner.PortOf(row, PortOwner.UdpIpv4));
    }

    [Fact]
    public void ThePortIsNotTheWholeFieldEither()
    {
        // The high word of the field is not part of the port. A row whose upper bytes carry
        // anything at all still names the same endpoint.
        var row = Row(port: [0x00, 0x35, 0xFF, 0xFF], owner: 1234);

        Assert.Equal(Dns, PortOwner.PortOf(row, PortOwner.UdpIpv4));
    }

    [Fact]
    public void TheOwnerOfThePortIsFoundAmongTheRowsOfTheTable()
    {
        var table = Table(PortOwner.UdpIpv4, Row([0x01, 0xBB], 900), Row([0x00, 0x35], 1234), Row([0x22, 0xB8], 7));

        Assert.Equal(1234, PortOwner.OwningProcessId(table, PortOwner.UdpIpv4, Dns));
    }

    [Fact]
    public void ARowOnAnotherAddressIsNotTheHolderOfTheLoopbackPort()
    {
        // A proxy on 192.168.0.1:53 does not hold 127.0.0.1:53; the row after it does.
        var table = Table(
            PortOwner.UdpIpv4,
            Row([0x00, 0x35], 111, address: [192, 168, 0, 1]),
            Row([0x00, 0x35], 222, address: [127, 0, 0, 1]));

        Assert.Equal(222, PortOwner.OwningProcessId(table, PortOwner.UdpIpv4, Dns));
    }

    [Fact]
    public void ARowOnlyOnAnotherAddressAnswersNothing()
    {
        var table = Table(PortOwner.UdpIpv4, Row([0x00, 0x35], 111, address: [192, 168, 0, 1]));

        Assert.Null(PortOwner.OwningProcessId(table, PortOwner.UdpIpv4, Dns));
    }

    [Fact]
    public void AnotherLoopbackAddressIsNotTheOneProbed()
    {
        var table = Table(PortOwner.UdpIpv4, Row([0x00, 0x35], 111, address: [127, 0, 0, 2]));

        Assert.Null(PortOwner.OwningProcessId(table, PortOwner.UdpIpv4, Dns));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWildcardRowCoversTheLoopbackAddress(bool tcp)
    {
        var layout = tcp ? PortOwner.TcpIpv4 : PortOwner.UdpIpv4;
        var table = Table(layout, Row([0x00, 0x35], 333, layout, address: [0, 0, 0, 0]));

        Assert.Equal(333, PortOwner.OwningProcessId(table, layout, Dns));
    }

    [Fact]
    public void TheLoopbackAddressIsMatchedInTheTcpTableToo()
    {
        var layout = PortOwner.TcpIpv4;
        var table = Table(
            layout,
            Row([0x00, 0x35], 111, layout, address: [10, 0, 0, 5]),
            Row([0x00, 0x35], 444, layout, address: [127, 0, 0, 1]));

        Assert.Equal(444, PortOwner.OwningProcessId(table, layout, Dns));
    }

    [Fact]
    public void ASixColumnRowIsMatchedOnlyWhenItIsTheWildcard()
    {
        var other = new byte[16];
        other[15] = 1;
        var table = Table(
            PortOwner.UdpIpv6,
            Row([0x00, 0x35], 111, PortOwner.UdpIpv6, other),
            Row([0x00, 0x35], 555, PortOwner.UdpIpv6, new byte[16]));

        Assert.Equal(555, PortOwner.OwningProcessId(table, PortOwner.UdpIpv6, Dns));
    }

    [Fact]
    public void TheSameSearchReadsTheSixColumnTable()
    {
        var table = Table(PortOwner.UdpIpv6, Row([0x00, 0x35], 4321, PortOwner.UdpIpv6));

        Assert.Equal(4321, PortOwner.OwningProcessId(table, PortOwner.UdpIpv6, Dns));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSameSearchReadsTheTcpTables(bool sixColumn)
    {
        // One search over four layouts; where the port and owner sit is data passed in.
        var layout = sixColumn ? PortOwner.TcpIpv6 : PortOwner.TcpIpv4;
        var table = Table(layout, Row([0x01, 0xBB], 900, layout), Row([0x00, 0x35], 5678, layout));

        Assert.Equal(5678, PortOwner.OwningProcessId(table, layout, Dns));
    }

    [Fact]
    public void ATableWithNobodyOnThePortAnswersNothing()
    {
        var table = Table(PortOwner.UdpIpv4, Row([0x01, 0xBB], 900));

        Assert.Null(PortOwner.OwningProcessId(table, PortOwner.UdpIpv4, Dns));
    }

    [Fact]
    public void ATableWithNoRowsAtAllAnswersNothing()
    {
        Assert.Null(PortOwner.OwningProcessId(Table(PortOwner.UdpIpv4), PortOwner.UdpIpv4, Dns));
    }

    [Fact]
    public void ATableThatClaimsMoreRowsThanItCarriesIsNotReadPastItsEnd()
    {
        // The header count is trusted only as far as the block goes: a table that shrank between sizing and reading must not be read past.
        var table = Table(PortOwner.UdpIpv4, Row([0x01, 0xBB], 900));
        table[0] = 4;

        Assert.Null(PortOwner.OwningProcessId(table, PortOwner.UdpIpv4, Dns));
    }

    [Fact]
    public void AnEmptyBlockIsNotATable()
    {
        Assert.Null(PortOwner.OwningProcessId([], PortOwner.UdpIpv4, Dns));
        Assert.Null(PortOwner.OwningProcessId([0x01, 0x00], PortOwner.UdpIpv4, Dns));
    }

    [Fact]
    public void AHolderIsNamedByItsProcessAndAlwaysByItsId()
    {
        Assert.Equal("dnscache (process 1234)", PortOwner.Describe(1234, "dnscache"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AHolderThatEndedBeforeItCouldBeNamedIsStillReported(string? name)
    {
        // A process that went away before the question is ordinary; the id is still useful.
        Assert.Equal("process 1234", PortOwner.Describe(1234, name));
    }

    [Fact]
    public void APortOutsideTheRangeOfPortsIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PortOwner.Find(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PortOwner.Find(65536));
    }

    [Fact]
    public void TheTwoAddressFamiliesAreTheOnesWindowsNumbers()
    {
        // AF_INET6 is 23 on Windows and 10 on POSIX. Ten would never fetch the second table, so a resolver on [::]:53 would be reported as no holder.
        Assert.Equal(2u, UdpTableInterop.AF_INET);
        Assert.Equal(23u, UdpTableInterop.AF_INET6);
    }

    [Fact]
    public void TheTableAskedForIsTheOneThatCarriesTheOwnerOfEachEndpoint()
    {
        // UDP_TABLE_BASIC is 0 and its rows are eight bytes with no owner; read as twelve-byte rows the ports and ids are rubbish.
        Assert.Equal(1u, UdpTableInterop.UDP_TABLE_OWNER_PID);
    }

    [Fact]
    public void TheTcpTableAskedForIsTheOneOfListenersWithTheirOwners()
    {
        // TCP_TABLE_OWNER_PID_LISTENER. The basic listener table is 0 and has no owner; 4 and 5 list every established connection.
        Assert.Equal(3u, TcpTableInterop.TCP_TABLE_OWNER_PID_LISTENER);
    }

    [Fact]
    public void TheAnswerTheSizingCallAlwaysGivesIsNotAFailure()
    {
        // A sizing call has no block to write into, so even an empty table does not fit: 122 is what every machine answers.
        // Accepting only NO_ERROR would report "owner unknown" everywhere.
        Assert.Equal(0, UdpTableInterop.NO_ERROR);
        Assert.Equal(122, UdpTableInterop.ERROR_INSUFFICIENT_BUFFER);
    }

    [Fact]
    public void TheEndpointTableOfThisMachineIsRead()
    {
        // Asked of the machine rather than a constant: reading the UDP table changes nothing, and "unknown" here would appear in every diagnostic package.
        Assert.True(PortOwner.Find(Dns).Known, "The UDP endpoint table of this machine could not be read.");
    }

    [Fact]
    public void AnEndpointOfThisProcessIsFoundAndNamed()
    {
        // A socket this test owns and closes, on a port the machine handed out.
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var use = PortOwner.Find(((IPEndPoint)socket.LocalEndPoint!).Port);

        Assert.True(use.Known);
        Assert.Contains(Own, use.Holder ?? "nothing", StringComparison.Ordinal);
    }

    [Fact]
    public void AListenerHeldOnlyInTheTcpTableIsFoundAndNamed()
    {
        // Nothing is bound in UDP and a resolver listens on TCP: the holder must still be named, not reported as a free port.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var use = PortOwner.Find(port);

            Assert.True(use.Known);
            Assert.Contains(Own, use.Holder ?? "nothing", StringComparison.Ordinal);
        }
        finally
        {
            listener.Dispose();
        }
    }

    [RequiresIpv6]
    public void AListenerHeldOnlyInTheSixColumnTcpTableIsStillFound()
    {
        // The same in the family whose rows are laid out differently; a wrong offset reads the state or half an address as the owner.
        var listener = new TcpListener(IPAddress.IPv6Any, 0);
        listener.Start();

        try
        {
            var use = PortOwner.Find(((IPEndPoint)listener.LocalEndpoint).Port);

            Assert.True(use.Known);
            Assert.Contains(Own, use.Holder ?? "nothing", StringComparison.Ordinal);
        }
        finally
        {
            listener.Dispose();
        }
    }

    [RequiresIpv6]
    public void AnEndpointHeldOnlyInTheSixColumnTableIsStillFound()
    {
        // Bound in IPv6 only, as a resolver on [::]:53 is. Asking only the first table, or the second in the wrong family, would report the port free.
        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));

        var use = PortOwner.Find(((IPEndPoint)socket.LocalEndPoint!).Port);

        Assert.True(use.Known);
        Assert.Contains(Own, use.Holder ?? "nothing", StringComparison.Ordinal);
    }

    /// <summary>One row, laid out the way the platform lays it out.</summary>
    private static byte[] Row(byte[] port, uint owner, PortOwner.RowLayout? layout = null, byte[]? address = null)
    {
        var row = new byte[(layout ?? PortOwner.UdpIpv4).Size];
        var where = layout ?? PortOwner.UdpIpv4;

        // Left zero - the wildcard - unless the test says where the endpoint is.
        address?.CopyTo(row, where.AddressOffset);
        port.CopyTo(row, where.PortOffset);
        BitConverter.GetBytes(owner).CopyTo(row, where.OwnerOffset);

        return row;
    }

    /// <summary>The header the platform puts in front of the rows, and the rows behind it.</summary>
    private static byte[] Table(PortOwner.RowLayout layout, params byte[][] rows)
    {
        var table = new byte[PortOwner.HeaderSize + (rows.Length * layout.Size)];
        BitConverter.GetBytes((uint)rows.Length).CopyTo(table, 0);

        for (var index = 0; index < rows.Length; index++)
        {
            rows[index].CopyTo(table, PortOwner.HeaderSize + (index * layout.Size));
        }

        return table;
    }
}
