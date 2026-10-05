using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;

namespace Chronos.Service.Setup;

/// <summary>
/// What holds a port: unknown, free or held. Unknown must stay distinct from free, since they
/// point the reader at different problems.
/// </summary>
public readonly record struct PortUse(bool Known, string? Holder)
{
    public static PortUse Unknown => new(Known: false, Holder: null);

    public static PortUse Free => new(Known: true, Holder: null);

    public static PortUse HeldBy(string holder) => new(Known: true, holder);
}

/// <summary>
/// Who is bound to a port, over either protocol. Port 53 is the one asked about: another resolver
/// holding it is the usual reason the DNS layer cannot start.
/// </summary>
/// <remarks>
/// Machine failures never throw; an unreadable table gives <see cref="PortUse.Unknown"/>, since this
/// runs on broken machines. A port out of range does throw, as a caller bug. All four tables (UDP
/// and TCP, IPv4 and IPv6) are asked so a TCP-only or <c>[::]:53</c> resolver is not reported free.
/// </remarks>
public static class PortOwner
{
    /// <summary>Where the row array starts: after the table's dwNumEntries, in all four tables.</summary>
    internal const int HeaderSize = 4;

    /// <summary>Row length and the port, owner and address offsets of one table. Tests pin it to the interop structs.</summary>
    internal readonly record struct RowLayout(int Size, int PortOffset, int OwnerOffset, int AddressOffset, int AddressLength);

    internal static RowLayout UdpIpv4 { get; } = new(Size: 12, PortOffset: 4, OwnerOffset: 8, AddressOffset: 0, AddressLength: 4);

    internal static RowLayout UdpIpv6 { get; } = new(Size: 28, PortOffset: 20, OwnerOffset: 24, AddressOffset: 0, AddressLength: 16);

    internal static RowLayout TcpIpv4 { get; } = new(Size: 24, PortOffset: 8, OwnerOffset: 20, AddressOffset: 4, AddressLength: 4);

    internal static RowLayout TcpIpv6 { get; } = new(Size: 56, PortOffset: 20, OwnerOffset: 52, AddressOffset: 0, AddressLength: 16);

    internal enum Endpoints
    {
        Udp,
        Tcp,
    }

    /// <summary>Every table to search, in order.</summary>
    private static readonly (Endpoints Kind, uint Family, RowLayout Layout)[] Sources =
    [
        (Endpoints.Udp, UdpTableInterop.AF_INET, UdpIpv4),
        (Endpoints.Udp, UdpTableInterop.AF_INET6, UdpIpv6),
        (Endpoints.Tcp, UdpTableInterop.AF_INET, TcpIpv4),
        (Endpoints.Tcp, UdpTableInterop.AF_INET6, TcpIpv6),
    ];

    /// <summary>Tries per table when it grows between the sizing call and the read.</summary>
    private const int Attempts = 3;

    public static PortUse Find(int port)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);

        var known = false;

        foreach (var (kind, family, layout) in Sources)
        {
            if (ReadTable(kind, family) is not { } table)
            {
                continue;
            }

            // One readable table is enough to call the port free; with IPv6 off the others do not exist.
            known = true;

            if (OwningProcessId(table, layout, port) is { } owner)
            {
                return PortUse.HeldBy(Describe(owner, NameOf(owner)));
            }
        }

        return known ? PortUse.Free : PortUse.Unknown;
    }

    /// <summary>
    /// The process id bound to <paramref name="port"/> in one table, or null. The header count is
    /// trusted only as far as the block goes.
    /// </summary>
    internal static int? OwningProcessId(ReadOnlySpan<byte> table, RowLayout layout, int port)
    {
        if (table.Length < HeaderSize)
        {
            return null;
        }

        var rows = BinaryPrimitives.ReadUInt32LittleEndian(table);

        for (var index = 0u; index < rows; index++)
        {
            var start = HeaderSize + ((long)index * layout.Size);
            if (start + layout.Size > table.Length)
            {
                break;
            }

            var row = table.Slice((int)start, layout.Size);
            if (PortOf(row, layout) != port || !Covers(row, layout))
            {
                continue;
            }

            return (int)BinaryPrimitives.ReadUInt32LittleEndian(row[layout.OwnerOffset..]);
        }

        return null;
    }

    /// <summary>Whether the row's endpoint serves 127.0.0.1: that address or the wildcard. IPv6 rows count only as [::].</summary>
    internal static bool Covers(ReadOnlySpan<byte> row, RowLayout layout)
    {
        var address = row.Slice(layout.AddressOffset, layout.AddressLength);

        return !address.ContainsAnyExcept((byte)0)
               || (layout.AddressLength == 4 && address.SequenceEqual(IPAddress.Loopback.GetAddressBytes()));
    }

    /// <summary>The low word of the field holds the port in network byte order: port 53 arrives as 0x3500.</summary>
    internal static int PortOf(ReadOnlySpan<byte> row, RowLayout layout)
    {
        var field = BinaryPrimitives.ReadUInt32LittleEndian(row[layout.PortOffset..]);

        return (int)(((field & 0x00FF) << 8) | ((field & 0xFF00) >> 8));
    }

    /// <summary>Names a holder. The id is always known; the name is not if the process has already ended.</summary>
    internal static string Describe(int processId, string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? string.Create(CultureInfo.InvariantCulture, $"process {processId}")
            : string.Create(CultureInfo.InvariantCulture, $"{name} (process {processId})");

    private static string? NameOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);

            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // The process ended since the table was read; the id stands.
            return null;
        }
    }

    /// <summary>The endpoint table of one protocol and address family as bytes, or null when it would not be read.</summary>
    private static byte[]? ReadTable(Endpoints kind, uint family)
    {
        try
        {
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                var size = 0;
                var status = Ask(kind, IntPtr.Zero, ref size, family);

                // ERROR_INSUFFICIENT_BUFFER is the normal sizing answer. NO_ERROR is accepted too,
                // but Read then finds no header, so the port ends as Unknown rather than free.
                if (status is not (UdpTableInterop.ERROR_INSUFFICIENT_BUFFER or UdpTableInterop.NO_ERROR))
                {
                    return null;
                }

                if (Read(kind, family, size) is { } table)
                {
                    return table;
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // A machine without the IP helper.
            return null;
        }
    }

    /// <summary>One read into a block of <paramref name="size"/> bytes, or null when the table grew and must be re-sized.</summary>
    private static byte[]? Read(Endpoints kind, uint family, int size)
    {
        // Smaller than a header is unreadable, and a zero-byte allocation would give a null pointer.
        if (size < HeaderSize)
        {
            return null;
        }

        var block = Marshal.AllocHGlobal(size);
        try
        {
            var length = size;
            var status = Ask(kind, block, ref length, family);

            if (status is not UdpTableInterop.NO_ERROR)
            {
                return null;
            }

            // Use the length the platform reports; it may be shorter than sized.
            var bytes = new byte[Math.Min(length, size)];
            Marshal.Copy(block, bytes, 0, bytes.Length);

            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    /// <summary>The one call that differs between the UDP and TCP tables.</summary>
    private static int Ask(Endpoints kind, IntPtr table, ref int size, uint family) => kind switch
    {
        Endpoints.Udp => UdpTableInterop.GetExtendedUdpTable(
            table, ref size, bOrder: false, family, UdpTableInterop.UDP_TABLE_OWNER_PID, reserved: 0),
        _ => TcpTableInterop.GetExtendedTcpTable(
            table, ref size, bOrder: false, family, TcpTableInterop.TCP_TABLE_OWNER_PID_LISTENER, reserved: 0),
    };
}
