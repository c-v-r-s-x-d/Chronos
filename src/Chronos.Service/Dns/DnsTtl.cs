using System.Buffers.Binary;

namespace Chronos.Service.Dns;

/// <summary>
/// TTL handling for the response cache: the smallest TTL, ageing every TTL by the time held, and
/// the extended RCODE that an OPT record keeps in its TTL field. All walk all three record sections.
/// </summary>
public static class DnsTtl
{
    // An OPT record keeps EXTENDED-RCODE, VERSION, DO and Z where other records keep the TTL (RFC 6891 6.1.3).
    private const ushort OptRecordType = 41;

    private const int QuestionTailLength = 4;  // QTYPE + QCLASS
    private const int RecordHeaderLength = 10; // TYPE + CLASS + TTL + RDLENGTH
    private const int TtlFieldOffset = 4;      // within the record header
    private const int TtlFieldLength = 4;

    /// <summary>The smallest TTL in a response; null when it has none or the message does not walk (uncacheable).</summary>
    public static uint? Smallest(ReadOnlySpan<byte> response)
    {
        if (!TryStartOfRecords(response, out var pos, out var records))
        {
            return null;
        }

        uint? smallest = null;

        for (var record = 0; record < records; record++)
        {
            if (!TryNextRecord(response, ref pos, out var ttlAt, out var type))
            {
                return null;
            }

            if (type == OptRecordType)
            {
                continue;
            }

            var ttl = BinaryPrimitives.ReadUInt32BigEndian(response.Slice(ttlAt, TtlFieldLength));
            if (smallest is null || ttl < smallest)
            {
                smallest = ttl;
            }
        }

        return smallest;
    }

    /// <summary>
    /// Subtracts the seconds held from every record's TTL, in place. False on a message that does
    /// not walk; octets already reached stay changed.
    /// </summary>
    public static bool TryAge(Span<byte> response, uint seconds)
    {
        if (!TryStartOfRecords(response, out var pos, out var records))
        {
            return false;
        }

        for (var record = 0; record < records; record++)
        {
            if (!TryNextRecord(response, ref pos, out var ttlAt, out var type))
            {
                return false;
            }

            if (type == OptRecordType)
            {
                continue;
            }

            var field = response.Slice(ttlAt, TtlFieldLength);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(field);

            // Zero means do not cache (RFC 1035 4.1.3) and stays zero; any other TTL bottoms out at 1.
            BinaryPrimitives.WriteUInt32BigEndian(field, ttl > seconds ? ttl - seconds : Math.Min(ttl, 1u));
        }

        return true;
    }

    /// <summary>
    /// The top eight bits of the twelve-bit RCODE, carried in the OPT record (RFC 6891 6.1.3).
    /// Zero without an OPT record or when the message does not walk.
    /// </summary>
    public static byte ExtendedRcode(ReadOnlySpan<byte> response)
    {
        if (!TryStartOfRecords(response, out var pos, out var records))
        {
            return 0;
        }

        for (var record = 0; record < records; record++)
        {
            if (!TryNextRecord(response, ref pos, out var ttlAt, out var type))
            {
                return 0;
            }

            // Sections are not told apart; there is at most one OPT, in the additional section.
            if (type == OptRecordType)
            {
                // EXTENDED-RCODE is the first of the four octets.
                return response[ttlAt];
            }
        }

        return 0;
    }

    /// <summary>Steps over the header and questions, giving where the records begin and how many the counts promise.</summary>
    private static bool TryStartOfRecords(ReadOnlySpan<byte> response, out int pos, out int records)
    {
        pos = 0;
        records = 0;

        if (response.Length < DnsHeader.Length)
        {
            return false;
        }

        var questions = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(4, 2)); // QDCOUNT
        records =
            BinaryPrimitives.ReadUInt16BigEndian(response.Slice(6, 2)) +            // ANCOUNT
            BinaryPrimitives.ReadUInt16BigEndian(response.Slice(8, 2)) +            // NSCOUNT
            BinaryPrimitives.ReadUInt16BigEndian(response.Slice(10, 2));            // ARCOUNT

        pos = DnsHeader.Length;

        // QDCOUNT questions, not one: a forwarded response is whatever the upstream sent.
        for (var question = 0; question < questions; question++)
        {
            if (!DnsName.TrySkip(response, ref pos) || pos + QuestionTailLength > response.Length)
            {
                return false;
            }

            pos += QuestionTailLength;
        }

        return true;
    }

    /// <summary>Steps over one record by its RDLENGTH, giving its TTL offset and type; unknown types are fine.</summary>
    private static bool TryNextRecord(ReadOnlySpan<byte> response, ref int pos, out int ttlAt, out ushort type)
    {
        ttlAt = 0;
        type = 0;

        if (!DnsName.TrySkip(response, ref pos) || pos + RecordHeaderLength > response.Length)
        {
            return false;
        }

        type = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(pos, 2));
        ttlAt = pos + TtlFieldOffset;

        var rdLength = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(pos + 8, 2));
        pos += RecordHeaderLength;

        if (pos + rdLength > response.Length)
        {
            return false;
        }

        pos += rdLength;
        return true;
    }
}
