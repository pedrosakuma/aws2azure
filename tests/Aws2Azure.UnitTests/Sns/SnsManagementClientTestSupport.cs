using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aws2Azure.UnitTests.Sns;

internal static class SnsManagementClientTestSupport
{
    public static ServiceBusTopicsCredentials NewCredentials() => new()
    {
        Namespace = "myns",
        SasKeyName = "RootManageSharedAccessKey",
        SasKey = "secret",
    };

    public static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers.Authorization = "AWS4-HMAC-SHA256 Credential=AKIAEXAMPLE/20250101/us-west-2/sns/aws4_request, SignedHeaders=content-type;host;x-amz-date, Signature=deadbeef";
        context.Request.Host = new HostString("sns.us-west-2.amazonaws.com");
        context.Response.Body = new MemoryStream();
        return context;
    }

    public static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();
    }

    public static string ReadElementValue(string xml, string elementName)
    {
        var startTag = "<" + elementName + ">";
        var endTag = "</" + elementName + ">";
        var start = xml.IndexOf(startTag, StringComparison.Ordinal);
        var end = xml.IndexOf(endTag, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Element '{elementName}' not found in XML: {xml}");
        return WebUtility.HtmlDecode(xml[(start + startTag.Length)..end]);
    }

    public static Dictionary<string, string> ReadAttributes(string xml)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true });

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "entry")
            {
                continue;
            }

            using var entryReader = reader.ReadSubtree();
            string? key = null;
            string? value = null;
            if (!entryReader.Read())
            {
                continue;
            }

            while (true)
            {
                if (entryReader.NodeType == XmlNodeType.Element)
                {
                    if (entryReader.LocalName == "key")
                    {
                        key = entryReader.ReadElementContentAsString();
                        if (entryReader.EOF)
                        {
                            break;
                        }

                        continue;
                    }

                    if (entryReader.LocalName == "value")
                    {
                        value = entryReader.ReadElementContentAsString();
                        if (entryReader.EOF)
                        {
                            break;
                        }

                        continue;
                    }
                }

                if (!entryReader.Read())
                {
                    break;
                }
            }

            if (key is not null)
            {
                attributes[key] = value ?? string.Empty;
            }
        }

        return attributes;
    }

    public static string BuildTopicsFeed(params string[] topicNames)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.Append("<feed xmlns=\"http://www.w3.org/2005/Atom\">");
        builder.Append("<title type=\"text\">Topics</title>");
        foreach (var topicName in topicNames)
        {
            builder.Append(BuildTopicEntryCore(topicName, 0, false, includeDeclaration: false));
        }

        builder.Append("</feed>");
        return builder.ToString();
    }

    public static string BuildTopicEntry(string topicName, int subscriptionCount = 0, bool requiresDuplicateDetection = false)
        => BuildTopicEntryCore(topicName, subscriptionCount, requiresDuplicateDetection, includeDeclaration: true);

    public static string BuildSubscriptionsFeed(params (string SubscriptionName, string UserMetadata)[] subscriptions)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.Append("<feed xmlns=\"http://www.w3.org/2005/Atom\">");
        builder.Append("<title type=\"text\">Subscriptions</title>");
        foreach (var subscription in subscriptions)
        {
            builder.Append(BuildSubscriptionEntryCore(
                subscription.SubscriptionName,
                subscription.UserMetadata,
                ServiceBusTopicsManagementClient.DefaultLockDurationIso8601,
                ServiceBusTopicsManagementClient.DefaultMaxDeliveryCount,
                ServiceBusTopicsManagementClient.LongIdleIso8601,
                includeDeclaration: false));
        }

        builder.Append("</feed>");
        return builder.ToString();
    }

    public static string BuildSubscriptionEntry(
        string subscriptionName,
        string? userMetadata,
        string lockDuration = ServiceBusTopicsManagementClient.DefaultLockDurationIso8601,
        int maxDeliveryCount = ServiceBusTopicsManagementClient.DefaultMaxDeliveryCount,
        string autoDeleteOnIdle = ServiceBusTopicsManagementClient.LongIdleIso8601,
        string? additionalPropertiesXml = null)
        => BuildSubscriptionEntryCore(subscriptionName, userMetadata, lockDuration, maxDeliveryCount, autoDeleteOnIdle, includeDeclaration: true, additionalPropertiesXml);

    private static string BuildTopicEntryCore(string topicName, int subscriptionCount, bool requiresDuplicateDetection, bool includeDeclaration)
    {
        var builder = new StringBuilder();
        if (includeDeclaration)
        {
            builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        }

        builder.Append("<entry xmlns=\"http://www.w3.org/2005/Atom\">");
        builder.Append("<title type=\"text\">").Append(topicName).Append("</title>");
        builder.Append("<content type=\"application/xml\"><TopicDescription xmlns=\"http://schemas.microsoft.com/netservices/2010/10/servicebus/connect\">");
        builder.Append("<SubscriptionCount>").Append(subscriptionCount).Append("</SubscriptionCount>");
        builder.Append("<RequiresDuplicateDetection>").Append(requiresDuplicateDetection ? "true" : "false").Append("</RequiresDuplicateDetection>");
        builder.Append("</TopicDescription></content></entry>");
        return builder.ToString();
    }

    private static string BuildSubscriptionEntryCore(
        string subscriptionName,
        string? userMetadata,
        string lockDuration,
        int maxDeliveryCount,
        string autoDeleteOnIdle,
        bool includeDeclaration,
        string? additionalPropertiesXml = null)
    {
        var builder = new StringBuilder();
        if (includeDeclaration)
        {
            builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        }

        builder.Append("<entry xmlns=\"http://www.w3.org/2005/Atom\">");
        builder.Append("<title type=\"text\">").Append(subscriptionName).Append("</title>");
        builder.Append("<content type=\"application/xml\"><SubscriptionDescription xmlns=\"http://schemas.microsoft.com/netservices/2010/10/servicebus/connect\">");
        builder.Append("<LockDuration>").Append(lockDuration).Append("</LockDuration>");
        builder.Append("<MaxDeliveryCount>").Append(maxDeliveryCount).Append("</MaxDeliveryCount>");
        builder.Append(additionalPropertiesXml);
        if (userMetadata is not null)
        {
            builder.Append("<UserMetadata>").Append(WebUtility.HtmlEncode(userMetadata)).Append("</UserMetadata>");
        }
        builder.Append("<AutoDeleteOnIdle>").Append(autoDeleteOnIdle).Append("</AutoDeleteOnIdle>");
        builder.Append("</SubscriptionDescription></content></entry>");
        return builder.ToString();
    }

    public static string SerializeMetadata(
        string protocol,
        string endpoint,
        string? filterPolicyJson = null,
        bool rawDeliveryEnabled = false,
        string? filterPolicyScope = null)
        => JsonSerializer.Serialize(
            new SnsSubscriptionMetadata
            {
                Protocol = protocol,
                Endpoint = endpoint,
                FilterPolicyJson = filterPolicyJson,
                FilterPolicyScope = filterPolicyScope,
                RawDeliveryEnabled = rawDeliveryEnabled,
            },
            SnsSubscriptionJsonContext.Default.SnsSubscriptionMetadata);

    public static ServiceBusTopicsManagementClient NewManagementClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var handler = new ScriptedHandler(responder);
        var httpClient = new AzureHttpClient(handler, ownsHandler: false);
        return new ServiceBusTopicsManagementClient(
            httpClient,
            new TestAuthenticator(),
            NullLogger<ServiceBusTopicsManagementClient>.Instance);
    }

    private sealed class TestAuthenticator : IServiceBusTopicsAuthenticator
    {
        public ValueTask AuthenticateAsync(HttpRequestMessage request, ServiceBusTopicsCredentials credentials, CancellationToken cancellationToken = default)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "TestAuth");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
