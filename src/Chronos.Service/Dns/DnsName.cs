using System.Globalization;
using System.Text;

namespace Chronos.Service.Dns;

/// <summary>
/// Reads and writes domain names in DNS wire format, compression pointers included. The one codec
/// for every layer that touches the wire.
/// </summary>
public static class DnsName
{
    public const int MaxLabelLength = 63;
    public const int MaxWireLength = 255;

    private const int MaxPointerHops = 128;

    /// <summary>Advances past one encoded name. False on anything malformed or hostile.</summary>
    public static bool TrySkip(ReadOnlySpan<byte> message, ref int pos) => TryWalk(message, ref pos, null, out _);

    /// <summary>
    /// The same walk, keeping the labels: lower-cased and dot-joined, the root as an empty string.
    /// Bytes above ASCII are widened as Latin-1, so the result is for matching and logging, not a
    /// round trip. On false, <paramref name="name"/> is empty and <paramref name="pos"/> is untouched.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> message, ref int pos, out string name) =>
        TryRead(message, ref pos, out name, out _);

    /// <summary>
    /// The same read, also reporting whether a compression pointer was used. A question name may not
    /// be compressed: nothing precedes it to point at (RFC 1035 4.1.4).
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> message, ref int pos, out string name, out bool compressed)
    {
        var labels = new StringBuilder();

        if (!TryWalk(message, ref pos, labels, out compressed))
        {
            name = string.Empty;
            return false;
        }

        name = labels.ToString();
        return true;
    }

    /// <summary>
    /// Encodes a name, punycode included. Throws <see cref="ArgumentException"/> on a name that
    /// cannot go on the wire.
    /// </summary>
    public static byte[] Encode(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var trimmed = name.EndsWith('.') ? name[..^1] : name;

        // Punycode, not Encoding.ASCII, which turns a non-ASCII name into '?' without an exception.
        // GetAscii throws ArgumentException, the same type as ValidateWireLength.
        string ascii;
        try
        {
            // One instance per call: thread safety is documented for IdnMapping's static members only,
            // and the UDP and TCP listeners run in parallel.
            ascii = new IdnMapping().GetAscii(trimmed);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"'{name}' is not a domain name that can be queried.", nameof(name), exception);
        }

        var wireLength = ValidateWireLength(ascii, nameof(name));

        var buffer = new byte[wireLength];
        var offset = 0;
        foreach (var label in ascii.Split('.'))
        {
            buffer[offset] = (byte)label.Length;
            offset += 1;
            offset += Encoding.ASCII.GetBytes(label, buffer.AsSpan(offset));
        }

        buffer[offset] = 0;
        return buffer;
    }

    /// <summary>
    /// The wire length an already-ASCII name will occupy, root terminator included, or
    /// <see cref="ArgumentException"/> if it cannot be legal. Backs up <see cref="IdnMapping"/>,
    /// whose own length checks are not a documented contract.
    /// </summary>
    internal static int ValidateWireLength(string ascii, string paramName)
    {
        var wireLength = 1; // root terminator, counted toward 255 (RFC 1035 2.3.4)
        foreach (var label in ascii.Split('.'))
        {
            if (label.Length == 0 || label.Length > MaxLabelLength)
            {
                throw new ArgumentException($"Label '{label}' must be 1-{MaxLabelLength} bytes.", paramName);
            }

            wireLength += 1 + label.Length;
        }

        if (wireLength > MaxWireLength)
        {
            throw new ArgumentException(
                $"'{ascii}' encodes to {wireLength} bytes; the wire limit is {MaxWireLength}.", paramName);
        }

        return wireLength;
    }

    /// <summary>
    /// Advances <paramref name="pos"/> past one encoded name, following compression pointers and
    /// appending labels when asked. Loops are stopped twice: a hop cap, and pointers must point
    /// strictly backward. <paramref name="compressed"/> is set when any pointer is met, even a refused one.
    /// </summary>
    private static bool TryWalk(ReadOnlySpan<byte> message, ref int pos, StringBuilder? labels, out bool compressed)
    {
        var readPos = pos;
        var afterName = -1;
        var hops = 0;

        compressed = false;

        // Starts at 1 for the root octet, matching Encode.
        var nameLength = 1;

        while (true)
        {
            if (readPos >= message.Length)
            {
                return false;
            }

            var lengthByte = message[readPos];

            if ((lengthByte & 0xC0) == 0xC0)
            {
                compressed = true;

                if (readPos + 1 >= message.Length)
                {
                    return false;
                }

                var pointer = ((lengthByte & 0x3F) << 8) | message[readPos + 1];

                if (afterName < 0)
                {
                    afterName = readPos + 2;
                }

                if (pointer >= readPos)
                {
                    return false; // must point strictly backward: rules out loops and forward jumps
                }

                hops++;
                if (hops > MaxPointerHops)
                {
                    return false;
                }

                readPos = pointer;
                continue;
            }

            // Top bits 00 are a length, 11 a pointer (above); 0x40 and 0x80 are reserved. One
            // comparison rejects both a reserved type and an over-long label.
            if (lengthByte > MaxLabelLength)
            {
                return false;
            }

            if (lengthByte == 0)
            {
                readPos += 1;
                if (afterName < 0)
                {
                    afterName = readPos;
                }

                break;
            }

            var labelLength = lengthByte;
            nameLength += 1 + labelLength;
            if (nameLength > MaxWireLength)
            {
                return false;
            }

            if (readPos + 1 + labelLength > message.Length)
            {
                return false;
            }

            if (labels is not null)
            {
                if (labels.Length > 0)
                {
                    labels.Append('.');
                }

                foreach (var b in message.Slice(readPos + 1, labelLength))
                {
                    // ASCII-only case folding (RFC 4343); other bytes are widened as Latin-1.
                    labels.Append((char)(b is >= (byte)'A' and <= (byte)'Z' ? b + 0x20 : b));
                }
            }

            readPos += 1 + labelLength;
        }

        pos = afterName;
        return true;
    }
}
