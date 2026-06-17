using System.Text;
using System.Text.Json;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// A constant property name / string value that has been pre-encoded for both
/// writer back-ends: the JSON-text path consumes <see cref="Encoded"/> (escaped,
/// allocation-free), and the CosmosBinary path consumes <see cref="Utf8Raw"/>
/// (verbatim UTF-8, unescaped — binary string tokens are not JSON-escaped).
/// </summary>
internal readonly struct TokenName
{
    public TokenName(string value)
    {
        Encoded = JsonEncodedText.Encode(value);
        Utf8Raw = Encoding.UTF8.GetBytes(value);
    }

    /// <summary>Pre-escaped JSON text, for the <see cref="Utf8JsonTokenWriter"/>.</summary>
    public JsonEncodedText Encoded { get; }

    /// <summary>Verbatim UTF-8 bytes, for the <see cref="CosmosBinaryWriter"/>.</summary>
    public byte[] Utf8Raw { get; }
}

/// <summary>
/// Minimal structural JSON writer surface shared by the two DDB→Cosmos write
/// encoders so a single token walk (see
/// <see cref="Aws2Azure.Modules.DynamoDb.Persistence.InferredAttributeStorage"/>)
/// can emit either canonical JSON <b>text</b>
/// (<see cref="Utf8JsonTokenWriter"/>) or the Cosmos <b>binary</b> JSON format
/// (<see cref="CosmosBinaryWriter"/>) without duplicating the walk. The methods
/// mirror the subset of <see cref="Utf8JsonWriter"/> the encoder needs; numbers
/// arrive as their canonical decimal text via <see cref="WriteNumberRaw"/>.
/// </summary>
internal interface ITokenWriter
{
    void WriteStartObject();

    void WriteEndObject();

    void WriteStartArray();

    void WriteEndArray();

    void WritePropertyName(in TokenName name);

    void WritePropertyName(string name);

    /// <summary>Writes a property name from its <b>unescaped</b> UTF-8 bytes —
    /// the single-pass wire path (#342) hands the reader's value span straight
    /// through. The text back-end re-escapes per its encoder; the binary
    /// back-end writes the bytes verbatim.</summary>
    void WritePropertyName(ReadOnlySpan<byte> utf8Unescaped);

    void WriteString(in TokenName name, string? value);

    void WriteString(in TokenName name, in TokenName value);

    void WriteStringValue(string? value);

    /// <summary>Writes a string value from its <b>unescaped</b> UTF-8 bytes —
    /// the single-pass wire path (#342) hands the reader's value span straight
    /// through. The text back-end re-escapes per its encoder; the binary
    /// back-end writes the bytes verbatim.</summary>
    void WriteStringValue(ReadOnlySpan<byte> utf8Unescaped);

    void WriteBooleanValue(bool value);

    void WriteNullValue();

    /// <summary>Writes a bare JSON number from its canonical decimal text (no
    /// exponent, no superfluous zeros). The text back-end emits the digits
    /// verbatim; the binary back-end re-encodes them as the narrowest exact
    /// numeric marker (int32 / int64 / double).</summary>
    void WriteNumberRaw(string canonicalDecimal);

    /// <summary>Writes a bare JSON number from its canonical decimal <b>UTF-8</b>
    /// bytes (no exponent, no superfluous zeros) — the single-pass wire path
    /// (#429) canonicalizes straight into a stack buffer and hands the bytes
    /// through with zero string materialization. Behaviour matches
    /// <see cref="WriteNumberRaw(string)"/> byte-for-byte.</summary>
    void WriteNumberRaw(ReadOnlySpan<byte> canonicalUtf8);

    void Flush();
}
