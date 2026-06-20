using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// One item in a <c>BatchGetItem</c> <c>Responses</c> list, carrying <b>either</b>
/// the already-transformed DDB item bytes (the allocation-lean no-projection path,
/// spliced verbatim — issue #443) <b>or</b> a materialized AttributeValue map (used
/// when a <c>ProjectionExpression</c> requires structured access). Exactly one of
/// the two is populated.
/// </summary>
/// <remarks>
/// The bytes carried here MUST have been written with the default
/// <see cref="System.Text.Encodings.Web.JavaScriptEncoder"/> (see
/// <see cref="Persistence.InferredAttributeStorage.ExtractItemsFusedWithIdBytes{TReader,TSink}"/>)
/// so the spliced output is byte-identical to the model-serializer path the map
/// branch produces. <see cref="BatchGetResponseItemConverter"/> writes the bytes
/// via <see cref="Utf8JsonWriter.WriteRawValue(ReadOnlySpan{byte},bool)"/> and the
/// map via the same per-attribute <c>WritePropertyName</c> + <see cref="JsonElement.WriteTo"/>
/// sequence the source-generated dictionary converter uses.
/// </remarks>
[JsonConverter(typeof(BatchGetResponseItemConverter))]
internal readonly struct BatchGetResponseItem
{
    private readonly byte[]? _bytes;
    private readonly Dictionary<string, JsonElement>? _map;

    public BatchGetResponseItem(byte[] bytes)
    {
        _bytes = bytes;
        _map = null;
    }

    public BatchGetResponseItem(Dictionary<string, JsonElement> map)
    {
        _map = map;
        _bytes = null;
    }

    public byte[]? Bytes => _bytes;

    public Dictionary<string, JsonElement>? Map => _map;
}

internal sealed class BatchGetResponseItemConverter : JsonConverter<BatchGetResponseItem>
{
    public override BatchGetResponseItem Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("BatchGetResponseItem is write-only (response shape).");

    public override void Write(
        Utf8JsonWriter writer, BatchGetResponseItem value, JsonSerializerOptions options)
    {
        if (value.Bytes is { } bytes)
        {
            // Pre-transformed item written with the default encoder upstream:
            // splice verbatim. skipInputValidation is safe — the bytes are a
            // complete object emitted by Utf8JsonWriter.
            writer.WriteRawValue(bytes, skipInputValidation: true);
            return;
        }

        // Projection path: serialize the map exactly as the source-generated
        // Dictionary<string,JsonElement> converter would, so the bytes match the
        // legacy output element-for-element.
        var map = value.Map!;
        writer.WriteStartObject();
        foreach (var kv in map)
        {
            writer.WritePropertyName(kv.Key);
            kv.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}
