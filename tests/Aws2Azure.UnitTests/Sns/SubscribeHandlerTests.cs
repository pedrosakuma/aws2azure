using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aws2Azure.UnitTests.Sns;

public sealed class SubscribeHandlerTests
{
    [Theory]
    [InlineData("sqs", "arn:aws:sqs:us-west-2:000000000000:orders")]
    [InlineData("https", "https://example.com/hooks/orders")]
    [InlineData("http", "http://example.com/hooks/orders")]
    public async Task HandleAsync_creates_subscription_for_supported_protocols(string protocol, string endpoint)
    {
        var managementClient = SnsManagementClientTestSupport.NewManagementClient(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("https://myns.servicebus.windows.net/orders/subscriptions/" +
                         SnsSubscriptionSupport.CreateSubscriptionId("arn:aws:sns:us-west-2:000000000000:orders", protocol, endpoint) +
                         "?api-version=2021-05", request.RequestUri!.ToString());
            Assert.True(request.Headers.TryGetValues("Authorization", out var authorization));
            Assert.Equal("TestAuth", Assert.Single(authorization));

            var body = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Contains("<LockDuration>PT30S</LockDuration>", body);
            Assert.Contains("<MaxDeliveryCount>10</MaxDeliveryCount>", body);
            Assert.Contains(ServiceBusTopicsManagementClient.LongIdleIso8601, body);

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "application/atom+xml"),
            };
        });

        var context = SnsManagementClientTestSupport.NewContext();
        await SubscribeHandler.HandleAsync(
            context,
            NewParseResult(("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"), ("Protocol", protocol), ("Endpoint", endpoint)),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = SnsManagementClientTestSupport.ReadBody(context);
        var subscriptionArn = SnsManagementClientTestSupport.ReadElementValue(body, "SubscriptionArn");
        Assert.Equal(
            "arn:aws:sns:us-west-2:000000000000:orders:" + SnsSubscriptionSupport.CreateSubscriptionId("arn:aws:sns:us-west-2:000000000000:orders", protocol, endpoint),
            subscriptionArn);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("sms")]
    [InlineData("lambda")]
    public async Task HandleAsync_rejects_unsupported_protocols(string protocol)
    {
        var context = SnsManagementClientTestSupport.NewContext();

        await SubscribeHandler.HandleAsync(
            context,
            NewParseResult(("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"), ("Protocol", protocol), ("Endpoint", "value")),
            SnsManagementClientTestSupport.NewCredentials(),
            SnsManagementClientTestSupport.NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called.")),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidParameter", SnsManagementClientTestSupport.ReadBody(context));
    }

    [Theory]
    [InlineData("Protocol")]
    [InlineData("Endpoint")]
    [InlineData("TopicArn")]
    public async Task HandleAsync_rejects_missing_required_parameters(string missingParameter)
    {
        var parameters = new List<(string Key, string Value)>
        {
            ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"),
            ("Protocol", "https"),
            ("Endpoint", "https://example.com")
        };
        parameters.RemoveAll(pair => pair.Key == missingParameter);

        var context = SnsManagementClientTestSupport.NewContext();
        await SubscribeHandler.HandleAsync(
            context,
            NewParseResult(parameters.ToArray()),
            SnsManagementClientTestSupport.NewCredentials(),
            SnsManagementClientTestSupport.NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called.")),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains(missingParameter, SnsManagementClientTestSupport.ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_is_idempotent_for_re_subscribe()
    {
        const string topicArn = "arn:aws:sns:us-west-2:000000000000:orders";
        const string endpoint = "https://example.com/hooks/orders";
        const string protocol = "https";
        var subscriptionId = SnsSubscriptionSupport.CreateSubscriptionId(topicArn, protocol, endpoint);
        var requests = 0;
        var metadata = SnsManagementClientTestSupport.SerializeMetadata(protocol, endpoint);
        var managementClient = SnsManagementClientTestSupport.NewManagementClient((request, _) =>
        {
            requests++;
            if (requests == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
            }

            if (requests == 2)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SnsManagementClientTestSupport.BuildSubscriptionsFeed((subscriptionId, metadata)), Encoding.UTF8, "application/atom+xml"),
            });
        });

        var firstContext = SnsManagementClientTestSupport.NewContext();
        await SubscribeHandler.HandleAsync(firstContext, NewParseResult(("TopicArn", topicArn), ("Protocol", protocol), ("Endpoint", endpoint)), SnsManagementClientTestSupport.NewCredentials(), managementClient, NullLogger.Instance, CancellationToken.None);
        var secondContext = SnsManagementClientTestSupport.NewContext();
        await SubscribeHandler.HandleAsync(secondContext, NewParseResult(("TopicArn", topicArn), ("Protocol", protocol), ("Endpoint", endpoint)), SnsManagementClientTestSupport.NewCredentials(), managementClient, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(
            SnsManagementClientTestSupport.ReadElementValue(SnsManagementClientTestSupport.ReadBody(firstContext), "SubscriptionArn"),
            SnsManagementClientTestSupport.ReadElementValue(SnsManagementClientTestSupport.ReadBody(secondContext), "SubscriptionArn"));
    }

    [Fact]
    public void SubscriptionId_is_deterministic()
    {
        var first = SnsSubscriptionSupport.CreateSubscriptionId("arn:aws:sns:us-west-2:000000000000:orders", "https", "https://example.com/a");
        var second = SnsSubscriptionSupport.CreateSubscriptionId("arn:aws:sns:us-west-2:000000000000:orders", "https", "https://example.com/a");
        var different = SnsSubscriptionSupport.CreateSubscriptionId("arn:aws:sns:us-west-2:000000000000:orders", "https", "https://example.com/b");

        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
        Assert.Equal(20, first.Length);
    }

    [Fact]
    public async Task HandleAsync_stores_filter_policy_in_user_metadata()
    {
        var capturedMetadata = string.Empty;
        var managementClient = SnsManagementClientTestSupport.NewManagementClient(async (request, _) =>
        {
            capturedMetadata = SnsManagementClientTestSupport.ReadElementValue(await request.Content!.ReadAsStringAsync().ConfigureAwait(false), "UserMetadata");
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        var context = SnsManagementClientTestSupport.NewContext();
        await SubscribeHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"),
                ("Protocol", "https"),
                ("Endpoint", "https://example.com"),
                ("Attributes.entry.1.key", "FilterPolicy"),
                ("Attributes.entry.1.value", "{\"tenant\":[\"blue\"]}"),
                ("Attributes.entry.2.key", "RawMessageDelivery"),
                ("Attributes.entry.2.value", "true")),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            NullLogger.Instance,
            CancellationToken.None);

        var metadata = JsonSerializer.Deserialize(capturedMetadata, SnsSubscriptionJsonContext.Default.SnsSubscriptionMetadata);
        Assert.NotNull(metadata);
        Assert.Equal("https", metadata!.Protocol);
        Assert.Equal("https://example.com", metadata.Endpoint);
        Assert.Equal("{\"tenant\":[\"blue\"]}", metadata.FilterPolicyJson);
        Assert.True(metadata.RawDeliveryEnabled);
    }

    private static SnsParseResult NewParseResult(params (string Key, string Value)[] pairs)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            parameters[pair.Key] = pair.Value;
        }

        return new SnsParseResult(SnsOperation.Subscribe, parameters, null);
    }
}
