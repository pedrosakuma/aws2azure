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

    [Fact]
    public async Task HandleAsync_clearing_filter_policy_also_clears_scope()
    {
        var storedMetadata = SnsManagementClientTestSupport.SerializeMetadata(
            "https",
            "https://example.com/hooks/orders",
            "{\"detail\":{\"tenant\":[\"blue\"]}}",
            filterPolicyScope: SnsSubscriptionMetadata.MessageBodyScope);
        var managementClient = NewStatefulManagementClient(() => storedMetadata, value => storedMetadata = value);

        var context = SnsManagementClientTestSupport.NewContext();
        await SetSubscriptionAttributesHandler.HandleAsync(
            context,
            NewParseResult("FilterPolicy", string.Empty),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var metadata = JsonSerializer.Deserialize(storedMetadata, SnsSubscriptionJsonContext.Default.SnsSubscriptionMetadata);
        Assert.NotNull(metadata);
        Assert.Null(metadata!.FilterPolicyJson);
        Assert.Null(metadata.FilterPolicyScope);
    }

    [Fact]
    public async Task HandleAsync_programs_service_bus_sql_rule_for_message_body_scope()
    {
        var storedMetadata = SnsManagementClientTestSupport.SerializeMetadata("https", "https://example.com/hooks/orders");
        string? ruleBody = null;
        var managementClient = SnsManagementClientTestSupport.NewManagementClient(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        SnsManagementClientTestSupport.BuildSubscriptionEntry("sub123", storedMetadata),
                        Encoding.UTF8,
                        "application/atom+xml"),
                };
            }

            if (request.RequestUri!.AbsoluteUri.Contains("/rules/", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Delete)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                ruleBody = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var body = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
            storedMetadata = SnsManagementClientTestSupport.ReadElementValue(body, "UserMetadata");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var context = SnsManagementClientTestSupport.NewContext();
        await SetSubscriptionAttributesHandler.HandleAsync(
            context,
            NewParseResult("FilterPolicyScope", "MessageBody"),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        context = SnsManagementClientTestSupport.NewContext();
        await SetSubscriptionAttributesHandler.HandleAsync(
            context,
            NewParseResult("FilterPolicy", "{ \"detail\" : { \"tenant\" : [ \"blue\" ] } }"),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotNull(ruleBody);
        Assert.Contains("SqlFilter", ruleBody);
        Assert.Contains("aws2azure_sns_body_363a64657461696c7c363a74656e616e74", ruleBody);
        Assert.Contains("'blue'", ruleBody);
        Assert.Contains("<Name>aws2azure</Name>", ruleBody);
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
                            lockDuration: "PT1M",
                            maxDeliveryCount: 17,
                            additionalPropertiesXml:
                                "<RequiresSession>true</RequiresSession>"
                                + "<EnableBatchedOperations>false</EnableBatchedOperations>"
                                + "<DeadLetteringOnMessageExpiration>true</DeadLetteringOnMessageExpiration>"
                                + "<ForwardTo>archive</ForwardTo>"
                                + "<CreatedAt>2026-07-22T00:00:00Z</CreatedAt>"
                                + "<UpdatedAt>2026-07-22T00:00:01Z</UpdatedAt>"
                                + "<AccessedAt>2026-07-22T00:00:02Z</AccessedAt>"
                                + "<MessageCount>42</MessageCount>"
                                + "<SizeInBytes>1024</SizeInBytes>"
                                + "<CountDetails><ActiveMessageCount>42</ActiveMessageCount></CountDetails>"
                                + "<EntityAvailabilityStatus>Available</EntityAvailabilityStatus>"
                                + "<SkippedUpdate>1</SkippedUpdate>"
                                + "<DefaultRuleDescription><Name>$Default</Name></DefaultRuleDescription>"),
                        Encoding.UTF8,
                        "application/atom+xml"),
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"etag-unrelated\"");
                return response;
            }

            if (request.RequestUri!.AbsoluteUri.Contains("/rules/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("*", Assert.Single(request.Headers.GetValues("If-Match")));
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
        Assert.Contains("PT1M</LockDuration>", updateBody);
        Assert.Contains(">17</MaxDeliveryCount>", updateBody);
        Assert.Contains("<RequiresSession", updateBody);
        Assert.Contains(">true</RequiresSession>", updateBody);
        Assert.Contains("<EnableBatchedOperations", updateBody);
        Assert.Contains(">false</EnableBatchedOperations>", updateBody);
        Assert.Contains("<DeadLetteringOnMessageExpiration", updateBody);
        Assert.Contains("<ForwardTo", updateBody);
        Assert.Contains(">archive</ForwardTo>", updateBody);
        Assert.DoesNotContain("<CreatedAt", updateBody);
        Assert.DoesNotContain("<UpdatedAt", updateBody);
        Assert.DoesNotContain("<AccessedAt", updateBody);
        Assert.DoesNotContain("<MessageCount", updateBody);
        Assert.DoesNotContain("<SizeInBytes", updateBody);
        Assert.DoesNotContain("<CountDetails", updateBody);
        Assert.DoesNotContain("<EntityAvailabilityStatus", updateBody);
        Assert.DoesNotContain("<SkippedUpdate", updateBody);
        Assert.DoesNotContain("<DefaultRuleDescription", updateBody);
        Assert.True(JsonSerializer.Deserialize(
            SnsManagementClientTestSupport.ReadElementValue(updateBody, "UserMetadata"),
            SnsSubscriptionJsonContext.Default.SnsSubscriptionMetadata)!.RawDeliveryEnabled);
    }

    [Fact]
    public async Task HandleAsync_maps_backend_precondition_failure_to_concurrent_access()
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

    [Fact]
    public async Task HandleAsync_updates_existing_custom_rule_when_filter_is_readded_without_default_rule()
    {
        var storedMetadata = SnsManagementClientTestSupport.SerializeMetadata("https", "https://example.com/hooks/orders");
        var rulePutIfMatch = string.Empty;
        var defaultRuleDeletes = 0;
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
                            additionalPropertiesXml: "<DefaultRuleDescription><Name>$Default</Name></DefaultRuleDescription>"),
                        Encoding.UTF8,
                        "application/atom+xml"),
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"etag-readd\"");
                return response;
            }

            if (request.RequestUri!.AbsoluteUri.Contains("/rules/", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Delete)
                {
                    defaultRuleDeletes++;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                rulePutIfMatch = Assert.Single(request.Headers.IfMatch).ToString();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var body = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
            storedMetadata = SnsManagementClientTestSupport.ReadElementValue(body, "UserMetadata");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var context = SnsManagementClientTestSupport.NewContext();
        await SetSubscriptionAttributesHandler.HandleAsync(
            context,
            NewParseResult("FilterPolicy", "{ \"tenant\" : [ \"blue\" ] }"),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("*", rulePutIfMatch);
        Assert.Equal(1, defaultRuleDeletes);
    }

    [Fact]
    public async Task HandleAsync_does_not_add_pass_through_rule_when_default_rule_delete_fails()
    {
        var originalMetadata = SnsManagementClientTestSupport.SerializeMetadata("https", "https://example.com/hooks/orders");
        var storedMetadata = originalMetadata;
        var rulePutRequests = 0;
        var defaultRuleDeleteRequests = 0;
        var customRuleDeleteRequests = 0;
        var ifMatchValues = new List<string>();
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
                            additionalPropertiesXml: "<DefaultRuleDescription><Name>$Default</Name></DefaultRuleDescription>"),
                        Encoding.UTF8,
                        "application/atom+xml"),
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"etag-rollback\"");
                return response;
            }

            if (request.RequestUri!.AbsoluteUri.Contains("/rules/", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Delete)
                {
                    if (request.RequestUri.AbsoluteUri.Contains("/rules/%24Default", StringComparison.Ordinal))
                    {
                        defaultRuleDeleteRequests++;
                        return new HttpResponseMessage(HttpStatusCode.Forbidden);
                    }

                    customRuleDeleteRequests++;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                rulePutRequests++;
                if (request.Headers.IfMatch.Count > 0)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            ifMatchValues.Add(Assert.Single(request.Headers.GetValues("If-Match")));
            var body = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
            storedMetadata = SnsManagementClientTestSupport.ReadElementValue(body, "UserMetadata");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var context = SnsManagementClientTestSupport.NewContext();
        await SetSubscriptionAttributesHandler.HandleAsync(
            context,
            NewParseResult("FilterPolicy", "{ \"tenant\" : [ \"blue\" ] }"),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal(3, defaultRuleDeleteRequests);
        Assert.Equal(1, customRuleDeleteRequests);
        Assert.Equal(2, rulePutRequests);
        Assert.Equal(originalMetadata, storedMetadata);
        Assert.Equal(new[] { "*", "*" }, ifMatchValues);
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

            if (request.RequestUri!.AbsoluteUri.Contains("/rules/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("*", Assert.Single(request.Headers.GetValues("If-Match")));
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
