using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Sns;

public sealed class SetTopicAttributesHandlerTests
{
    [Theory]
    [InlineData("EffectiveDeliveryPolicy")]
    [InlineData("KmsMasterKeyId")]
    [InlineData("SignatureVersion")]
    [InlineData("TracingConfig")]
    public async Task HandleAsync_returns_success_for_remaining_no_op_attributes(string attributeName)
    {
        var context = SnsManagementClientTestSupport.NewContext();
        await SetTopicAttributesHandler.HandleAsync(
            context,
            NewParseResult(attributeName, string.Empty),
            SnsManagementClientTestSupport.NewCredentials(),
            SnsManagementClientTestSupport.NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called.")),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("<SetTopicAttributesResponse", SnsManagementClientTestSupport.ReadBody(context));
    }

    [Theory]
    [InlineData("DisplayName", "Orders")]
    [InlineData("Policy", "{ \"Statement\": [] }")]
    [InlineData("DeliveryPolicy", "{ \"healthyRetryPolicy\": { \"numRetries\": 3 } }")]
    public async Task HandleAsync_updates_metadata_backed_topic_attributes(string attributeName, string attributeValue)
    {
        string? updateBody = null;
        var managementClient = SnsManagementClientTestSupport.NewManagementClient(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        SnsManagementClientTestSupport.BuildTopicEntry(
                            "orders",
                            subscriptionCount: 2,
                            requiresDuplicateDetection: false,
                            userMetadata: JsonSerializer.Serialize(
                                new SnsTopicMetadata
                                {
                                    DisplayName = "Old",
                                    PolicyJson = "{\"Statement\":[{\"Sid\":\"old\"}]}",
                                },
                                SnsTopicJsonContext.Default.SnsTopicMetadata),
                            additionalPropertiesXml:
                                "<DefaultMessageTimeToLive>P14D</DefaultMessageTimeToLive>"
                                + "<EnableBatchedOperations>true</EnableBatchedOperations>"
                                + "<CreatedAt>2026-07-22T00:00:00Z</CreatedAt>"
                                + "<UpdatedAt>2026-07-22T00:00:01Z</UpdatedAt>"
                                + "<CountDetails><ActiveMessageCount>0</ActiveMessageCount></CountDetails>"),
                        Encoding.UTF8,
                        "application/atom+xml"),
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"etag-topic\"");
                return response;
            }

            updateBody = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("*", Assert.Single(request.Headers.GetValues("If-Match")));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var context = SnsManagementClientTestSupport.NewContext();
        await SetTopicAttributesHandler.HandleAsync(
            context,
            NewParseResult(attributeName, attributeValue),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotNull(updateBody);
        var metadata = JsonSerializer.Deserialize(
            SnsManagementClientTestSupport.ReadElementValue(updateBody, "UserMetadata"),
            SnsTopicJsonContext.Default.SnsTopicMetadata);
        Assert.NotNull(metadata);
        Assert.Equal(attributeName == "DisplayName" ? "Orders" : "Old", metadata!.DisplayName);
        Assert.Equal(attributeName == "Policy" ? "{\"Statement\":[]}" : "{\"Statement\":[{\"Sid\":\"old\"}]}", metadata.PolicyJson);
        Assert.Equal(attributeName == "DeliveryPolicy" ? "{\"healthyRetryPolicy\":{\"numRetries\":3}}" : null, metadata.DeliveryPolicyJson);
    }

    [Fact]
    public async Task HandleAsync_rejects_content_based_deduplication_changes()
    {
        var requests = 0;
        var managementClient = SnsManagementClientTestSupport.NewManagementClient((request, _) =>
        {
            requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SnsManagementClientTestSupport.BuildTopicEntry("orders", subscriptionCount: 1, requiresDuplicateDetection: false), Encoding.UTF8, "application/atom+xml"),
            });
        });

        var context = SnsManagementClientTestSupport.NewContext();
        await SetTopicAttributesHandler.HandleAsync(
            context,
            NewParseResult("ContentBasedDeduplication", "true"),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(1, requests);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("cannot be changed after the Service Bus topic has been created", SnsManagementClientTestSupport.ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_updates_fifo_throughput_scope_metadata()
    {
        const string topicName = "scope-update.fifo";
        string? updateBody = null;
        var credentials = SnsManagementClientTestSupport.NewCredentials();
        SnsFifoPublishSupport.RecordServiceBusTopicState(
            credentials,
            topicName,
            requiresDuplicateDetection: true,
            contentBasedDeduplication: false,
            SnsTopicAttributeSupport.TopicFifoThroughputScope);
        var managementClient = SnsManagementClientTestSupport.NewManagementClient(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        SnsManagementClientTestSupport.BuildTopicEntry(
                            topicName,
                            requiresDuplicateDetection: true),
                        Encoding.UTF8,
                        "application/atom+xml"),
                };
            }

            updateBody = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var context = SnsManagementClientTestSupport.NewContext();
        await SetTopicAttributesHandler.HandleAsync(
            context,
            NewParseResult("FifoThroughputScope", "MessageGroup", topicName),
            credentials,
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var metadata = JsonSerializer.Deserialize(
            SnsManagementClientTestSupport.ReadElementValue(updateBody!, "UserMetadata"),
            SnsTopicJsonContext.Default.SnsTopicMetadata);
        Assert.Equal("MessageGroup", metadata!.FifoThroughputScope);
        Assert.False(metadata.ContentBasedDeduplication);
        Assert.True(SnsFifoPublishSupport.TryGetCachedServiceBusTopicState(credentials, topicName, out var cachedState));
        Assert.False(cachedState.ContentBasedDeduplication);
        Assert.Equal(SnsTopicAttributeSupport.MessageGroupFifoThroughputScope, cachedState.FifoThroughputScope);
    }

    [Fact]
    public void Topic_update_inserts_missing_user_metadata_before_support_ordering()
    {
        const string serviceBusNamespace = "http://schemas.microsoft.com/netservices/2010/10/servicebus/connect";
        var description = new ServiceBusTopicDescription(
            "orders.fifo",
            SubscriptionCount: 0,
            RequiresDuplicateDetection: true,
            UserMetadata: "{\"fifoThroughputScope\":\"Topic\"}",
            Properties:
            [
                new("DefaultMessageTimeToLive", $"<DefaultMessageTimeToLive xmlns=\"{serviceBusNamespace}\">P14D</DefaultMessageTimeToLive>"),
                new("MaxSizeInMegabytes", $"<MaxSizeInMegabytes xmlns=\"{serviceBusNamespace}\">1024</MaxSizeInMegabytes>"),
                new("RequiresDuplicateDetection", $"<RequiresDuplicateDetection xmlns=\"{serviceBusNamespace}\">true</RequiresDuplicateDetection>"),
                new("DuplicateDetectionHistoryTimeWindow", $"<DuplicateDetectionHistoryTimeWindow xmlns=\"{serviceBusNamespace}\">PT5M</DuplicateDetectionHistoryTimeWindow>"),
                new("EnableBatchedOperations", $"<EnableBatchedOperations xmlns=\"{serviceBusNamespace}\">true</EnableBatchedOperations>"),
                new("SupportOrdering", $"<SupportOrdering xmlns=\"{serviceBusNamespace}\">false</SupportOrdering>"),
                new("AutoDeleteOnIdle", $"<AutoDeleteOnIdle xmlns=\"{serviceBusNamespace}\">P10675199DT2H48M5.4775807S</AutoDeleteOnIdle>"),
            ]);

        var body = ServiceBusAtomXml.BuildTopicDescriptionEntry(description);

        var userMetadataIndex = body.IndexOf("<UserMetadata", StringComparison.Ordinal);
        var supportOrderingIndex = body.IndexOf("<SupportOrdering", StringComparison.Ordinal);
        Assert.True(userMetadataIndex >= 0);
        Assert.True(supportOrderingIndex > userMetadataIndex);
    }

    [Theory]
    [InlineData("orders.fifo", "invalid", "must be either 'Topic' or 'MessageGroup'")]
    [InlineData("orders", "MessageGroup", "supported only for FIFO topics")]
    public async Task HandleAsync_rejects_invalid_fifo_throughput_scope(
        string topicName,
        string attributeValue,
        string expectedError)
    {
        var managementClient = SnsManagementClientTestSupport.NewManagementClient((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    SnsManagementClientTestSupport.BuildTopicEntry(
                        topicName,
                        requiresDuplicateDetection: topicName.EndsWith(".fifo", StringComparison.Ordinal)),
                    Encoding.UTF8,
                    "application/atom+xml"),
            });
        });

        var context = SnsManagementClientTestSupport.NewContext();
        await SetTopicAttributesHandler.HandleAsync(
            context,
            NewParseResult("FifoThroughputScope", attributeValue, topicName),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains(expectedError, SnsManagementClientTestSupport.ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_unknown_attribute_names()
    {
        var context = SnsManagementClientTestSupport.NewContext();
        await SetTopicAttributesHandler.HandleAsync(
            context,
            NewParseResult("UnknownAttribute", "value"),
            SnsManagementClientTestSupport.NewCredentials(),
            SnsManagementClientTestSupport.NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called.")),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("Invalid attribute name: UnknownAttribute", SnsManagementClientTestSupport.ReadBody(context));
    }

    private static SnsParseResult NewParseResult(
        string attributeName,
        string attributeValue,
        string topicName = "orders")
        => new(
            SnsOperation.SetTopicAttributes,
            new Dictionary<string, string>
            {
                ["TopicArn"] = $"arn:aws:sns:us-west-2:000000000000:{topicName}",
                ["AttributeName"] = attributeName,
                ["AttributeValue"] = attributeValue,
            },
            null);
}
