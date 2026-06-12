using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.EventGrid;

namespace Aws2Azure.Modules.Sns.Operations;

internal readonly record struct SnsTopicRoute(
    SnsTopicBackend Backend,
    string ServiceBusTopicName,
    string? EventGridTopicEndpoint,
    string? EventGridAccessKey);

internal static class SnsTopicRouting
{
    public static SnsTopicRoute Resolve(ServiceBusTopicsCredentials credentials, SnsSettings snsSettings, string topicName)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(snsSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);

        var settings = ResolveTopicSettings(credentials.Topics, topicName);
        return new SnsTopicRoute(
            settings?.Backend ?? snsSettings.DefaultBackend,
            string.IsNullOrWhiteSpace(settings?.ServiceBusTopicName) ? topicName : settings!.ServiceBusTopicName!,
            settings?.EventGridTopicEndpoint,
            settings?.EventGridAccessKey);
    }

    public static EventGridPublishDestination ResolveEventGridDestination(SnsTopicRoute route, EventGridCredentials? credentials)
    {
        if (route.Backend != SnsTopicBackend.EventGrid)
        {
            throw new InvalidOperationException("Event Grid destination requested for a non-Event-Grid SNS route.");
        }

        var endpoint = !string.IsNullOrWhiteSpace(route.EventGridTopicEndpoint)
            ? route.EventGridTopicEndpoint!
            : BuildEndpoint(credentials);
        var accessKey = !string.IsNullOrWhiteSpace(route.EventGridAccessKey)
            ? route.EventGridAccessKey
            : credentials?.AccessKey;
        return new EventGridPublishDestination(
            endpoint,
            accessKey,
            credentials?.AuthMode ?? AzureAuthMode.ClientSecret,
            credentials?.TenantId,
            credentials?.ClientId,
            credentials?.ClientSecret);
    }

    private static SnsTopicSettings? ResolveTopicSettings(IReadOnlyDictionary<string, SnsTopicSettings>? topics, string topicName)
    {
        if (topics is null || topics.Count == 0)
        {
            return null;
        }

        SnsTopicSettings? best = null;
        var bestWeight = -1;
        foreach (var (pattern, settings) in topics)
        {
            if (settings is null || string.IsNullOrWhiteSpace(pattern) || !MatchesPattern(pattern, topicName))
            {
                continue;
            }

            var weight = HasWildcards(pattern) ? pattern.Length : int.MaxValue;
            if (weight > bestWeight)
            {
                best = settings;
                bestWeight = weight;
            }
        }

        return best;
    }

    internal static string BuildEndpoint(EventGridCredentials? credentials)
    {
        if (credentials is null)
        {
            throw new InvalidOperationException("SNS Event Grid routing requires Event Grid credentials or a per-topic endpoint override.");
        }

        if (!string.IsNullOrWhiteSpace(credentials.Endpoint))
        {
            return credentials.Endpoint;
        }

        if (string.IsNullOrWhiteSpace(credentials.Namespace) || string.IsNullOrWhiteSpace(credentials.TopicName))
        {
            throw new InvalidOperationException("SNS Event Grid routing requires either eventGrid.endpoint or eventGrid.namespace + eventGrid.topicName.");
        }

        return "https://"
            + credentials.TopicName.Trim()
            + "."
            + credentials.Namespace.Trim().TrimStart('.')
            + "/api/events";
    }

    private static bool HasWildcards(string pattern)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] is '*' or '?')
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesPattern(string pattern, string value)
    {
        var patternIndex = 0;
        var valueIndex = 0;
        var starIndex = -1;
        var starValueIndex = 0;

        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == '?' || pattern[patternIndex] == value[valueIndex]))
            {
                patternIndex++;
                valueIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                starValueIndex = valueIndex;
                continue;
            }

            if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++starValueIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }
}
