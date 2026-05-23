using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class SnsSubscriptionSupport
{
    internal const int ListSubscriptionsPageSize = 100;
    internal const int UserMetadataMaxLength = 1024;
    private const string DummyConfirmedSubscriptionId = "autoconfirmed";

    public static bool TryParseSubscribeRequest(
        IReadOnlyDictionary<string, string> parameters,
        out SubscribeRequest request,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        request = default!;
        if (!TryGetRequiredNonEmptyParameter(parameters, "TopicArn", out var topicArn, out error)
            || !SnsPublishSupport.TryParsePublishTopicArn(topicArn, out var topicName, out error)
            || !TryGetRequiredNonEmptyParameter(parameters, "Protocol", out var protocol, out error)
            || !TryGetRequiredNonEmptyParameter(parameters, "Endpoint", out var endpoint, out error))
        {
            return false;
        }

        protocol = protocol.Trim().ToLowerInvariant();
        if (!IsSupportedProtocol(protocol))
        {
            error = protocol switch
            {
                "email" or "email-json" or "sms" or "lambda" or "application" or "firehose"
                    => $"Protocol '{protocol}' is not supported in this slice. Supported protocols: sqs, https, http.",
                _ => $"Protocol '{protocol}' is invalid. Supported protocols: sqs, https, http."
            };
            return false;
        }

        if (!TryReadAttributes(parameters, out var attributes, out error))
        {
            return false;
        }

        string? filterPolicyJson = null;
        if (TryGetAttribute(attributes, "FilterPolicy", out var filterPolicyRaw))
        {
            filterPolicyJson = NormalizeJson(filterPolicyRaw, "FilterPolicy", out error);
            if (error is not null)
            {
                return false;
            }
        }

        var rawDeliveryEnabled = false;
        if (TryGetAttribute(attributes, "RawMessageDelivery", out var rawMessageDeliveryRaw)
            && !TryParseBoolean(rawMessageDeliveryRaw, out rawDeliveryEnabled))
        {
            error = "Attribute 'RawMessageDelivery' must be a boolean value ('true' or 'false').";
            return false;
        }

        var metadata = new SnsSubscriptionMetadata
        {
            Protocol = protocol,
            Endpoint = endpoint,
            FilterPolicyJson = filterPolicyJson,
            FilterPolicyScope = filterPolicyJson is null ? null : SnsSubscriptionMetadata.MessageAttributesScope,
            RawDeliveryEnabled = rawDeliveryEnabled,
        };

        if (!TrySerializeMetadata(metadata, out var serializedMetadata))
        {
            error = $"Subscription metadata exceeds the Azure Service Bus UserMetadata limit of {UserMetadataMaxLength} characters.";
            return false;
        }

        request = new SubscribeRequest(
            topicArn,
            topicName,
            protocol,
            endpoint,
            metadata,
            serializedMetadata);
        error = null;
        return true;
    }

    public static string CreateSubscriptionId(string topicArn, string protocol, string endpoint)
    {
        var payload = Encoding.UTF8.GetBytes(topicArn + "\n" + protocol + "\n" + endpoint);
        var hash = SHA256.HashData(payload);
        return Convert.ToHexString(hash).ToLowerInvariant()[..20];
    }

    public static string BuildSubscriptionArn(HttpContext context, string topicName, string subscriptionId)
        => BuildSubscriptionArn(SnsTopicSupport.ResolveRegion(context), topicName, subscriptionId);

    public static string BuildSubscriptionArn(string region, string topicName, string subscriptionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        return $"arn:aws:sns:{region}:{SnsTopicSupport.PlaceholderAccountId}:{topicName}:{subscriptionId}";
    }

    public static bool TryParseSubscriptionArn(string subscriptionArn, out string topicName, out string subscriptionId, out string? error)
    {
        topicName = string.Empty;
        subscriptionId = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(subscriptionArn))
        {
            error = "Parameter 'SubscriptionArn' is required.";
            return false;
        }

        var parts = subscriptionArn.Split(':', 7, StringSplitOptions.None);
        if (parts.Length != 7
            || !string.Equals(parts[0], "arn", StringComparison.Ordinal)
            || !string.Equals(parts[1], "aws", StringComparison.Ordinal)
            || !string.Equals(parts[2], "sns", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(parts[3])
            || string.IsNullOrWhiteSpace(parts[4])
            || string.IsNullOrWhiteSpace(parts[5])
            || string.IsNullOrWhiteSpace(parts[6]))
        {
            error = "SubscriptionArn must be a valid SNS subscription ARN of the form 'arn:aws:sns:{region}:{accountId}:{topicName}:{subscriptionId}'.";
            return false;
        }

        if (!SnsPublishSupport.TryParsePublishTopicArn(string.Join(':', parts, 0, 6), out topicName, out error))
        {
            return false;
        }

        subscriptionId = parts[6];
        return true;
    }

    public static string EncodeNextToken(int topicSkip, int subscriptionSkipWithinTopic)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(topicSkip);
        ArgumentOutOfRangeException.ThrowIfNegative(subscriptionSkipWithinTopic);

        var token = new SnsListSubscriptionsNextToken
        {
            TopicSkip = topicSkip,
            SubscriptionSkipWithinTopic = subscriptionSkipWithinTopic,
        };

        var json = JsonSerializer.Serialize(token, SnsSubscriptionJsonContext.Default.SnsListSubscriptionsNextToken);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static bool TryDecodeNextToken(string nextToken, out SnsListSubscriptionsNextToken token)
    {
        token = new SnsListSubscriptionsNextToken();
        if (string.IsNullOrWhiteSpace(nextToken))
        {
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(nextToken));
            var parsed = JsonSerializer.Deserialize(json, SnsSubscriptionJsonContext.Default.SnsListSubscriptionsNextToken);
            if (parsed is null || parsed.TopicSkip < 0 || parsed.SubscriptionSkipWithinTopic < 0)
            {
                return false;
            }

            token = parsed;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static ListedSubscription ToListedSubscription(HttpContext context, string topicName, string subscriptionId, string? userMetadata)
    {
        var metadata = DeserializeMetadata(userMetadata);
        return new ListedSubscription(
            BuildSubscriptionArn(context, topicName, subscriptionId),
            SnsTopicSupport.PlaceholderAccountId,
            metadata?.Protocol ?? "unknown",
            metadata?.Endpoint ?? string.Empty,
            SnsTopicSupport.BuildTopicArn(context, topicName));
    }

    public static bool MetadataMatches(string? existingMetadataJson, SnsSubscriptionMetadata expected)
    {
        var existing = DeserializeMetadata(existingMetadataJson);
        return existing is not null
            && string.Equals(existing.Protocol, expected.Protocol, StringComparison.Ordinal)
            && string.Equals(existing.Endpoint, expected.Endpoint, StringComparison.Ordinal)
            && string.Equals(existing.FilterPolicyJson, expected.FilterPolicyJson, StringComparison.Ordinal)
            && string.Equals(existing.FilterPolicyScope, expected.FilterPolicyScope, StringComparison.Ordinal)
            && existing.RawDeliveryEnabled == expected.RawDeliveryEnabled;
    }

    public static bool TrySerializeMetadata(SnsSubscriptionMetadata metadata, out string json)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        json = JsonSerializer.Serialize(metadata, SnsSubscriptionJsonContext.Default.SnsSubscriptionMetadata);
        return json.Length <= UserMetadataMaxLength;
    }

    public static string SerializeMetadata(SnsSubscriptionMetadata metadata)
    {
        if (!TrySerializeMetadata(metadata, out var json))
        {
            throw new InvalidOperationException($"Subscription metadata exceeds the Azure Service Bus UserMetadata limit of {UserMetadataMaxLength} characters.");
        }

        return json;
    }

    public static SnsSubscriptionMetadata ParseMetadata(string? userMetadata)
        => DeserializeMetadata(userMetadata) ?? new SnsSubscriptionMetadata();

    public static string? NormalizeJsonAttribute(string rawJson, string attributeName, out string? error)
        => NormalizeJson(rawJson, attributeName, out error);

    public static bool TryParseBooleanAttribute(string value, out bool result)
        => TryParseBoolean(value, out result);

    public static string ResolveConfirmSubscriptionArn(HttpContext context, string topicName, string token)
    {
        if (TryParseSubscriptionArn(token, out _, out _, out _))
        {
            return token;
        }

        if (TryDeriveSubscriptionIdFromToken(token, out var subscriptionId))
        {
            return BuildSubscriptionArn(context, topicName, subscriptionId);
        }

        return BuildSubscriptionArn(context, topicName, DummyConfirmedSubscriptionId);
    }

    private static bool IsSupportedProtocol(string protocol)
        => protocol is "sqs" or "https" or "http";

    private static bool TryReadAttributes(
        IReadOnlyDictionary<string, string> parameters,
        out Dictionary<string, string> attributes,
        out string? error)
    {
        var indexes = new SortedSet<int>();
        foreach (var key in parameters.Keys)
        {
            if (TryExtractEntryIndex(key, "Attributes.entry.", out var index))
            {
                indexes.Add(index);
            }
        }

        attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in indexes)
        {
            var prefix = $"Attributes.entry.{index}.";
            if (!TryGetParameterIgnoreCase(parameters, prefix + "key", out var name)
                || string.IsNullOrWhiteSpace(name)
                || !TryGetParameterIgnoreCase(parameters, prefix + "value", out var value))
            {
                error = $"Attribute entry {index} must include key and value.";
                return false;
            }

            attributes[name] = value;
        }

        error = null;
        return true;
    }

    private static SnsSubscriptionMetadata? DeserializeMetadata(string? userMetadata)
    {
        if (string.IsNullOrWhiteSpace(userMetadata))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(userMetadata, SnsSubscriptionJsonContext.Default.SnsSubscriptionMetadata);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NormalizeJson(string rawJson, string attributeName, out string? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                document.RootElement.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
        }
        catch (JsonException)
        {
            error = $"Attribute '{attributeName}' must contain valid JSON.";
            return null;
        }
    }

    private static bool TryGetRequiredNonEmptyParameter(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        out string value,
        out string? error)
    {
        if (!parameters.TryGetValue(name, out value!) || string.IsNullOrWhiteSpace(value))
        {
            value = string.Empty;
            error = $"Parameter '{name}' is required and must not be empty.";
            return false;
        }

        error = null;
        return true;
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

    private static bool TryGetAttribute(IReadOnlyDictionary<string, string> attributes, string name, out string value)
    {
        foreach (var pair in attributes)
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

    private static bool TryExtractEntryIndex(string key, string prefix, out int index)
    {
        index = 0;
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remaining = key.AsSpan(prefix.Length);
        var separator = remaining.IndexOf('.');
        if (separator <= 0)
        {
            return false;
        }

        return int.TryParse(remaining[..separator], out index);
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        if (bool.TryParse(value, out result))
        {
            return true;
        }

        if (string.Equals(value, "1", StringComparison.Ordinal))
        {
            result = true;
            return true;
        }

        if (string.Equals(value, "0", StringComparison.Ordinal))
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }

    private static bool TryDeriveSubscriptionIdFromToken(string token, out string subscriptionId)
    {
        subscriptionId = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (token.Length == 20)
        {
            for (var i = 0; i < token.Length; i++)
            {
                if (!Uri.IsHexDigit(token[i]))
                {
                    return false;
                }
            }

            subscriptionId = token.ToLowerInvariant();
            return true;
        }

        return false;
    }
}

internal sealed record SubscribeRequest(
    string TopicArn,
    string TopicName,
    string Protocol,
    string Endpoint,
    SnsSubscriptionMetadata Metadata,
    string UserMetadata);

internal sealed record ListedSubscription(
    string SubscriptionArn,
    string Owner,
    string Protocol,
    string Endpoint,
    string TopicArn);

internal sealed class SnsSubscriptionMetadata
{
    public const string MessageAttributesScope = "MessageAttributes";
    public const string MessageBodyScope = "MessageBody";

    public string Protocol { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string? FilterPolicyJson { get; set; }
    public string? FilterPolicyScope { get; set; }
    public bool RawDeliveryEnabled { get; set; }
}

internal sealed class SnsListSubscriptionsNextToken
{
    public int TopicSkip { get; set; }
    public int SubscriptionSkipWithinTopic { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SnsSubscriptionMetadata))]
[JsonSerializable(typeof(SnsListSubscriptionsNextToken))]
internal sealed partial class SnsSubscriptionJsonContext : JsonSerializerContext
{
}
