using System.Text.Json;
using System.Text.Json.Serialization;
using Aws2Azure.Modules.Sns.WireProtocol;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class SnsTopicAttributeSupport
{
    internal const int UserMetadataMaxLength = 1024;

    public static bool TryParseCreateTopicAttributes(
        IReadOnlyDictionary<string, string> parameters,
        string topicName,
        out CreateTopicAttributes attributes,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);

        attributes = new CreateTopicAttributes(false, false, null);
        error = null;

        var parsedAttributes = ReadCreateTopicAttributes(parameters, out error);
        if (error is not null)
        {
            return false;
        }

        var metadata = new SnsTopicMetadata();
        bool? fifoTopic = null;
        var contentBasedDeduplication = false;
        var hasContentBasedDeduplication = false;
        var isFifoTopic = topicName.EndsWith(".fifo", StringComparison.Ordinal);
        foreach (var (name, value) in parsedAttributes)
        {
            switch (name)
            {
                case "DisplayName":
                    metadata.DisplayName = string.IsNullOrWhiteSpace(value) ? null : value;
                    break;
                case "Policy":
                    metadata.PolicyJson = NormalizeOptionalJson(value, "Policy", out error);
                    if (error is not null)
                    {
                        return false;
                    }

                    break;
                case "DeliveryPolicy":
                    metadata.DeliveryPolicyJson = NormalizeOptionalJson(value, "DeliveryPolicy", out error);
                    if (error is not null)
                    {
                        return false;
                    }

                    break;
                case "FifoTopic":
                    if (!SnsSubscriptionSupport.TryParseBooleanAttribute(value, out var parsedFifoTopic))
                    {
                        error = "Attribute 'FifoTopic' must be a boolean value ('true' or 'false').";
                        return false;
                    }

                    fifoTopic = parsedFifoTopic;
                    break;
                case "ContentBasedDeduplication":
                    if (!SnsSubscriptionSupport.TryParseBooleanAttribute(value, out contentBasedDeduplication))
                    {
                        error = "Attribute 'ContentBasedDeduplication' must be a boolean value ('true' or 'false').";
                        return false;
                    }

                    hasContentBasedDeduplication = true;
                    break;
            }
        }

        if (fifoTopic == true && !isFifoTopic)
        {
            error = "Attribute 'FifoTopic' requires parameter 'Name' to end with '.fifo'.";
            return false;
        }

        if (fifoTopic is null && isFifoTopic)
        {
            error = "Attribute 'FifoTopic' must be set to 'true' when parameter 'Name' ends with '.fifo'.";
            return false;
        }

        if (fifoTopic == false && isFifoTopic)
        {
            error = "Attribute 'FifoTopic' cannot be false when parameter 'Name' ends with '.fifo'.";
            return false;
        }

        if (hasContentBasedDeduplication && !isFifoTopic)
        {
            error = "Attribute 'ContentBasedDeduplication' is supported only for FIFO topics whose names end with '.fifo'.";
            return false;
        }

        metadata.FifoTopic = isFifoTopic ? true : null;
        metadata.ContentBasedDeduplication = isFifoTopic ? contentBasedDeduplication : null;
        if (!TryBuildUserMetadata(metadata, out var userMetadata))
        {
            error = $"Topic metadata exceeds the Azure Service Bus UserMetadata limit of {UserMetadataMaxLength} characters.";
            return false;
        }

        attributes = new CreateTopicAttributes(isFifoTopic, isFifoTopic || contentBasedDeduplication, userMetadata);
        return true;
    }

    public static SnsTopicMetadata ParseMetadata(string? userMetadata)
        => DeserializeMetadata(userMetadata) ?? new SnsTopicMetadata();

    public static bool TryBuildUserMetadata(SnsTopicMetadata metadata, out string? userMetadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (string.IsNullOrWhiteSpace(metadata.DisplayName)
            && string.IsNullOrWhiteSpace(metadata.SnsTopicName)
            && string.IsNullOrWhiteSpace(metadata.PolicyJson)
            && string.IsNullOrWhiteSpace(metadata.DeliveryPolicyJson)
            && metadata.FifoTopic is null
            && metadata.ContentBasedDeduplication is null)
        {
            userMetadata = null;
            return true;
        }

        userMetadata = JsonSerializer.Serialize(metadata, SnsTopicJsonContext.Default.SnsTopicMetadata);
        return userMetadata.Length <= UserMetadataMaxLength;
    }

    private static Dictionary<string, string> ReadCreateTopicAttributes(IReadOnlyDictionary<string, string> parameters, out string? error)
    {
        error = null;
        var indexes = new SortedSet<int>();
        foreach (var key in parameters.Keys)
        {
            if (SnsParameterParsing.TryExtractEntryIndex(key, "Attributes.entry.", out var index))
            {
                indexes.Add(index);
            }
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var index in indexes)
        {
            var prefix = $"Attributes.entry.{index}.";
            if (!TryGetParameterIgnoreCase(parameters, prefix + "key", out var name)
                || string.IsNullOrWhiteSpace(name)
                || !TryGetParameterIgnoreCase(parameters, prefix + "value", out var value))
            {
                error = $"Incomplete attribute entry at index {index}.";
                return attributes;
            }

            attributes[name] = value;
        }

        return attributes;
    }

    private static string? NormalizeOptionalJson(string value, string attributeName, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = null;
            return null;
        }

        return SnsSubscriptionSupport.NormalizeJsonAttribute(value, attributeName, out error);
    }

    private static SnsTopicMetadata? DeserializeMetadata(string? userMetadata)
    {
        if (string.IsNullOrWhiteSpace(userMetadata))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(userMetadata, SnsTopicJsonContext.Default.SnsTopicMetadata);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetParameterIgnoreCase(IReadOnlyDictionary<string, string> parameters, string name, out string value)
    {
        if (parameters.TryGetValue(name, out value!))
        {
            return true;
        }

        foreach (var pair in parameters)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}

internal readonly record struct CreateTopicAttributes(
    bool IsFifoTopic,
    bool RequiresDuplicateDetection,
    string? UserMetadata);

internal sealed class SnsTopicMetadata
{
    public string? SnsTopicName { get; set; }
    public string? DisplayName { get; set; }
    public string? PolicyJson { get; set; }
    public string? DeliveryPolicyJson { get; set; }
    public bool? FifoTopic { get; set; }
    public bool? ContentBasedDeduplication { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SnsTopicMetadata))]
internal sealed partial class SnsTopicJsonContext : JsonSerializerContext
{
}
