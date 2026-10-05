using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Chronos.Service.Dns;

/// <summary>Whether a query this service sends its own resolver comes back.</summary>
public interface IDnsSelfCheck
{
    /// <summary>True when the server on 127.0.0.1:<paramref name="port"/> answered. Synchronous: runs under the settings lock.</summary>
    bool Reaches(int port);
}

/// <summary>
/// Sends a header with no question over UDP. The server answers FORMERR itself and never forwards
/// it. Detects a VPN filter on port 53 dropping loopback queries.
/// </summary>
public sealed class LoopbackDnsSelfCheck : IDnsSelfCheck
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _timeout;

    public LoopbackDnsSelfCheck()
        : this(Timeout)
    {
    }

    internal LoopbackDnsSelfCheck(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        _timeout = timeout;
    }

    internal TimeSpan Wait => _timeout;

    public bool Reaches(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);

        var id = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
        var query = new byte[DnsHeader.Length];
        BinaryPrimitives.WriteUInt16BigEndian(query, id);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveTimeout = (int)Math.Ceiling(_timeout.TotalMilliseconds);

        try
        {
            // Connected, so only the server's own reply is read.
            socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
            socket.Send(query);

            var reply = new byte[DnsQuery.DefaultUdpPayloadSize];
            var length = socket.Receive(reply);

            return length >= DnsHeader.Length
                && BinaryPrimitives.ReadUInt16BigEndian(reply) == id
                && (reply[2] & DnsHeader.Qr) != 0
                && (reply[3] & DnsHeader.RcodeMask) == DnsHeader.RcodeFormatError;
        }
        catch (SocketException)
        {
            // Timed out, or nothing on the port (an ICMP unreachable reads as a reset).
            return false;
        }
    }
}
