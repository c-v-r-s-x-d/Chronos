namespace Chronos.Service.Dns;

/// <summary>
/// The twelve-octet DNS message header (RFC 1035 4.1.1): length, flag bits and response codes,
/// shared by the codec, the parser and the reply builder.
/// </summary>
internal static class DnsHeader
{
    public const int Length = 12;

    // First flag octet: QR, OPCODE (four bits), AA, TC, RD.
    public const byte Qr = 0x80;
    public const byte OpcodeMask = 0x78;
    public const byte OpcodeQuery = 0x00;
    public const byte AuthoritativeAnswer = 0x04;
    public const byte Truncated = 0x02;
    public const byte RecursionDesired = 0x01;

    // Second flag octet: RA, three reserved bits, RCODE (four bits).
    public const byte RecursionAvailable = 0x80;
    public const byte RcodeMask = 0x0F;

    public const byte RcodeNoError = 0;
    public const byte RcodeFormatError = 1;
    public const byte RcodeServerFailure = 2;
    public const byte RcodeNameError = 3;
    public const byte RcodeNotImplemented = 4;
}
