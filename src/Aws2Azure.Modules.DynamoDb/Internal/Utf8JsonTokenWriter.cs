using System.Text.Json;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// <see cref="ITokenWriter"/> over a <see cref="Utf8JsonWriter"/> — the
/// canonical JSON-<b>text</b> back-end and the existing fast path. A
/// <c>readonly struct</c> wrapping the writer reference so the shared token
/// walk devirtualizes through the generic constraint with no allocation and
/// no behavioural change (output stays byte-identical to direct
/// <see cref="Utf8JsonWriter"/> calls).
/// </summary>
internal readonly struct Utf8JsonTokenWriter(Utf8JsonWriter writer) : ITokenWriter
{
    private readonly Utf8JsonWriter _writer = writer;

    public void WriteStartObject() => _writer.WriteStartObject();

    public void WriteEndObject() => _writer.WriteEndObject();

    public void WriteStartArray() => _writer.WriteStartArray();

    public void WriteEndArray() => _writer.WriteEndArray();

    public void WritePropertyName(in TokenName name) => _writer.WritePropertyName(name.Encoded);

    public void WritePropertyName(string name) => _writer.WritePropertyName(name);

    public void WritePropertyName(ReadOnlySpan<byte> utf8Unescaped) => _writer.WritePropertyName(utf8Unescaped);

    public void WriteString(in TokenName name, string? value) => _writer.WriteString(name.Encoded, value);

    public void WriteString(in TokenName name, in TokenName value) => _writer.WriteString(name.Encoded, value.Encoded);

    public void WriteStringValue(string? value) => _writer.WriteStringValue(value);

    public void WriteStringValue(ReadOnlySpan<byte> utf8Unescaped) => _writer.WriteStringValue(utf8Unescaped);

    public void WriteBooleanValue(bool value) => _writer.WriteBooleanValue(value);

    public void WriteNullValue() => _writer.WriteNullValue();

    public void WriteNumberRaw(string canonicalDecimal) => _writer.WriteRawValue(canonicalDecimal, skipInputValidation: false);

    public void Flush() => _writer.Flush();
}
