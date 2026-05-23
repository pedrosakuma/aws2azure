using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

    public static string BuildTopicsFeed(params string[] topicNames)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.Append("<feed xmlns=\"http://www.w3.org/2005/Atom\">");
        builder.Append("<title type=\"text\">Topics</title>");
        foreach (var topicName in topicNames)
        {
            builder.Append("<entry>");
            builder.Append("<title type=\"text\">").Append(topicName).Append("</title>");
            builder.Append("<content type=\"application/xml\"><TopicDescription xmlns=\"http://schemas.microsoft.com/netservices/2010/10/servicebus/connect\" /></content>");
            builder.Append("</entry>");
        }

        builder.Append("</feed>");
        return builder.ToString();
    }

    public static string BuildSubscriptionsFeed(params (string SubscriptionName, string UserMetadata)[] subscriptions)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.Append("<feed xmlns=\"http://www.w3.org/2005/Atom\">");
        builder.Append("<title type=\"text\">Subscriptions</title>");
        foreach (var subscription in subscriptions)
        {
            builder.Append("<entry>");
            builder.Append("<title type=\"text\">").Append(subscription.SubscriptionName).Append("</title>");
            builder.Append("<content type=\"application/xml\"><SubscriptionDescription xmlns=\"http://schemas.microsoft.com/netservices/2010/10/servicebus/connect\">");
            if (!string.IsNullOrEmpty(subscription.UserMetadata))
            {
                builder.Append("<UserMetadata>").Append(WebUtility.HtmlEncode(subscription.UserMetadata)).Append("</UserMetadata>");
            }
            builder.Append("</SubscriptionDescription></content>");
            builder.Append("</entry>");
        }

        builder.Append("</feed>");
        return builder.ToString();
    }

    public static string SerializeMetadata(string protocol, string endpoint, string? filterPolicyJson = null, bool rawDeliveryEnabled = false)
        => JsonSerializer.Serialize(
            new SnsSubscriptionMetadata
            {
                Protocol = protocol,
                Endpoint = endpoint,
                FilterPolicyJson = filterPolicyJson,
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
