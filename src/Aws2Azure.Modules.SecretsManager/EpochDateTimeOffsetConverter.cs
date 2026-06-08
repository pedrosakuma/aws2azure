using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.Modules.SecretsManager;

/// <summary>
/// Serializes <see cref="DateTimeOffset"/> values as AWS-style Unix epoch
/// seconds (a JSON number with fractional milliseconds), matching the AWS JSON
/// 1.1 wire protocol used by Secrets Manager. The AWS SDK's response
/// unmarshaller reads timestamps as numbers; emitting an ISO-8601 string here
/// makes the SDK's list-element unmarshaller fail to advance and spin in an
/// allocation loop, so this converter is required for wire faithfulness.
/// Applied to <c>DateTimeOffset?</c> properties too — System.Text.Json unwraps
/// the nullable and emits <c>null</c> when the value is absent.
/// </summary>
internal sealed class EpochDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            var seconds = reader.GetDouble();
            return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000d));
        }

        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } text
            && DateTimeOffset.TryParse(text, out var parsed))
        {
            return parsed;
        }

        throw new JsonException("Expected a Unix epoch number for a Secrets Manager timestamp.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.ToUnixTimeMilliseconds() / 1000d);
    }
}
