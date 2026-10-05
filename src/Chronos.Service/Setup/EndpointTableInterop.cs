using System.Runtime.InteropServices;

namespace Chronos.Service.Setup;

/// <summary>
/// The IP helper's UDP endpoint table. Field names match the Windows headers.
/// </summary>
/// <remarks>
/// The row structs are never marshalled with <see cref="Marshal.PtrToStructure"/>; the table is
/// copied into bytes and read at offsets pinned against these declarations.
/// </remarks>
internal static class UdpTableInterop
{
    /// <summary>AF_INET and AF_INET6, the two address families a UDP endpoint can be bound in.</summary>
    public const uint AF_INET = 2;

    /// <summary>Windows numbers AF_INET6 as 23; POSIX headers say 10, which would miss a resolver bound to <c>[::]:53</c>.</summary>
    public const uint AF_INET6 = 23;

    /// <summary>UDP_TABLE_OWNER_PID: the table class that carries the owning process.</summary>
    public const uint UDP_TABLE_OWNER_PID = 1;

    public const int NO_ERROR = 0;

    /// <summary>ERROR_INSUFFICIENT_BUFFER: the sizing call's answer, and the second call's if the table grew.</summary>
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>MIB_UDPROW_OWNER_PID: 12 bytes; rows start one DWORD in, after dwNumEntries.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_UDPROW_OWNER_PID
    {
        public uint dwLocalAddr;

        /// <summary>The port in network byte order, in the low word. Never read as it stands.</summary>
        public uint dwLocalPort;

        public uint dwOwningPid;
    }

    /// <summary>MIB_UDP6ROW_OWNER_PID: a 16-byte address and a scope id, so port and owner sit further along.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_UDP6ROW_OWNER_PID
    {
        /// <summary>ucLocalAddr[16], declared as the array it is so the size of the row is right.</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucLocalAddr;

        public uint dwLocalScopeId;

        public uint dwLocalPort;

        public uint dwOwningPid;
    }

    /// <summary>Fills a caller-allocated block with the table, or reports the size needed. bOrder stays false: sorting is wasted work.</summary>
    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern int GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        uint ulAf,
        uint tableClass,
        uint reserved);
}

/// <summary>
/// The TCP endpoint table. A resolver listening only on TCP/53 would look free in the UDP table.
/// Address families are <see cref="UdpTableInterop.AF_INET"/> and <see cref="UdpTableInterop.AF_INET6"/>.
/// </summary>
internal static class TcpTableInterop
{
    /// <summary>TCP_TABLE_OWNER_PID_LISTENER: listening sockets with owners, without every established connection.</summary>
    public const uint TCP_TABLE_OWNER_PID_LISTENER = 3;

    /// <summary>MIB_TCPROW_OWNER_PID: six DWORDs, 24 bytes; the local port is the third.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;

        public uint dwLocalAddr;

        /// <summary>The port in network byte order, in the low word. Never read as it stands.</summary>
        public uint dwLocalPort;

        public uint dwRemoteAddr;

        public uint dwRemotePort;

        public uint dwOwningPid;
    }

    /// <summary>MIB_TCP6ROW_OWNER_PID: 56 bytes; the state is near the end and the owner is the last DWORD.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCP6ROW_OWNER_PID
    {
        /// <summary>ucLocalAddr[16], declared as the array it is so the size of the row is right.</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucLocalAddr;

        public uint dwLocalScopeId;

        public uint dwLocalPort;

        /// <summary>ucRemoteAddr[16], declared as an array so the row size is right.</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucRemoteAddr;

        public uint dwRemoteScopeId;

        public uint dwRemotePort;

        public uint dwState;

        public uint dwOwningPid;
    }

    /// <summary>The TCP counterpart of <see cref="UdpTableInterop.GetExtendedUdpTable"/>: size, then read.</summary>
    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern int GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        uint ulAf,
        uint tableClass,
        uint reserved);
}
