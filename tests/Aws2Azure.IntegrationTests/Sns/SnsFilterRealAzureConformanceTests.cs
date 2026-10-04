using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Azure.Messaging.ServiceBus;
using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.IntegrationTests.Sns;

[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class SnsFilterRealAzureConformanceTests(
    RealAzureProxyFixture fixture,
    ITestOutputHelper output)
{
    [SkippableFact]
    public Task Equals_ignore_case_routes_only_matching_messages_against_real_service_bus()
        => VerifyAllPublishPathsAsync("equals-ignore-case", "Blue",
        [
            new("different", "green", false),
            new("missing", null, false),
            new("lower", "blue", true),
            new("upper", "BLUE", true),
            new("mixed", "bLuE", true),
        ]);

    [SkippableFact]
    public Task Ipv4_cidr_routes_only_matching_messages_against_real_service_bus()
        => VerifyAllPublishPathsAsync("cidr", "10.0.0.0/24",
        [
            new("below", "9.255.255.255", false),
            new("above", "10.0.1.0", false),
            new("not-ip", "not-an-ip", false),
            new("missing", null, false),
            new("lower", "10.0.0.0", true),
            new("inside", "10.0.0.42", true),
            new("upper", "10.0.0.255", true),
        ]);

    private async Task VerifyAllPublishPathsAsync(
        string filterOperator,
        string operand,
        FilterCase[] cases)
    {
        Skip.IfNot(fixture.SnsConfigured, "AZURE_SB_CONNSTR not set - skipping real-Azure SNS filters.");

        // Each matrix identity passes only after all four scope/publish combinations pass.
        foreach (var scope in new[] { "MessageAttributes", "MessageBody" })
        {
            foreach (var batch in new[] { false, true })
            {
                output.WriteLine($"Filter={filterOperator}; scope={scope}; action={(batch ? "PublishBatch" : "Publish")}");
                await VerifyRoutingAsync(filterOperator, operand, cases, scope, batch).ConfigureAwait(false);
            }
        }
    }

    private async Task VerifyRoutingAsync(
        string filterOperator,
        string operand,
        FilterCase[] cases,
        string scope,
        bool batch)
    {
        using var client = fixture.CreateSnsClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var topicName = SnsQueryApiClient.CreateTopicName("sns-real-filter");
        var create = await SendAsync(client, "CreateTopic", [new("Name", topicName)]).ConfigureAwait(false);
        SnsServiceBusTestSupport.AssertStatus(create, HttpStatusCode.OK, "CreateTopic");
        var topicArn = SnsQueryApiClient.ReadTopicArn(create);

        try
        {
            var filteredArn = await SubscribeAsync(client, topicArn).ConfigureAwait(false);
            var controlArn = await SubscribeAsync(client, topicArn).ConfigureAwait(false);
            var setScope = await SendAsync(client, "SetSubscriptionAttributes",
            [
                new("SubscriptionArn", filteredArn),
                new("AttributeName", "FilterPolicyScope"),
                new("AttributeValue", scope),
            ]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(setScope, HttpStatusCode.OK, "SetSubscriptionAttributes[scope]");

            var policy = new JsonObject
            {
                ["value"] = new JsonArray(new JsonObject { [filterOperator] = operand }),
            };
            if (scope == "MessageBody")
            {
                policy = new JsonObject { ["detail"] = policy };
            }

            var setPolicy = await SendAsync(client, "SetSubscriptionAttributes",
            [
                new("SubscriptionArn", filteredArn),
                new("AttributeName", "FilterPolicy"),
                new("AttributeValue", policy.ToJsonString()),
            ]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(setPolicy, HttpStatusCode.OK, "SetSubscriptionAttributes[policy]");

            var bodies = cases.ToDictionary(
                item => item.Id,
                item =>
                {
                    var detail = new JsonObject();
                    if (scope == "MessageBody" && item.Value is not null)
                    {
                        detail["value"] = item.Value;
                    }

                    return new JsonObject { ["caseId"] = item.Id, ["detail"] = detail }.ToJsonString();
                },
                StringComparer.Ordinal);
            var messageIds = await PublishAsync(client, topicArn, cases, bodies, scope, batch).ConfigureAwait(false);
            Assert.Equal(cases.Length, messageIds.Values.Distinct(StringComparer.Ordinal).Count());

            await using var serviceBus = new ServiceBusClient(fixture.CreateServiceBusConnectionString());
            await using var filtered = serviceBus.CreateReceiver(
                topicName, SnsQueryApiClient.ExtractSubscriptionName(filteredArn));
            await using var control = serviceBus.CreateReceiver(
                topicName, SnsQueryApiClient.ExtractSubscriptionName(controlArn));

            // The unfiltered subscription proves that even the rejected inputs reached the broker.
            await AssertDeliveryAsync(control, cases, bodies, messageIds).ConfigureAwait(false);
            await AssertDeliveryAsync(
                filtered, cases.Where(item => item.Matches), bodies, messageIds).ConfigureAwait(false);
            await Task.WhenAll(
                SnsServiceBusTestSupport.AssertNoMessagesAsync(filtered, TimeSpan.FromSeconds(5)),
                SnsServiceBusTestSupport.AssertNoMessagesAsync(control, TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }
        finally
        {
            var delete = await SendAsync(client, "DeleteTopic", [new("TopicArn", topicArn)]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(delete, HttpStatusCode.OK, "DeleteTopic[cleanup]");
        }
    }

    private static async Task<Dictionary<string, string>> PublishAsync(
        HttpClient client,
        string topicArn,
        FilterCase[] cases,
        IReadOnlyDictionary<string, string> bodies,
        string scope,
        bool batch)
    {
        var messageIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var parameters = new List<KeyValuePair<string, string>> { new("TopicArn", topicArn) };
        for (var i = 0; i < cases.Length; i++)
        {
            var item = cases[i];
            var prefix = batch ? $"PublishBatchRequestEntries.member.{i + 1}." : string.Empty;
            if (batch)
            {
                parameters.Add(new(prefix + "Id", item.Id));
            }

            parameters.Add(new(prefix + "Message", bodies[item.Id]));
            if (scope == "MessageAttributes" && item.Value is not null)
            {
                parameters.Add(new(prefix + "MessageAttributes.entry.1.Name", "value"));
                parameters.Add(new(prefix + "MessageAttributes.entry.1.Value.DataType", "String"));
                parameters.Add(new(prefix + "MessageAttributes.entry.1.Value.StringValue", item.Value));
            }

            if (!batch)
            {
                var response = await SendAsync(client, "Publish", parameters).ConfigureAwait(false);
                SnsServiceBusTestSupport.AssertStatus(response, HttpStatusCode.OK, $"Publish[{item.Id}]");
                messageIds.Add(item.Id, SnsQueryApiClient.ReadMessageId(response));
                parameters = [new("TopicArn", topicArn)];
            }
        }

        if (batch)
        {
            var response = await SendAsync(client, "PublishBatch", parameters).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(response, HttpStatusCode.OK, "PublishBatch");
            Assert.Empty(SnsQueryApiClient.ReadPublishBatchFailures(response));
            Assert.Equal(
                cases.Select(item => item.Id).Order(StringComparer.Ordinal),
                SnsQueryApiClient.ReadPublishBatchSuccessIds(response).Order(StringComparer.Ordinal));
            Assert.NotNull(response.Xml);
            var ns = response.Xml.Root!.Name.Namespace;
            foreach (var member in response.Xml.Descendants(ns + "Successful").Elements(ns + "member"))
            {
                var id = member.Element(ns + "Id");
                var messageId = member.Element(ns + "MessageId");
                Assert.NotNull(id);
                Assert.NotNull(messageId);
                messageIds.Add(id.Value, messageId.Value);
            }
        }

        Assert.Equal(cases.Length, messageIds.Count);
        Assert.All(messageIds.Values, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        return messageIds;
    }

    private static async Task AssertDeliveryAsync(
        ServiceBusReceiver receiver,
        IEnumerable<FilterCase> expectedCases,
        IReadOnlyDictionary<string, string> bodies,
        IReadOnlyDictionary<string, string> messageIds)
    {
        var expected = expectedCases.ToDictionary(item => messageIds[item.Id], item => bodies[item.Id]);
        var messages = await SnsServiceBusTestSupport.ReceiveMessagesAsync(
            receiver, expected.Count, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        Assert.Equal(
            expected.Keys.Order(StringComparer.Ordinal),
            messages.Select(message => message.MessageId).Order(StringComparer.Ordinal));
        foreach (var message in messages)
        {
            Assert.Equal(expected[message.MessageId], message.Body.ToString());
        }

        await SnsServiceBusTestSupport.CompleteMessagesAsync(receiver, messages).ConfigureAwait(false);
    }

    private static async Task<string> SubscribeAsync(HttpClient client, string topicArn)
    {
        var response = await SendAsync(client, "Subscribe",
        [
            new("TopicArn", topicArn),
            new("Protocol", "sqs"),
            new("Endpoint", SnsQueryApiClient.CreateSubscriptionEndpoint()),
        ]).ConfigureAwait(false);
        SnsServiceBusTestSupport.AssertStatus(response, HttpStatusCode.OK, "Subscribe");
        return SnsQueryApiClient.ReadSubscriptionArn(response);
    }

    private static Task<SnsXmlResponse> SendAsync(
        HttpClient client,
        string action,
        IEnumerable<KeyValuePair<string, string>> parameters)
        => SnsQueryApiClient.SendActionAsync(
            client, action, parameters, RealAzureProxyFixture.AwsAccessKey, RealAzureProxyFixture.AwsSecret);

    private sealed record FilterCase(string Id, string? Value, bool Matches);
}
