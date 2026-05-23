using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Xml.Linq;
using Aws2Azure.IntegrationTests.Fixtures;

namespace Aws2Azure.IntegrationTests.Sns;

internal static class SnsQueryApiClient
{
    private const string ApiVersion = "2010-03-31";
    private static readonly XNamespace Ns = "http://sns.amazonaws.com/doc/2010-03-31/";

    public static string CreateTopicName(string prefix)
        => prefix + "-" + Guid.NewGuid().ToString("N")[..20];

    public static string CreateSubscriptionEndpoint()
        => "arn:aws:sqs:us-east-1:000000000000:stub-" + Guid.NewGuid().ToString("N")[..12];

    public static async Task<SnsXmlResponse> SendActionAsync(
        HttpClient client,
        string action,
        IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var payloadPairs = new List<KeyValuePair<string, string>>
        {
            new("Action", action),
            new("Version", ApiVersion),
        };
        payloadPairs.AddRange(parameters);

        using var form = new FormUrlEncodedContent(payloadPairs);
        var payload = await form.ReadAsByteArrayAsync().ConfigureAwait(false);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(client.BaseAddress!, "/"))
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded")
        {
            CharSet = "utf-8",
        };

        TestSigV4Signer.SignHeader(
            request,
            payload,
            SnsServiceBusProxyFixture.AwsAccessKey,
            SnsServiceBusProxyFixture.AwsSecret,
            region: "us-east-1",
            service: "sns");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var xml = string.IsNullOrWhiteSpace(body) ? null : XDocument.Parse(body);
        return new SnsXmlResponse(response.StatusCode, body, xml);
    }

    public static Task<SnsXmlResponse> CreateTopicAsync(HttpClient client, string topicName)
        => SendActionAsync(client, "CreateTopic", [new("Name", topicName)]);

    public static Task<SnsXmlResponse> DeleteTopicAsync(HttpClient client, string topicArn)
        => SendActionAsync(client, "DeleteTopic", [new("TopicArn", topicArn)]);

    public static Task<SnsXmlResponse> ListTopicsAsync(HttpClient client)
        => SendActionAsync(client, "ListTopics", []);

    public static Task<SnsXmlResponse> GetTopicAttributesAsync(HttpClient client, string topicArn)
        => SendActionAsync(client, "GetTopicAttributes", [new("TopicArn", topicArn)]);

    public static Task<SnsXmlResponse> SubscribeAsync(HttpClient client, string topicArn, string protocol, string endpoint)
        => SendActionAsync(client, "Subscribe", [
            new("TopicArn", topicArn),
            new("Protocol", protocol),
            new("Endpoint", endpoint),
        ]);

    public static Task<SnsXmlResponse> ListSubscriptionsByTopicAsync(HttpClient client, string topicArn)
        => SendActionAsync(client, "ListSubscriptionsByTopic", [new("TopicArn", topicArn)]);

    public static Task<SnsXmlResponse> GetSubscriptionAttributesAsync(HttpClient client, string subscriptionArn)
        => SendActionAsync(client, "GetSubscriptionAttributes", [new("SubscriptionArn", subscriptionArn)]);

    public static Task<SnsXmlResponse> SetSubscriptionAttributeAsync(HttpClient client, string subscriptionArn, string attributeName, string attributeValue)
        => SendActionAsync(client, "SetSubscriptionAttributes", [
            new("SubscriptionArn", subscriptionArn),
            new("AttributeName", attributeName),
            new("AttributeValue", attributeValue),
        ]);

    public static Task<SnsXmlResponse> UnsubscribeAsync(HttpClient client, string subscriptionArn)
        => SendActionAsync(client, "Unsubscribe", [new("SubscriptionArn", subscriptionArn)]);

    public static string ReadTopicArn(SnsXmlResponse response)
        => ReadRequiredElement(response, "TopicArn");

    public static string ReadSubscriptionArn(SnsXmlResponse response)
        => ReadRequiredElement(response, "SubscriptionArn");

    public static IReadOnlyList<string> ReadTopicArns(SnsXmlResponse response)
        => response.Xml?.Descendants(Ns + "TopicArn").Select(x => x.Value).ToArray()
           ?? Array.Empty<string>();

    public static Dictionary<string, string> ReadAttributes(SnsXmlResponse response)
        => response.Xml?
            .Descendants(Ns + "entry")
            .Where(entry => entry.Element(Ns + "key") is not null)
            .ToDictionary(
                entry => entry.Element(Ns + "key")!.Value,
                entry => entry.Element(Ns + "value")?.Value ?? string.Empty,
                StringComparer.Ordinal)
           ?? new Dictionary<string, string>(StringComparer.Ordinal);

    public static IReadOnlyList<SnsListedSubscription> ReadListedSubscriptions(SnsXmlResponse response)
        => response.Xml?
            .Descendants(Ns + "member")
            .Where(member => member.Element(Ns + "SubscriptionArn") is not null)
            .Select(member => new SnsListedSubscription(
                member.Element(Ns + "SubscriptionArn")!.Value,
                member.Element(Ns + "Protocol")?.Value ?? string.Empty,
                member.Element(Ns + "Endpoint")?.Value ?? string.Empty,
                member.Element(Ns + "TopicArn")?.Value ?? string.Empty))
            .ToArray()
           ?? Array.Empty<SnsListedSubscription>();

    public static IReadOnlyList<string> ReadPublishBatchSuccessIds(SnsXmlResponse response)
        => response.Xml?.Descendants(Ns + "Successful").Descendants(Ns + "Id").Select(x => x.Value).ToArray()
           ?? Array.Empty<string>();

    public static IReadOnlyList<SnsBatchFailure> ReadPublishBatchFailures(SnsXmlResponse response)
        => response.Xml?
            .Descendants(Ns + "Failed")
            .Descendants(Ns + "member")
            .Select(member => new SnsBatchFailure(
                member.Element(Ns + "Id")?.Value ?? string.Empty,
                member.Element(Ns + "Code")?.Value ?? string.Empty,
                member.Element(Ns + "Message")?.Value ?? string.Empty,
                string.Equals(member.Element(Ns + "SenderFault")?.Value, "true", StringComparison.OrdinalIgnoreCase)))
            .ToArray()
           ?? Array.Empty<SnsBatchFailure>();

    public static string ExtractSubscriptionName(string subscriptionArn)
    {
        var parts = subscriptionArn.Split(':', 7, StringSplitOptions.None);
        return parts.Length == 7 ? parts[6] : string.Empty;
    }

    private static string ReadRequiredElement(SnsXmlResponse response, string localName)
        => response.Xml?.Descendants(Ns + localName).FirstOrDefault()?.Value
           ?? throw new InvalidOperationException($"SNS response did not contain '{localName}'. Body={response.Body}");
}

internal sealed record SnsXmlResponse(HttpStatusCode StatusCode, string Body, XDocument? Xml);
internal sealed record SnsListedSubscription(string SubscriptionArn, string Protocol, string Endpoint, string TopicArn);
internal sealed record SnsBatchFailure(string Id, string Code, string Message, bool SenderFault);
