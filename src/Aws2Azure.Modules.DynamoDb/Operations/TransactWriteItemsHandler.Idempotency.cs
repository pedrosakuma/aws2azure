using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core.Buffers;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static partial class TransactWriteItemsHandler
{
    private const string FingerprintVersion = "a2a-ddb-transact-write-v1";

    private static bool TryValidateClientRequestToken(
        string? token,
        out string error)
    {
        if (token is null)
        {
            error = string.Empty;
            return true;
        }
        if (token.Length is < 1 or > 36)
        {
            error =
                "ClientRequestToken must have a length between 1 and 36 characters.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    internal static string BuildIdempotencyRecordId(string token)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(token))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return DynamoDbPersistedFormatContract.TransactionIdempotencyRecordIdPrefix
            + encoded;
    }

    private static string ComputeRequestFingerprint(
        string tableName,
        string partitionKey,
        byte[] body,
        PreparedRequestOp[] operations)
    {
        using var canonical = new PooledByteBufferWriter(512);
        using (var writer = new Utf8JsonWriter(canonical))
        {
            writer.WriteStartObject();
            writer.WriteString("version", FingerprintVersion);
            writer.WriteString("table", tableName);
            writer.WriteString("partition", partitionKey);
            writer.WriteStartArray("operations");
            foreach (var operation in operations)
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "type",
                    operation.Kind switch
                    {
                        OpKind.Put => "PUT",
                        OpKind.Delete => "DELETE",
                        _ => "CHECK",
                    });
                writer.WriteString("id", operation.Id);
                if (operation.Kind == OpKind.Put)
                {
                    using var operationDocument = JsonDocument.Parse(
                        body.AsMemory(
                            operation.Range.Start,
                            operation.Range.Length),
                        TransactItemParseOptions);
                    writer.WritePropertyName("item");
                    WriteCanonicalAttributeMap(
                        writer,
                        operationDocument.RootElement.GetProperty("Item"));
                }

                writer.WritePropertyName("condition");
                if (operation.ConditionJson is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteRawValue(operation.ConditionJson);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(canonical.WrittenMemory.Span, digest);
        return Convert.ToHexStringLower(digest);
    }

    private static void WriteCanonicalAttributeMap(
        Utf8JsonWriter writer,
        JsonElement map)
    {
        var properties = new List<JsonProperty>();
        foreach (var property in map.EnumerateObject())
        {
            properties.Add(property);
        }
        properties.Sort(static (left, right) =>
            string.CompareOrdinal(left.Name, right.Name));

        writer.WriteStartObject();
        foreach (var property in properties)
        {
            writer.WritePropertyName(property.Name);
            WriteCanonicalAttributeValue(writer, property.Value);
        }
        writer.WriteEndObject();
    }

    private static void WriteCanonicalAttributeValue(
        Utf8JsonWriter writer,
        JsonElement attribute)
    {
        if (!ParsedAttributeValue.TryParse(attribute, out var parsed))
        {
            throw new ArgumentException(
                "AttributeValue must contain exactly one supported type tag.");
        }

        writer.WriteStartObject();
        writer.WritePropertyName(parsed.TypeTag);
        switch (parsed.TypeTag)
        {
            case AttributeValueTypes.String:
                writer.WriteStringValue(parsed.Value.GetString());
                break;
            case AttributeValueTypes.Number:
                if (!InferredAttributeStorage.TryNormalizeDdbNumber(
                        parsed.Value.GetString() ?? string.Empty,
                        out var canonical,
                        out _,
                        out var numberError))
                {
                    throw new ArgumentException(numberError);
                }
                writer.WriteStringValue(canonical);
                break;
            case AttributeValueTypes.Binary:
                writer.WriteStringValue(
                    Convert.ToBase64String(
                        Convert.FromBase64String(
                            parsed.Value.GetString() ?? string.Empty)));
                break;
            case AttributeValueTypes.Bool:
                writer.WriteBooleanValue(parsed.Value.GetBoolean());
                break;
            case AttributeValueTypes.Null:
                writer.WriteBooleanValue(true);
                break;
            case AttributeValueTypes.Map:
                WriteCanonicalAttributeMap(writer, parsed.Value);
                break;
            case AttributeValueTypes.List:
                writer.WriteStartArray();
                foreach (var element in parsed.Value.EnumerateArray())
                {
                    WriteCanonicalAttributeValue(writer, element);
                }
                writer.WriteEndArray();
                break;
            case AttributeValueTypes.StringSet:
                WriteSortedStringSet(writer, parsed.Value);
                break;
            case AttributeValueTypes.NumberSet:
                WriteSortedNumberSet(writer, parsed.Value);
                break;
            case AttributeValueTypes.BinarySet:
                WriteSortedBinarySet(writer, parsed.Value);
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported AttributeValue type '{parsed.TypeTag}'.");
        }
        writer.WriteEndObject();
    }

    private static void WriteSortedStringSet(
        Utf8JsonWriter writer,
        JsonElement values)
    {
        var sorted = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            sorted.Add(value.GetString() ?? string.Empty);
        }
        sorted.Sort(StringComparer.Ordinal);
        writer.WriteStartArray();
        foreach (var value in sorted)
        {
            writer.WriteStringValue(value);
        }
        writer.WriteEndArray();
    }

    private static void WriteSortedNumberSet(
        Utf8JsonWriter writer,
        JsonElement values)
    {
        var sorted = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (!InferredAttributeStorage.TryNormalizeDdbNumber(
                    value.GetString() ?? string.Empty,
                    out var canonical,
                    out _,
                    out var error))
            {
                throw new ArgumentException(error);
            }
            sorted.Add(canonical);
        }
        sorted.Sort(StringComparer.Ordinal);
        writer.WriteStartArray();
        foreach (var value in sorted)
        {
            writer.WriteStringValue(value);
        }
        writer.WriteEndArray();
    }

    private static void WriteSortedBinarySet(
        Utf8JsonWriter writer,
        JsonElement values)
    {
        var sorted = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            sorted.Add(
                Convert.ToBase64String(
                    Convert.FromBase64String(
                        value.GetString() ?? string.Empty)));
        }
        sorted.Sort(StringComparer.Ordinal);
        writer.WriteStartArray();
        foreach (var value in sorted)
        {
            writer.WriteStringValue(value);
        }
        writer.WriteEndArray();
    }
}
