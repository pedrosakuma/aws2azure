using System.Net;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Sns;

public sealed class SetSubscriptionAttributesHandlerTests
{
    [Fact]
    public async Task HandleAsync_round_trips_filter_policy_into_get_subscription_attributes()
    {
        var storedMetadata = SnsManagementClientTestSupport.SerializeMetadata("https", "https://example.com/hooks/orders");
        var managementClient = NewStatefulManagementClient(() => storedMetadata, value => storedMetadata = value);

        var setContext = SnsManagementClientTestSupport.NewContext();
        await SetSubscriptionAttributesHandler.HandleAsync(
            setContext,
            NewParseResult("FilterPolicy", "{ \"tenant\" : [ \"blue\" ] }"),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, setContext.Response.StatusCode);

        var getContext = SnsManagementClientTestSupport.NewContext();
        await GetSubscriptionAttributesHandler.HandleAsync(
            getContext,
            new SnsParseResult(SnsOperation.GetSubscriptionAttributes, new Dictionary<string, string> { ["SubscriptionArn"] = SubscriptionArn }, null),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        var attributes = SnsManagementClientTestSupport.ReadAttributes(SnsManagementClientTestSupport.ReadBody(getContext));
        Assert.Equal("{\"tenant\":[\"blue\"]}", attributes["FilterPolicy"]);
        Assert.Equal("MessageAttributes", attributes["FilterPolicyScope"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleAsync_updates_raw_message_delivery(bool enabled)
    {
        var storedMetadata = SnsManagementClientTestSupport.SerializeMetadata("https", "https://example.com/hooks/orders", rawDeliveryEnabled: !enabled);
        var managementClient = NewStatefulManagementClient(() => storedMetadata, value => storedMetadata = value);

        var context = SnsManagementClientTestSupport.NewContext();
        await SetSubscriptionAttributesHandler.HandleAsync(
            context,
            NewParseResult("RawMessageDelivery", enabled ? "true" : "false"),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var metadata = JsonSerializer.Deserialize(storedMetadata, SnsSubscriptionJsonContext.Default.SnsSubscriptionMetadata);
        Assert.NotNull(metadata);
        Assert.Equal(enabled, metadata!.RawDeliveryEnabled);
    }

    [Fact]
    public async Task HandleAsync_rejects_invalid_filter_policy_json()
    {
        var storedMetadata = SnsManagementClientTestSupport.SerializeMetadata("https", "https://example.com/hooks/orders");
        var managementClient = NewStatefulManagementClient(() => storedMetadata, value => storedMetadata = value);

        var context = SnsManagementClientTestSupport.NewContext();
        await SetSubscriptionAttributesHandler.HandleAsync(
            context,
            NewParseResult("FilterPolicy", "{not-json}"),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("must contain valid JSON", SnsManagementClientTestSupport.ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_filter_policies_that_exceed_user_metadata_limit()
    {
        var storedMetadata = SnsManagementClientTestSupport.SerializeMetadata("https", "https://example.com/hooks/orders");
        var managementClient = NewStatefulManagementClient(() => storedMetadata, value => storedMetadata = value);
        var oversizedPolicy = "{\"tenant\":[\"" + new string('a', 1100) + "\"]}";

        var context = SnsManagementClientTestSupport.NewContext();
        await SetSubscriptionAttributesHandler.HandleAsync(
            context,
            NewParseResult("FilterPolicy", oversizedPolicy),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("UserMetadata limit", SnsManagementClientTestSupport.ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_unknown_attribute_names()
    {
        var context = SnsManagementClientTestSupport.NewContext();
        await SetSubscriptionAttributesHandler.HandleAsync(
            context,
            NewParseResult("Nope", "value"),
            SnsManagementClientTestSupport.NewCredentials(),
            SnsManagementClientTestSupport.NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called.")),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("Invalid attribute name: Nope", SnsManagementClientTestSupport.ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_preserves_unrelated_service_bus_properties_and_uses_etag()
    {
        var storedMetadata = SnsManagementClientTestSupport.SerializeMetadata("https", "https://example.com/hooks/orders");
        string? updateBody = null;
        var managementClient = SnsManagementClientTestSupport.NewManagementClient(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        SnsManagementClientTestSupport.BuildSubscriptionEntry(
                            "sub123",
                            storedMetadata,
                            additionalPropertiesXml:
                                "<RequiresSession>true</RequiresSession>"
                                + "<DeadLetteringOnMessageExpiration>true</DeadLetteringOnMessageExpiration>"
                                + "<ForwardTo>archive</ForwardTo>"),
                        Encoding.UTF8,
                        "application/atom+xml"),
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"etag-42\"");
                return response;
            }

            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("\"etag-42\"", Assert.Single(request.Headers.GetValues("If-Match")));
            updateBody = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var context = SnsManagementClientTestSupport.NewContext();
        await SetSubscriptionAttributesHandler.HandleAsync(
            context,
            NewParseResult("RawMessageDelivery", "true"),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotNull(updateBody);
        Assert.Contains("<RequiresSession", updateBody);
        Assert.Contains(">true</RequiresSession>", updateBody);
        Assert.Contains("<DeadLetteringOnMessageExpiration", updateBody);
        Assert.Contains("<ForwardTo", updateBody);
        Assert.Contains(">archive</ForwardTo>", updateBody);
        Assert.True(JsonSerializer.Deserialize(
            SnsManagementClientTestSupport.ReadElementValue(updateBody, "UserMetadata"),
            SnsSubscriptionJsonContext.Default.SnsSubscriptionMetadata)!.RawDeliveryEnabled);
    }

    [Fact]
    public async Task HandleAsync_maps_etag_conflict_to_concurrent_access()
    {
        var storedMetadata = SnsManagementClientTestSupport.SerializeMetadata("https", "https://example.com/hooks/orders");
        var managementClient = SnsManagementClientTestSupport.NewManagementClient((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        SnsManagementClientTestSupport.BuildSubscriptionEntry("sub123", storedMetadata),
                        Encoding.UTF8,
                        "application/atom+xml"),
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"etag-stale\"");
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
        });

        var context = SnsManagementClientTestSupport.NewContext();
        await SetSubscriptionAttributesHandler.HandleAsync(
            context,
            NewParseResult("RawMessageDelivery", "true"),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Contains("<Code>ConcurrentAccess</Code>", SnsManagementClientTestSupport.ReadBody(context));
    }

    private static ServiceBusTopicsManagementClient NewStatefulManagementClient(Func<string> getMetadata, Action<string> setMetadata)
        => SnsManagementClientTestSupport.NewManagementClient(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        SnsManagementClientTestSupport.BuildSubscriptionEntry(
                            "sub123",
                            getMetadata(),
                            lockDuration: "PT1M",
                            maxDeliveryCount: 20,
                            autoDeleteOnIdle: ServiceBusTopicsManagementClient.LongIdleIso8601),
                        Encoding.UTF8,
                        "application/atom+xml"),
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"etag-stateful\"");
                return response;
            }

            Assert.Equal(HttpMethod.Put, request.Method);
            var body = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
            setMetadata(SnsManagementClientTestSupport.ReadElementValue(body, "UserMetadata"));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    private static SnsParseResult NewParseResult(string attributeName, string attributeValue)
        => new(
            SnsOperation.SetSubscriptionAttributes,
            new Dictionary<string, string>
            {
                ["SubscriptionArn"] = SubscriptionArn,
                ["AttributeName"] = attributeName,
                ["AttributeValue"] = attributeValue,
            },
            null);

    private const string SubscriptionArn = "arn:aws:sns:us-west-2:000000000000:orders:sub123";
}
