namespace Aws2Azure.Amqp.Codec;

/// <summary>
/// AMQP 1.0 primitive format codes (OASIS AMQP 1.0 §1.6.1).
/// </summary>
/// <remarks>
/// Codes are split into categories by the high nibble:
/// <list type="bullet">
/// <item><description>0x4_ — fixed-width, 0 bytes of data (null, true, false, zero variants).</description></item>
/// <item><description>0x5_ — fixed-width, 1 byte.</description></item>
/// <item><description>0x6_ — fixed-width, 2 bytes.</description></item>
/// <item><description>0x7_ — fixed-width, 4 bytes.</description></item>
/// <item><description>0x8_ — fixed-width, 8 bytes.</description></item>
/// <item><description>0x9_ — fixed-width, 16 bytes.</description></item>
/// <item><description>0xA_ — variable-width with a 1-byte size prefix.</description></item>
/// <item><description>0xB_ — variable-width with a 4-byte size prefix.</description></item>
/// <item><description>0xC_ — compound with 1-byte size + count.</description></item>
/// <item><description>0xD_ — compound with 4-byte size + count.</description></item>
/// <item><description>0xE_ — array with 1-byte size + count + element constructor.</description></item>
/// <item><description>0xF_ — array with 4-byte size + count + element constructor.</description></item>
/// </list>
/// Only the codes used by the SQS-over-Service-Bus profile are listed here;
/// the registry is intentionally closed (see ADR-0002 §AOT constraints).
/// </remarks>
internal static class AmqpFormatCode
{
    // 0x00 — described type constructor (descriptor + value follow).
    public const byte Described = 0x00;

    // 0x40 family — empty payload.
    public const byte Null = 0x40;
    public const byte BooleanTrue = 0x41;
    public const byte BooleanFalse = 0x42;
    public const byte UInt0 = 0x43;
    public const byte ULong0 = 0x44;
    public const byte List0 = 0x45;

    // 0x50 family — 1 byte payload.
    public const byte UByte = 0x50;
    public const byte Byte = 0x51;
    public const byte UIntSmall = 0x52;
    public const byte ULongSmall = 0x53;
    public const byte Boolean = 0x56;

    // 0x60 family — 2 bytes.
    public const byte UShort = 0x60;
    public const byte Short = 0x61;

    // 0x70 family — 4 bytes.
    public const byte UInt = 0x70;
    public const byte Int = 0x71;
    public const byte Float = 0x72;
    public const byte CharUtf32 = 0x73;

    // 0x80 family — 8 bytes.
    public const byte ULong = 0x80;
    public const byte Long = 0x81;
    public const byte Double = 0x82;
    public const byte TimestampMs = 0x83;

    // 0x90 family — 16 bytes.
    public const byte Uuid = 0x98;

    // 0xA0 — variable-width / 1-byte length prefix.
    public const byte Binary8 = 0xA0;
    public const byte String8Utf8 = 0xA1;
    public const byte Symbol8 = 0xA3;

    // 0xB0 — variable-width / 4-byte length prefix.
    public const byte Binary32 = 0xB0;
    public const byte String32Utf8 = 0xB1;
    public const byte Symbol32 = 0xB3;

    // 0xC0 — compound, 1-byte size + count.
    public const byte List8 = 0xC0;
    public const byte Map8 = 0xC1;

    // 0xD0 — compound, 4-byte size + count.
    public const byte List32 = 0xD0;
    public const byte Map32 = 0xD1;

    // 0xE0 — array, 1-byte size + count.
    public const byte Array8 = 0xE0;

    // 0xF0 — array, 4-byte size + count.
    public const byte Array32 = 0xF0;
}
