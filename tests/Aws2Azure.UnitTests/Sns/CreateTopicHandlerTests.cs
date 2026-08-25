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

public sealed class CreateTopicHandlerTests
{
    [Fact]
    public async Task HandleAsync_creates_topic_and_returns_topic_arn()
    {
        var managementClient = NewManagementClient(async (request, _) =>
        {
            Assert.Equal("https://myns.servicebus.windows.net/orders?api-version=2021-05", request.RequestUri!.ToString());
            Assert.True(request.Headers.TryGetValues("Authorization", out var authorization));
            Assert.Equal("TestAuth", Assert.Single(authorization));

            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            Assert.Equal(HttpMethod.Put, request.Method);
            var body = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Contains("<entry xmlns=\"http://www.w3.org/2005/Atom\">", body);
            Assert.Contains("TopicDescription", body);
            Assert.Contains("http://schemas.microsoft.com/netservices/2010/10/servicebus/connect", body);

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "application/atom+xml"),
            };
        });

        var context = NewContext();
        await CreateTopicHandler.HandleAsync(
            context,
            NewParseResult("orders"),
            NewCredentials(),
            new SnsSettings(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = ReadBody(context);
        Assert.Contains("<CreateTopicResponse", body);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:orders", body);
        Assert.Contains("<RequestId", body);
    }

    [Fact]
    public async Task HandleAsync_uses_service_bus_topic_alias_when_configured()
    {
        var credentials = NewCredentials();
        credentials.Topics = new Dictionary<string, SnsTopicSettings>
        {
            ["orders"] = new()
            {
                ServiceBusTopicName = "orders.v2",
            },
        };

        var managementClient = NewManagementClient(async (request, _) =>
        {
            Assert.Equal("https://myns.servicebus.windows.net/orders.v2?api-version=2021-05", request.RequestUri!.ToString());
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var body = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Contains("\"snsTopicName\":\"orders\"", body);
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        var context = NewContext();
        await CreateTopicHandler.HandleAsync(
            context,
            NewParseResult("orders"),
            credentials,
            new SnsSettings(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:orders", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_alias_conflicts_with_topics_owned_by_another_sns_name()
    {
        var credentials = NewCredentials();
        credentials.Topics = new Dictionary<string, SnsTopicSettings>
        {
            ["orders"] = new()
            {
                ServiceBusTopicName = "orders.v2",
            },
        };
        var foreignMetadata = System.Text.Json.JsonSerializer.Serialize(
            new SnsTopicMetadata
            {
                SnsTopicName = "payments",
            },
            SnsTopicJsonContext.Default.SnsTopicMetadata);
        var requestCount = 0;
        var managementClient = NewManagementClient((request, _) =>
        {
            requestCount++;
            return requestCount switch
            {
                1 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)),
                2 when request.Method == HttpMethod.Get => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SnsManagementClientTestSupport.BuildTopicEntry("orders.v2", userMetadata: foreignMetadata), Encoding.UTF8, "application/atom+xml"),
                }),
                _ => throw new Xunit.Sdk.XunitException("Unexpected request: " + request.Method + " " + request.RequestUri)
            };
        });

        var context = NewContext();
        await CreateTopicHandler.HandleAsync(
            context,
            NewParseResult("orders"),
            credentials,
            new SnsSettings(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("already bound to a different SNS topic", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_treats_preprovisioned_aliased_topics_without_ownership_metadata_as_idempotent()
    {
        var credentials = NewCredentials();
        credentials.Topics = new Dictionary<string, SnsTopicSettings>
        {
            ["orders"] = new()
            {
                ServiceBusTopicName = "orders.v2",
            },
        };
        var requestCount = 0;
        var managementClient = NewManagementClient((request, _) =>
        {
            requestCount++;
            return requestCount switch
            {
                1 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)),
                2 when request.Method == HttpMethod.Get => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SnsManagementClientTestSupport.BuildTopicEntry("orders.v2"), Encoding.UTF8, "application/atom+xml"),
                }),
                _ => throw new Xunit.Sdk.XunitException("Unexpected request: " + request.Method + " " + request.RequestUri)
            };
        });

        var context = NewContext();
        await CreateTopicHandler.HandleAsync(
            context,
            NewParseResult("orders"),
            credentials,
            new SnsSettings(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:orders", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_maps_supported_attributes_into_topic_description()
    {
        var managementClient = NewManagementClient(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var body = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Contains("<UserMetadata>", body);
            Assert.Contains("\"displayName\":\"Orders\"", body);
            Assert.Contains("\"policyJson\":\"{", body);
            Assert.Contains("\"deliveryPolicyJson\":\"{", body);
            Assert.Contains("<RequiresDuplicateDetection>true</RequiresDuplicateDetection>", body);
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        var context = NewContext();
        await CreateTopicHandler.HandleAsync(
            context,
            new SnsParseResult(
                SnsOperation.CreateTopic,
                new Dictionary<string, string>
                {
                    ["Name"] = "orders.fifo",
                    ["Attributes.entry.1.key"] = "DisplayName",
                    ["Attributes.entry.1.value"] = "Orders",
                    ["Attributes.entry.2.key"] = "Policy",
                    ["Attributes.entry.2.value"] = "{ \"Statement\": [] }",
                    ["Attributes.entry.3.key"] = "DeliveryPolicy",
                    ["Attributes.entry.3.value"] = "{ \"healthyRetryPolicy\": { \"numRetries\": 3 } }",
                    ["Attributes.entry.4.key"] = "FifoTopic",
                    ["Attributes.entry.4.value"] = "true",
                    ["Attributes.entry.5.key"] = "ContentBasedDeduplication",
                    ["Attributes.entry.5.value"] = "true",
                },
                null),
            NewCredentials(),
            new SnsSettings(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_creates_fifo_topics_with_duplicate_detection_enabled()
    {
        var managementClient = NewManagementClient(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var body = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Contains("<RequiresDuplicateDetection>true</RequiresDuplicateDetection>", body);
            Assert.Contains("<DuplicateDetectionHistoryTimeWindow>PT5M</DuplicateDetectionHistoryTimeWindow>", body);
            Assert.Contains("\"contentBasedDeduplication\":false", body);
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        var context = NewContext();
        await CreateTopicHandler.HandleAsync(
            context,
            new SnsParseResult(
                SnsOperation.CreateTopic,
                new Dictionary<string, string>
                {
                    ["Name"] = "orders.fifo",
                    ["Attributes.entry.1.key"] = "FifoTopic",
                    ["Attributes.entry.1.value"] = "true",
                },
                null),
            NewCredentials(),
            new SnsSettings(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("orders.fifo", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_incomplete_attribute_entries()
    {
        var context = NewContext();
        await CreateTopicHandler.HandleAsync(
            context,
            new SnsParseResult(
                SnsOperation.CreateTopic,
                new Dictionary<string, string>
                {
                    ["Name"] = "orders",
                    ["Attributes.entry.1.key"] = "Policy",
                },
                null),
            NewCredentials(),
            new SnsSettings(),
            NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called.")),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("Incomplete attribute entry", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_treats_existing_topic_as_idempotent()
    {
        var requests = 0;
        var managementClient = NewManagementClient((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
        });
        var context = NewContext();

        await CreateTopicHandler.HandleAsync(
            context,
            NewParseResult("orders"),
            NewCredentials(),
            new SnsSettings(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(1, requests);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:orders", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_does_not_overwrite_cached_fifo_state_for_existing_topics()
    {
        var credentials = NewCredentials();
        SnsFifoPublishSupport.RecordServiceBusTopicState(credentials, "orders.fifo", requiresDuplicateDetection: true, contentBasedDeduplication: false);
        var managementClient = NewManagementClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)));
        var context = NewContext();

        await CreateTopicHandler.HandleAsync(
            context,
            new SnsParseResult(
                SnsOperation.CreateTopic,
                new Dictionary<string, string>
                {
                    ["Name"] = "orders.fifo",
                    ["Attributes.entry.1.key"] = "FifoTopic",
                    ["Attributes.entry.1.value"] = "true",
                    ["Attributes.entry.2.key"] = "ContentBasedDeduplication",
                    ["Attributes.entry.2.value"] = "true",
                },
                null),
            credentials,
            new SnsSettings(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(SnsFifoPublishSupport.TryGetCachedServiceBusTopicState(credentials, "orders.fifo", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("orders!")]
    public async Task HandleAsync_validates_topic_name(string topicName)
    {
        var managementClient = NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called."));
        var context = NewContext();

        await CreateTopicHandler.HandleAsync(
            context,
            NewParseResult(topicName),
            NewCredentials(),
            new SnsSettings(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidParameter", ReadBody(context));
    }

    [Theory]
    [InlineData("-orders")]
    [InlineData("orders-")]
    [InlineData("_orders")]
    [InlineData("orders_")]
    [InlineData("-orders.fifo")]
    [InlineData("orders-.fifo")]
    public async Task HandleAsync_rejects_topic_names_that_violate_azure_service_bus_naming(string topicName)
    {
        var managementClient = NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called."));
        var context = NewContext();

        await CreateTopicHandler.HandleAsync(
            context,
            NewParseResult(topicName),
            NewCredentials(),
            new SnsSettings(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var body = ReadBody(context);
        Assert.Contains("InvalidParameter", body);
        Assert.Contains("Azure Service Bus topic-path naming restriction", body);
    }

    [Theory]
    [InlineData("orders", "FifoTopic", "true", "requires parameter 'Name' to end with '.fifo'")]
    [InlineData("orders.fifo", "FifoTopic", "false", "cannot be false")]
    [InlineData("orders", "ContentBasedDeduplication", "true", "supported only for FIFO topics")]
    [InlineData("orders", "ContentBasedDeduplication", "false", "supported only for FIFO topics")]
    public async Task HandleAsync_rejects_invalid_fifo_attribute_combinations(
        string topicName,
        string attributeName,
        string attributeValue,
        string expectedMessage)
    {
        var context = NewContext();
        await CreateTopicHandler.HandleAsync(
            context,
            new SnsParseResult(
                SnsOperation.CreateTopic,
                new Dictionary<string, string>
                {
                    ["Name"] = topicName,
                    ["Attributes.entry.1.key"] = attributeName,
                    ["Attributes.entry.1.value"] = attributeValue,
                },
                null),
            NewCredentials(),
            new SnsSettings(),
            NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called.")),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains(expectedMessage, ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_requires_explicit_fifo_topic_attribute_for_fifo_names()
    {
        var context = NewContext();
        await CreateTopicHandler.HandleAsync(
            context,
            NewParseResult("orders.fifo"),
            NewCredentials(),
            new SnsSettings(),
            NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called.")),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("FifoTopic", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_fifo_topics_when_resolved_backend_is_event_grid()
    {
        var context = NewContext();
        await CreateTopicHandler.HandleAsync(
            context,
            new SnsParseResult(
                SnsOperation.CreateTopic,
                new Dictionary<string, string>
                {
                    ["Name"] = "orders.fifo",
                    ["Attributes.entry.1.key"] = "FifoTopic",
                    ["Attributes.entry.1.value"] = "true",
                },
                null),
            NewCredentials(),
            new SnsSettings { DefaultBackend = SnsTopicBackend.EventGrid },
            NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called.")),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("Event Grid backend cannot honor SNS FIFO semantics", ReadBody(context));
    }

    private static ServiceBusTopicsCredentials NewCredentials() => new()
    {
        Namespace = "myns",
        SasKeyName = "RootManageSharedAccessKey",
        SasKey = "secret",
    };

    private static SnsParseResult NewParseResult(string topicName)
        => new(SnsOperation.CreateTopic, new Dictionary<string, string> { ["Name"] = topicName }, null);

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
