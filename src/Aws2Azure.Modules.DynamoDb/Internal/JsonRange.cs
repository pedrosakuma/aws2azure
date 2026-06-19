using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// The byte range (<c>[Start, Start+Length)</c>) of a JSON value inside the
/// contiguous UTF-8 request buffer it was deserialized from. Used in place of a
/// materialized <see cref="JsonElement"/> on hot write paths so the deserializer
/// does not build (and retain) a per-request <see cref="JsonDocument"/> DOM:
/// callers recover the value's raw bytes with a single slice and either encode
/// straight from them or parse a short-lived, pooled <see cref="JsonDocument"/>
/// on demand. <c>default</c> (<c>Length == 0</c>) means the property was absent.
///
/// <para>The converter consumes the value verbatim via
/// <see cref="Utf8JsonReader.Skip"/> (no DOM, no allocation) and inherits the
/// serializer's property-binding semantics (case-insensitivity, last-wins), so
/// the captured range always addresses the value the bound property resolved
/// to.</para>
///
/// <para>The <see cref="JsonConverterAttribute"/> is on the type (not a single
/// property) so the capture also applies to <see cref="JsonRange"/> nested in
/// collections (e.g. <c>List&lt;JsonRange&gt;</c> in BatchWriteItem), where there
/// is no property to annotate.</para>
/// </summary>
[JsonConverter(typeof(JsonRangeConverter))]
internal readonly record struct JsonRange(int Start, int Length)
{
    /// <summary>Whether the property was present in the source buffer.</summary>
    public bool IsPresent => Length > 0;
}

/// <summary>
/// Records the byte range of a JSON value instead of materializing it. Valid
/// <b>only</b> when the enclosing object is deserialized from a single
/// contiguous in-memory buffer (the <see cref="ReadOnlySpan{Byte}"/> /
/// <c>byte[]</c> <see cref="JsonSerializer.Deserialize{T}(ReadOnlySpan{byte}, JsonTypeInfo{T})"/>
/// overloads), where <see cref="Utf8JsonReader.TokenStartIndex"/> and
/// <see cref="Utf8JsonReader.BytesConsumed"/> are absolute offsets into that
/// buffer. Reading from a <see cref="System.IO.Stream"/>/<c>PipeReader</c>
/// re-buffers into segments the caller does not own, so the captured offsets
/// would not address the caller's buffer — do not use this converter there.
/// </summary>
internal sealed class JsonRangeConverter : JsonConverter<JsonRange>
{
    public override JsonRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var start = checked((int)reader.TokenStartIndex);
        reader.Skip();
        var length = checked((int)reader.BytesConsumed) - start;
        return new JsonRange(start, length);
    }

    public override void Write(Utf8JsonWriter writer, JsonRange value, JsonSerializerOptions options)
        => throw new NotSupportedException("JsonRange is a read-only request-parse projection and is never serialized.");
}
