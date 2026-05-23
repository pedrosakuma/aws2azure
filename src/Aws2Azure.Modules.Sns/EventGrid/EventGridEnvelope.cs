using System.Text.Json.Serialization;

namespace Aws2Azure.Modules.Sns.EventGrid;

internal static class SnsEventGridConstants
{
    public const string ApiVersion = "2018-01-01";
    public const string EventType = "aws.sns.Message";
    public const string DataVersion = "1.0";
}

internal sealed class SnsEventGridEnvelope
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = SnsEventGridConstants.EventType;

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("eventTime")]
    public DateTimeOffset EventTime { get; set; }

    [JsonPropertyName("dataVersion")]
    public string DataVersion { get; set; } = SnsEventGridConstants.DataVersion;

    [JsonPropertyName("data")]
    public SnsEventGridData Data { get; set; } = new();
}

internal sealed class SnsEventGridData
{
    [JsonPropertyName("Subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("Message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("MessageAttributes")]
    public Dictionary<string, SnsEventGridMessageAttribute> MessageAttributes { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("MessageStructure")]
    public string? MessageStructure { get; set; }

    [JsonPropertyName("TopicArn")]
    public string TopicArn { get; set; } = string.Empty;
}

internal sealed class SnsEventGridMessageAttribute
{
    [JsonPropertyName("Type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("Value")]
    public string Value { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SnsEventGridEnvelope))]
internal sealed partial class SnsEventGridJsonContext : JsonSerializerContext
{
}
