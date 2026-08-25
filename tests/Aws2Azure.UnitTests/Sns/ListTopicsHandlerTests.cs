using System.Net;
using System.Net.Http;
using System.Text;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aws2Azure.UnitTests.Sns;

public sealed class ListTopicsHandlerTests
{
    [Fact]
    public async Task HandleAsync_returns_topic_arns_from_atom_feed()
    {
        var managementClient = NewManagementClient((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            return uri switch
            {
                "https://myns.servicebus.windows.net/$Resources/topics?api-version=2021-05&$skip=0&$top=100"
                    => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(BuildFeed("orders", "payments"), Encoding.UTF8, "application/atom+xml"),
                    }),
                "https://myns.servicebus.windows.net/orders?api-version=2021-05"
                    => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SnsManagementClientTestSupport.BuildTopicEntry("orders"), Encoding.UTF8, "application/atom+xml"),
                    }),
                "https://myns.servicebus.windows.net/payments?api-version=2021-05"
                    => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SnsManagementClientTestSupport.BuildTopicEntry("payments"), Encoding.UTF8, "application/atom+xml"),
                    }),
                _ => throw new Xunit.Sdk.XunitException("Unexpected URI: " + uri)
            };
        });

        var context = NewContext();
        await ListTopicsHandler.HandleAsync(
            context,
            NewParseResult(null),
            NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = ReadBody(context);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:orders", body);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:payments", body);
        Assert.DoesNotContain("<NextToken>", body);
    }

    [Fact]
    public async Task HandleAsync_round_trips_next_token_as_skip_counter()
    {
        var firstPageTopics = new string[100];
        for (var i = 0; i < firstPageTopics.Length; i++)
        {
            firstPageTopics[i] = "topic-" + i.ToString("D3");
        }

        var managementClient = NewManagementClient((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.EndsWith("$skip=0&$top=100", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(BuildFeed(firstPageTopics), Encoding.UTF8, "application/atom+xml"),
                });
            }

            if (uri == "https://myns.servicebus.windows.net/$Resources/topics?api-version=2021-05&$skip=100&$top=100")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(BuildFeed("topic-100"), Encoding.UTF8, "application/atom+xml"),
                });
            }

            if (uri.StartsWith("https://myns.servicebus.windows.net/topic-", StringComparison.Ordinal))
            {
                var topicName = uri["https://myns.servicebus.windows.net/".Length..^"?api-version=2021-05".Length];
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SnsManagementClientTestSupport.BuildTopicEntry(topicName), Encoding.UTF8, "application/atom+xml"),
                });
            }

            throw new Xunit.Sdk.XunitException("Unexpected URI: " + uri);
        });

        var firstContext = NewContext();
        await ListTopicsHandler.HandleAsync(
            firstContext,
            NewParseResult(null),
            NewCredentials(),
            managementClient,
            CancellationToken.None);

        var firstBody = ReadBody(firstContext);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:topic-099", firstBody);
        var nextToken = ReadElementValue(firstBody, "NextToken");
        Assert.False(string.IsNullOrWhiteSpace(nextToken));

        var secondContext = NewContext();
        await ListTopicsHandler.HandleAsync(
            secondContext,
            NewParseResult(nextToken),
            NewCredentials(),
            managementClient,
            CancellationToken.None);

        var secondBody = ReadBody(secondContext);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:topic-100", secondBody);
        Assert.DoesNotContain("<NextToken>", secondBody);
    }

    [Fact]
    public async Task HandleAsync_maps_alias_backed_topics_back_to_their_sns_names()
    {
        var topicMetadata = System.Text.Json.JsonSerializer.Serialize(
            new SnsTopicMetadata
            {
                SnsTopicName = "orders",
            },
            SnsTopicJsonContext.Default.SnsTopicMetadata);
        var credentials = NewCredentials();
        credentials.Topics = new Dictionary<string, SnsTopicSettings>
        {
            ["orders"] = new()
            {
                ServiceBusTopicName = "orders.v2",
            },
        };
        var managementClient = NewManagementClient((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            return uri switch
            {
                "https://myns.servicebus.windows.net/$Resources/topics?api-version=2021-05&$skip=0&$top=100"
                    => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(BuildFeed("orders.v2"), Encoding.UTF8, "application/atom+xml"),
                    }),
                "https://myns.servicebus.windows.net/orders.v2?api-version=2021-05"
                    => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SnsManagementClientTestSupport.BuildTopicEntry("orders.v2", userMetadata: topicMetadata), Encoding.UTF8, "application/atom+xml"),
                    }),
                _ => throw new Xunit.Sdk.XunitException("Unexpected URI: " + uri)
            };
        });

        var context = NewContext();
        await ListTopicsHandler.HandleAsync(
            context,
            NewParseResult(null),
            credentials,
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = ReadBody(context);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:orders", body);
        Assert.DoesNotContain("orders.v2", body);
    }

    [Fact]
    public async Task HandleAsync_hides_topics_when_alias_config_no_longer_matches_stored_ownership()
    {
        var topicMetadata = System.Text.Json.JsonSerializer.Serialize(
            new SnsTopicMetadata
            {
                SnsTopicName = "orders",
            },
            SnsTopicJsonContext.Default.SnsTopicMetadata);
        var credentials = NewCredentials();
        credentials.Topics = new Dictionary<string, SnsTopicSettings>
        {
            ["payments"] = new()
            {
                ServiceBusTopicName = "orders.v2",
            },
        };
        var managementClient = NewManagementClient((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            return uri switch
            {
                "https://myns.servicebus.windows.net/$Resources/topics?api-version=2021-05&$skip=0&$top=100"
                    => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(BuildFeed("orders.v2"), Encoding.UTF8, "application/atom+xml"),
                    }),
                "https://myns.servicebus.windows.net/orders.v2?api-version=2021-05"
                    => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SnsManagementClientTestSupport.BuildTopicEntry("orders.v2", userMetadata: topicMetadata), Encoding.UTF8, "application/atom+xml"),
                    }),
                _ => throw new Xunit.Sdk.XunitException("Unexpected URI: " + uri)
            };
        });

        var context = NewContext();
        await ListTopicsHandler.HandleAsync(
            context,
            NewParseResult(null),
            credentials,
            managementClient,
            CancellationToken.None);

        var body = ReadBody(context);
        Assert.DoesNotContain("arn:aws:sns:us-west-2:000000000000:orders", body);
        Assert.DoesNotContain("arn:aws:sns:us-west-2:000000000000:payments", body);
    }

    private static ServiceBusTopicsCredentials NewCredentials() => new()
    {
        Namespace = "myns",
        SasKeyName = "RootManageSharedAccessKey",
        SasKey = "secret",
    };

    private static SnsParseResult NewParseResult(string? nextToken)
    {
        var parameters = new Dictionary<string, string>();
        if (nextToken is not null)
        {
            parameters["NextToken"] = nextToken;
        }

        return new SnsParseResult(SnsOperation.ListTopics, parameters, null);
    }

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers.Authorization = "AWS4-HMAC-SHA256 Credential=AKIAEXAMPLE/20250101/us-west-2/sns/aws4_request, SignedHeaders=content-type;host;x-amz-date, Signature=deadbeef";
        context.Request.Host = new HostString("sns.us-west-2.amazonaws.com");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();
    }

    private static string ReadElementValue(string xml, string elementName)
    {
        var startTag = "<" + elementName + ">";
        var endTag = "</" + elementName + ">";
        var start = xml.IndexOf(startTag, StringComparison.Ordinal);
        var end = xml.IndexOf(endTag, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Element '{elementName}' not found in XML: {xml}");
        return xml[(start + startTag.Length)..end];
    }

    private static string BuildFeed(params string[] topicNames)
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

    private static ServiceBusTopicsManagementClient NewManagementClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
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
