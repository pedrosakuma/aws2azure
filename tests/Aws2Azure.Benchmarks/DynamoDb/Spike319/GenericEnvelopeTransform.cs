using System.Buffers;
using System.Text.Json;

namespace Aws2Azure.Benchmarks.DynamoDb.Spike319;

/// <summary>
/// SPIKE (#319) — the DynamoDB GetItem envelope transform, written ONCE and
/// generic over <see cref="ITokenReader"/>. This is a faithful port of the
/// production <c>InferredAttributeStorage.WriteGetItemEnvelope(Utf8JsonWriter,
/// ReadOnlySpan&lt;byte&gt;)</c> with the only change being
/// <c>ref Utf8JsonReader</c> → <c>scoped ref TReader</c>.
///
/// <para>It drives byte-identical output whether <c>TReader</c> is
/// <see cref="Utf8JsonTokenReader"/> (JSON text) or <see cref="CosmosBinaryReader"/>
/// (binary). The point of the spike: prove the transform need not be duplicated
/// to serve the binary path, and that the abstraction is cheap under the
/// <c>allows ref struct</c> monomorphization.</para>
/// </summary>
internal static class GenericEnvelopeTransform
{
    private static readonly JsonEncodedText ItemPropEncoded = JsonEncodedText.Encode("Item");
    private static readonly JsonEncodedText IdPropEncoded = JsonEncodedText.Encode("id");
    private static readonly JsonEncodedText TagS = JsonEncodedText.Encode("S");
    private static readonly JsonEncodedText TagN = JsonEncodedText.Encode("N");
    private static readonly JsonEncodedText TagBool = JsonEncodedText.Encode("BOOL");
    private static readonly JsonEncodedText TagNull = JsonEncodedText.Encode("NULL");
    private static readonly JsonEncodedText TagM = JsonEncodedText.Encode("M");
    private static readonly JsonEncodedText TagL = JsonEncodedText.Encode("L");
    private static readonly JsonEncodedText TagB = JsonEncodedText.Encode("B");
    private static readonly JsonEncodedText TagSS = JsonEncodedText.Encode("SS");
    private static readonly JsonEncodedText TagNS = JsonEncodedText.Encode("NS");
    private static readonly JsonEncodedText TagBS = JsonEncodedText.Encode("BS");

    private static ReadOnlySpan<byte> ShadowEncodedIdNameU8 => "_a2a$id"u8;
    private static ReadOnlySpan<byte> IdNameU8 => "id"u8;
    private static ReadOnlySpan<byte> DiscriminatorPrefixU8 => "_a2a"u8;
    private static ReadOnlySpan<byte> EnvelopeTagPrefixU8 => "_a2a:"u8;
    private static ReadOnlySpan<byte> EnvelopeTagNU8 => "_a2a:N"u8;
    private static ReadOnlySpan<byte> EnvelopeTagBU8 => "_a2a:B"u8;
    private static ReadOnlySpan<byte> EnvelopeTagSSU8 => "_a2a:SS"u8;
    private static ReadOnlySpan<byte> EnvelopeTagNSU8 => "_a2a:NS"u8;
    private static ReadOnlySpan<byte> EnvelopeTagBSU8 => "_a2a:BS"u8;

    public static void WriteGetItemEnvelope<TReader>(Utf8JsonWriter writer, scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        writer.WriteStartObject();
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            writer.WriteEndObject();
            return;
        }

        writer.WritePropertyName(ItemPropEncoded);
        writer.WriteStartObject();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals(ShadowEncodedIdNameU8))
            {
                writer.WritePropertyName(IdPropEncoded);
                reader.Read();
                WriteAttributeValue(writer, ref reader);
            }
            else if (IsReservedTopLevelNameToken(ref reader) || IsCosmosSystemFieldToken(ref reader))
            {
                reader.Read();
                reader.Skip();
            }
            else
            {
                WriteCurrentPropertyName(writer, ref reader);
                reader.Read();
                WriteAttributeValue(writer, ref reader);
            }
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static bool IsReservedTopLevelNameToken<TReader>(ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        if (reader.ValueTextEquals(IdNameU8)) return true;
        if (reader.ValueIsEscaped || reader.HasValueSequence) return false;
        return reader.ValueSpan.StartsWith(DiscriminatorPrefixU8);
    }

    private static bool IsCosmosSystemFieldToken<TReader>(ref TReader reader)
        where TReader : ITokenReader, allows ref struct
        => reader.ValueTextEquals("_rid"u8)
        || reader.ValueTextEquals("_self"u8)
        || reader.ValueTextEquals("_etag"u8)
        || reader.ValueTextEquals("_ts"u8)
        || reader.ValueTextEquals("_attachments"u8)
        || reader.ValueTextEquals("_lsn"u8)
        || reader.ValueTextEquals("_metadata"u8);

    private static void WriteAttributeValue<TReader>(Utf8JsonWriter writer, scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                writer.WriteStartObject();
                writer.WritePropertyName(TagS);
                WriteStringTokenValue(writer, ref reader);
                writer.WriteEndObject();
                break;

            case JsonTokenType.Number:
                writer.WriteStartObject();
                writer.WritePropertyName(TagN);
                WriteNumberTokenAsString(writer, ref reader);
                writer.WriteEndObject();
                break;

            case JsonTokenType.True:
            case JsonTokenType.False:
                writer.WriteStartObject();
                writer.WriteBoolean(TagBool, reader.TokenType == JsonTokenType.True);
                writer.WriteEndObject();
                break;

            case JsonTokenType.Null:
                writer.WriteStartObject();
                writer.WriteBoolean(TagNull, true);
                writer.WriteEndObject();
                break;

            case JsonTokenType.StartArray:
                writer.WriteStartObject();
                writer.WritePropertyName(TagL);
                writer.WriteStartArray();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    WriteAttributeValue(writer, ref reader);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;

            case JsonTokenType.StartObject:
                WriteObjectAsAttributeValue(writer, ref reader);
                break;

            default:
                throw new InvalidOperationException($"Cannot decode Cosmos token {reader.TokenType}.");
        }
    }

    private static void WriteObjectAsAttributeValue<TReader>(Utf8JsonWriter writer, scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        if (!reader.Read())
            throw new InvalidOperationException("Truncated Cosmos object.");

        if (reader.TokenType == JsonTokenType.EndObject)
        {
            writer.WriteStartObject();
            writer.WritePropertyName(TagM);
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            return;
        }

        if (!reader.ValueIsEscaped && !reader.HasValueSequence &&
            reader.ValueSpan.StartsWith(EnvelopeTagPrefixU8))
        {
            if (reader.ValueTextEquals(EnvelopeTagNU8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.String)
                    throw new InvalidOperationException("'_a2a:N' envelope payload must be a string.");
                writer.WriteStartObject();
                writer.WritePropertyName(TagN);
                WriteStringTokenValue(writer, ref reader);
                writer.WriteEndObject();
                reader.Read();
                return;
            }
            if (reader.ValueTextEquals(EnvelopeTagBU8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.String)
                    throw new InvalidOperationException("'_a2a:B' envelope payload must be a string.");
                writer.WriteStartObject();
                writer.WritePropertyName(TagB);
                WriteStringTokenValue(writer, ref reader);
                writer.WriteEndObject();
                reader.Read();
                return;
            }
            if (reader.ValueTextEquals(EnvelopeTagSSU8)) { WriteUnwrappedSet(writer, TagSS, ref reader); reader.Read(); return; }
            if (reader.ValueTextEquals(EnvelopeTagNSU8)) { WriteUnwrappedSet(writer, TagNS, ref reader); reader.Read(); return; }
            if (reader.ValueTextEquals(EnvelopeTagBSU8)) { WriteUnwrappedSet(writer, TagBS, ref reader); reader.Read(); return; }

            throw new InvalidOperationException("Unknown '_a2a:' envelope tag in Cosmos document.");
        }

        writer.WriteStartObject();
        writer.WritePropertyName(TagM);
        writer.WriteStartObject();
        while (true)
        {
            WriteCurrentPropertyName(writer, ref reader);
            reader.Read();
            WriteAttributeValue(writer, ref reader);
            if (!reader.Read() || reader.TokenType == JsonTokenType.EndObject)
                break;
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteUnwrappedSet<TReader>(Utf8JsonWriter writer, JsonEncodedText tag, scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new InvalidOperationException($"Envelope {tag} payload must be a JSON array.");

        writer.WriteStartObject();
        writer.WritePropertyName(tag);
        writer.WriteStartArray();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new InvalidOperationException("Set members must be JSON strings.");
            WriteStringTokenValue(writer, ref reader);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteStringTokenValue<TReader>(Utf8JsonWriter writer, scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        if (!reader.ValueIsEscaped && !reader.HasValueSequence)
        {
            writer.WriteStringValue(reader.ValueSpan);
            return;
        }

        int max = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
        byte[]? rented = max > 256 ? ArrayPool<byte>.Shared.Rent(max) : null;
        Span<byte> buf = rented ?? stackalloc byte[256];
        int written = reader.CopyString(buf);
        writer.WriteStringValue(buf[..written]);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }

    private static void WriteCurrentPropertyName<TReader>(Utf8JsonWriter writer, scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        if (!reader.ValueIsEscaped && !reader.HasValueSequence)
        {
            writer.WritePropertyName(reader.ValueSpan);
            return;
        }

        int max = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
        byte[]? rented = max > 256 ? ArrayPool<byte>.Shared.Rent(max) : null;
        Span<byte> buf = rented ?? stackalloc byte[256];
        int written = reader.CopyString(buf);
        writer.WritePropertyName(buf[..written]);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }

    private static void WriteNumberTokenAsString<TReader>(Utf8JsonWriter writer, scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        if (!reader.HasValueSequence)
        {
            writer.WriteStringValue(reader.ValueSpan);
            return;
        }
        int len = checked((int)reader.ValueSequence.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(len);
        reader.ValueSequence.CopyTo(rented);
        writer.WriteStringValue(rented.AsSpan(0, len));
        ArrayPool<byte>.Shared.Return(rented);
    }
}
