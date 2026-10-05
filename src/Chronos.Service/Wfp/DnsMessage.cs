using System.Buffers.Binary;
using System.Net;
using Chronos.Service.Dns;

namespace Chronos.Service.Wfp;

/// <summary>Record type this codec knows how to query and parse.</summary>
public enum DnsRecordType : ushort
{
    A = 1,
    Aaaa = 28,
}

/// <summary>Outcome of parsing one DNS response.</summary>
public enum DnsParseStatus
{
    Ok,
    Malformed,
    IdMismatch,
    NotAnAnswer,
    ServerFailure,
    NameNotFound,
}

/// <summary>
/// What <see cref="DnsMessage.ParseResponse"/> found. <see cref="Truncated"/> is read from the TC
/// bit independently of <see cref="Status"/>: a resolver retrying over TCP needs to see it even on
/// an otherwise-successful parse.
/// </summary>
public readonly record struct DnsParseResult(DnsParseStatus Status, bool Truncated, IReadOnlyList<IPAddress> Addresses);

/// <summary>
/// Builds DNS queries and parses responses from wire bytes, for L3's pre-resolver. L2 reads client
/// queries with <see cref="DnsQuery"/>; both share <see cref="DnsName"/> and <see cref="DnsHeader"/>.
///
/// Not supported: DNSSEC, client-side EDNS(0), and any record type except <c>A</c>/<c>AAAA</c>.
/// <c>CNAME</c> records are skipped; addresses are collected whatever owner name they carry.
/// </summary>
public static class DnsMessage
{
    private const int HeaderLength = DnsHeader.Length;

    /// <summary>Encodes a standard query: RD set, one question, nothing else.</summary>
    public static byte[] BuildQuery(string name, DnsRecordType type, ushort id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var encodedName = DnsName.Encode(name);

        var query = new byte[HeaderLength + encodedName.Length + 4];
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(0, 2), id);
        query[2] = DnsHeader.RecursionDesired;
        query[3] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), 1); // QDCOUNT
        // ANCOUNT, NSCOUNT, ARCOUNT are already zero.

        encodedName.CopyTo(query.AsSpan(HeaderLength));
        var afterName = HeaderLength + encodedName.Length;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(afterName, 2), (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(afterName + 2, 2), 1); // QCLASS IN

        return query;
    }

    /// <summary>
    /// Parses a response to the query sent with <paramref name="expectedId"/>. Never throws; every
    /// failure is a <see cref="DnsParseStatus"/>, since the reply is untrusted.
    /// </summary>
    public static DnsParseResult ParseResponse(ReadOnlySpan<byte> response, ushort expectedId)
    {
        if (response.Length < HeaderLength)
        {
            return new DnsParseResult(DnsParseStatus.Malformed, false, []);
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(response[..2]);
        if (id != expectedId)
        {
            return new DnsParseResult(DnsParseStatus.IdMismatch, false, []);
        }

        var flags1 = response[2];
        var flags2 = response[3];
        var isResponse = (flags1 & DnsHeader.Qr) != 0;
        var truncated = (flags1 & DnsHeader.Truncated) != 0;
        var rcode = flags2 & DnsHeader.RcodeMask;

        if (!isResponse)
        {
            return new DnsParseResult(DnsParseStatus.NotAnAnswer, truncated, []);
        }

        if (rcode == DnsHeader.RcodeServerFailure)
        {
            return new DnsParseResult(DnsParseStatus.ServerFailure, truncated, []);
        }

        if (rcode == DnsHeader.RcodeNameError)
        {
            return new DnsParseResult(DnsParseStatus.NameNotFound, truncated, []);
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(4, 2));
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(6, 2));

        var pos = HeaderLength;

        for (var i = 0; i < questionCount; i++)
        {
            if (!DnsName.TrySkip(response, ref pos) || pos + 4 > response.Length)
            {
                return new DnsParseResult(DnsParseStatus.Malformed, truncated, []);
            }

            pos += 4; // QTYPE + QCLASS
        }

        var addresses = new List<IPAddress>();

        for (var i = 0; i < answerCount; i++)
        {
            if (!DnsName.TrySkip(response, ref pos) || pos + 10 > response.Length)
            {
                return new DnsParseResult(DnsParseStatus.Malformed, truncated, []);
            }

            var recordType = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(pos, 2));
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(pos + 8, 2));
            pos += 10; // TYPE + CLASS + TTL + RDLENGTH

            if (pos + rdLength > response.Length)
            {
                return new DnsParseResult(DnsParseStatus.Malformed, truncated, []);
            }

            if (recordType == (ushort)DnsRecordType.A)
            {
                // A RDATA is exactly 4 octets (RFC 1035); anything else is malformed.
                if (rdLength != 4)
                {
                    return new DnsParseResult(DnsParseStatus.Malformed, truncated, []);
                }

                addresses.Add(new IPAddress(response.Slice(pos, 4)));
            }
            else if (recordType == (ushort)DnsRecordType.Aaaa)
            {
                // AAAA RDATA is exactly 16 octets (RFC 3596).
                if (rdLength != 16)
                {
                    return new DnsParseResult(DnsParseStatus.Malformed, truncated, []);
                }

                addresses.Add(new IPAddress(response.Slice(pos, 16)));
            }
            // Other types (CNAME included) are skipped by RDLENGTH without judging their RDATA.

            pos += rdLength;
        }

        return new DnsParseResult(DnsParseStatus.Ok, truncated, addresses);
    }
}
