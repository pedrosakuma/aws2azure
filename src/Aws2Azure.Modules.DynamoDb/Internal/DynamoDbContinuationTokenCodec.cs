using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Version-1 DynamoDB Query/Scan continuation contract. The wire value remains
/// the original base64-wrapped Cosmos continuation so adjacent runtimes can
/// consume tokens minted before or during a rolling upgrade.
/// </summary>
internal static class DynamoDbContinuationTokenCodec
{
    public static string? Extract(JsonElement? exclusiveStartKey)
    {
        if (exclusiveStartKey is not { } key)
        {
            return null;
        }
        if (key.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("ExclusiveStartKey must be an object.");
        }
        if (!key.TryGetProperty(
                DynamoDbPersistedFormatContract.ContinuationSentinelAttribute,
                out var sentinel))
        {
            throw new FormatException("continuation attribute is missing.");
        }
        if (sentinel.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("continuation attribute must be a typed string.");
        }
        if (!sentinel.TryGetProperty("S", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("continuation attribute must be a typed string.");
        }

        var encoded = value.GetString();
        if (string.IsNullOrEmpty(encoded))
        {
            throw new FormatException("continuation attribute must not be empty.");
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            throw new FormatException("continuation attribute is not valid base64.");
        }
    }

    public static Dictionary<string, JsonElement> BuildKey(string continuation)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(continuation));
        var json = $"{{\"S\":\"{encoded}\"}}";
        using var document = JsonDocument.Parse(json);
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [DynamoDbPersistedFormatContract.ContinuationSentinelAttribute] =
                document.RootElement.Clone(),
        };
    }
}
