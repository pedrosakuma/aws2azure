using System.Net;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sns;

[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class SnsRealAzureConformanceTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task Topic_subscription_lifecycle_round_trips_against_real_service_bus()
    {
        Skip.IfNot(fixture.SnsConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SNS conformance.");

        using var client = fixture.CreateSnsClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var topicName = SnsQueryApiClient.CreateTopicName("sns-real-lifecycle");
        string? topicArn = null;
        string? subscriptionArn = null;

        try
        {
            var create = await SendAsync(client, "CreateTopic", [new("Name", topicName)]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(create, HttpStatusCode.OK, "CreateTopic");
            topicArn = SnsQueryApiClient.ReadTopicArn(create);

            var endpoint = SnsQueryApiClient.CreateSubscriptionEndpoint();
            var subscribe = await SendAsync(client, "Subscribe",
            [
                new("TopicArn", topicArn),
                new("Protocol", "sqs"),
                new("Endpoint", endpoint),
            ]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(subscribe, HttpStatusCode.OK, "Subscribe");
            subscriptionArn = SnsQueryApiClient.ReadSubscriptionArn(subscribe);
            var subscriptionName = SnsQueryApiClient.ExtractSubscriptionName(subscriptionArn);

            await using var serviceBusClient = new ServiceBusClient(fixture.CreateServiceBusConnectionString());
            await using var receiver = serviceBusClient.CreateReceiver(topicName, subscriptionName);
            var body = "lifecycle-" + Guid.NewGuid().ToString("N");
            var publish = await SendAsync(client, "Publish",
            [
                new("TopicArn", topicArn),
                new("Message", body),
            ]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(publish, HttpStatusCode.OK, "Publish");

            var messages = await SnsServiceBusTestSupport.ReceiveMessagesAsync(
                receiver, expectedCount: 1, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var message = Assert.Single(messages);
            Assert.Equal(body, message.Body.ToString());
            await receiver.CompleteMessageAsync(message, timeout.Token).ConfigureAwait(false);

            var unsubscribe = await SendAsync(client, "Unsubscribe",
                [new("SubscriptionArn", subscriptionArn)]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(unsubscribe, HttpStatusCode.OK, "Unsubscribe");
            subscriptionArn = null;

            var list = await SendAsync(client, "ListSubscriptionsByTopic",
                [new("TopicArn", topicArn)]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(list, HttpStatusCode.OK, "ListSubscriptionsByTopic");
            Assert.Empty(SnsQueryApiClient.ReadListedSubscriptions(list));

            var delete = await SendAsync(client, "DeleteTopic", [new("TopicArn", topicArn)]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(delete, HttpStatusCode.OK, "DeleteTopic");
            topicArn = null;
        }
        finally
        {
            if (subscriptionArn is not null)
            {
                try
                {
                    await SendAsync(client, "Unsubscribe",
                        [new("SubscriptionArn", subscriptionArn)]).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            if (topicArn is not null)
            {
                try
                {
                    await SendAsync(client, "DeleteTopic", [new("TopicArn", topicArn)]).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    [SkippableFact]
    public async Task Subscription_metadata_and_unrelated_azure_properties_survive_restart()
    {
        Skip.IfNot(fixture.SnsConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SNS conformance.");

        using var client = fixture.CreateSnsClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var admin = new ServiceBusAdministrationClient(fixture.CreateServiceBusConnectionString());
        var topicName = SnsQueryApiClient.CreateTopicName("sns-real-submeta");
        string? topicArn = null;
        string? subscriptionArn = null;

        try
        {
            var create = await SendAsync(client, "CreateTopic", [new("Name", topicName)]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(create, HttpStatusCode.OK, "CreateTopic");
            topicArn = SnsQueryApiClient.ReadTopicArn(create);

            var endpoint = SnsQueryApiClient.CreateSubscriptionEndpoint();
            var subscribe = await SendAsync(client, "Subscribe",
            [
                new("TopicArn", topicArn),
                new("Protocol", "sqs"),
                new("Endpoint", endpoint),
                new("ReturnSubscriptionArn", "true"),
            ]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(subscribe, HttpStatusCode.OK, "Subscribe");
            subscriptionArn = SnsQueryApiClient.ReadSubscriptionArn(subscribe);
            var subscriptionName = SnsQueryApiClient.ExtractSubscriptionName(subscriptionArn);

            var azure = (await admin.GetSubscriptionAsync(topicName, subscriptionName, timeout.Token)
                .ConfigureAwait(false)).Value;
            azure.LockDuration = TimeSpan.FromMinutes(1);
            azure.MaxDeliveryCount = 17;
            azure.EnableBatchedOperations = false;
            azure.DeadLetteringOnMessageExpiration = true;
            await admin.UpdateSubscriptionAsync(azure, timeout.Token).ConfigureAwait(false);

            var setFilter = await SendAsync(client, "SetSubscriptionAttributes",
            [
                new("SubscriptionArn", subscriptionArn),
                new("AttributeName", "FilterPolicy"),
                new("AttributeValue", "{\"tenant\":[\"blue\"]}"),
            ]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(setFilter, HttpStatusCode.OK, "SetSubscriptionAttributes[FilterPolicy]");
            var setRaw = await SendAsync(client, "SetSubscriptionAttributes",
            [
                new("SubscriptionArn", subscriptionArn),
                new("AttributeName", "RawMessageDelivery"),
                new("AttributeValue", "true"),
            ]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(setRaw, HttpStatusCode.OK, "SetSubscriptionAttributes[RawMessageDelivery]");

            var afterUpdate = (await admin.GetSubscriptionAsync(topicName, subscriptionName, timeout.Token)
                .ConfigureAwait(false)).Value;
            Assert.Equal(TimeSpan.FromMinutes(1), afterUpdate.LockDuration);
            Assert.Equal(17, afterUpdate.MaxDeliveryCount);
            Assert.False(afterUpdate.EnableBatchedOperations);
            Assert.True(afterUpdate.DeadLetteringOnMessageExpiration);

            await fixture.RestartAsync().ConfigureAwait(false);

            var get = await SendAsync(client, "GetSubscriptionAttributes",
                [new("SubscriptionArn", subscriptionArn)]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(get, HttpStatusCode.OK, "GetSubscriptionAttributes[after restart]");
            var attributes = SnsQueryApiClient.ReadAttributes(get);
            Assert.Equal("sqs", attributes["Protocol"]);
            Assert.Equal(endpoint, attributes["Endpoint"]);
            Assert.Equal("{\"tenant\":[\"blue\"]}", attributes["FilterPolicy"]);
            Assert.Equal("MessageAttributes", attributes["FilterPolicyScope"]);
            Assert.Equal("true", attributes["RawMessageDelivery"]);

            var confirm = await SendAsync(client, "ConfirmSubscription",
            [
                new("TopicArn", topicArn),
                new("Token", subscriptionArn),
            ]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(confirm, HttpStatusCode.OK, "ConfirmSubscription");
            Assert.Equal(subscriptionArn, SnsQueryApiClient.ReadSubscriptionArn(confirm));

            var unsubscribe = await SendAsync(client, "Unsubscribe",
                [new("SubscriptionArn", subscriptionArn)]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(unsubscribe, HttpStatusCode.OK, "Unsubscribe");
            subscriptionArn = null;
            Assert.False((await admin.SubscriptionExistsAsync(topicName, subscriptionName, timeout.Token)
                .ConfigureAwait(false)).Value);
        }
        finally
        {
            if (subscriptionArn is not null)
            {
                try
                {
                    await SendAsync(client, "Unsubscribe",
                        [new("SubscriptionArn", subscriptionArn)]).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            if (topicArn is not null)
            {
                try
                {
                    await SendAsync(client, "DeleteTopic", [new("TopicArn", topicArn)]).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    [SkippableFact]
    public async Task Topic_and_subscription_lists_paginate()
    {
        Skip.IfNot(fixture.SnsConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SNS conformance.");

        // AWS SNS fixes both list page sizes at 100 and exposes no MaxResults.
        // 101 is therefore the minimum entity count that can produce a genuine
        // continuation token; provision directly to keep this conformance test
        // bounded and avoid spending the proxy path on setup.
        const int entityCount = 101;
        var run = Guid.NewGuid().ToString("N")[..10];
        var topicNames = Enumerable.Range(0, entityCount)
            .Select(i => $"sns-real-page-{run}-{i:D3}")
            .ToArray();
        var subscriptionNames = Enumerable.Range(0, entityCount)
            .Select(i => $"sub-{run}-{i:D3}")
            .ToArray();
        var admin = new ServiceBusAdministrationClient(fixture.CreateServiceBusConnectionString());
        using var client = fixture.CreateSnsClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));

        try
        {
            await RunBatchesAsync(
                topicNames,
                name => admin.CreateTopicAsync(name, timeout.Token),
                batchSize: 8).ConfigureAwait(false);
            await RunBatchesAsync(
                subscriptionNames,
                name => admin.CreateSubscriptionAsync(topicNames[0], name, timeout.Token),
                batchSize: 8).ConfigureAwait(false);

            var pagedTopicArn = $"arn:aws:sns:us-east-1:000000000000:{topicNames[0]}";
            var otherTopicArn = $"arn:aws:sns:us-east-1:000000000000:{topicNames[1]}";
            var firstByTopic = await SendAsync(client, "ListSubscriptionsByTopic",
                [new("TopicArn", pagedTopicArn)]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(firstByTopic, HttpStatusCode.OK, "ListSubscriptionsByTopic[first]");
            Assert.Equal(100, SnsQueryApiClient.ReadListedSubscriptions(firstByTopic).Count);
            var byTopicToken = Assert.IsType<string>(SnsQueryApiClient.ReadNextToken(firstByTopic));

            var crossTopic = await SendAsync(client, "ListSubscriptionsByTopic",
            [
                new("TopicArn", otherTopicArn),
                new("NextToken", byTopicToken),
            ]).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.BadRequest, crossTopic.StatusCode);

            var crossOperation = await SendAsync(client, "ListSubscriptions",
                [new("NextToken", byTopicToken)]).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.BadRequest, crossOperation.StatusCode);

            await fixture.RestartAsync().ConfigureAwait(false);
            var secondByTopic = await SendAsync(client, "ListSubscriptionsByTopic",
            [
                new("TopicArn", pagedTopicArn),
                new("NextToken", byTopicToken),
            ]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(secondByTopic, HttpStatusCode.OK, "ListSubscriptionsByTopic[after restart]");
            Assert.Single(SnsQueryApiClient.ReadListedSubscriptions(secondByTopic));
            Assert.Null(SnsQueryApiClient.ReadNextToken(secondByTopic));

            var listedTopics = new HashSet<string>(StringComparer.Ordinal);
            string? topicToken = null;
            var topicPages = 0;
            do
            {
                var parameters = topicToken is null
                    ? Array.Empty<KeyValuePair<string, string>>()
                    : [new("NextToken", topicToken)];
                var response = await SendAsync(client, "ListTopics", parameters).ConfigureAwait(false);
                SnsServiceBusTestSupport.AssertStatus(response, HttpStatusCode.OK, "ListTopics");
                foreach (var arn in SnsQueryApiClient.ReadTopicArns(response))
                {
                    listedTopics.Add(arn.Split(':', 6)[5]);
                }

                topicToken = SnsQueryApiClient.ReadNextToken(response);
                topicPages++;
                Assert.InRange(topicPages, 1, 4);
            } while (!string.IsNullOrWhiteSpace(topicToken));

            Assert.True(topicPages > 1);
            var expectedTopics = topicNames.ToHashSet(StringComparer.Ordinal);
            Assert.Equal(
                topicNames,
                listedTopics.Where(expectedTopics.Contains).Order(StringComparer.Ordinal).ToArray());

            var listedSubscriptions = new HashSet<string>(StringComparer.Ordinal);
            string? subscriptionToken = null;
            var subscriptionPages = 0;
            do
            {
                var parameters = subscriptionToken is null
                    ? Array.Empty<KeyValuePair<string, string>>()
                    : [new("NextToken", subscriptionToken)];

                var response = await SendAsync(client, "ListSubscriptions", parameters).ConfigureAwait(false);
                SnsServiceBusTestSupport.AssertStatus(response, HttpStatusCode.OK, "ListSubscriptions");
                foreach (var subscription in SnsQueryApiClient.ReadListedSubscriptions(response))
                {
                    listedSubscriptions.Add(SnsQueryApiClient.ExtractSubscriptionName(subscription.SubscriptionArn));
                }

                subscriptionToken = SnsQueryApiClient.ReadNextToken(response);
                subscriptionPages++;
                Assert.InRange(subscriptionPages, 1, 4);
            } while (!string.IsNullOrWhiteSpace(subscriptionToken));

            Assert.True(subscriptionPages > 1);
            var expectedSubscriptions = subscriptionNames.ToHashSet(StringComparer.Ordinal);
            Assert.Equal(
                subscriptionNames,
                listedSubscriptions.Where(expectedSubscriptions.Contains).Order(StringComparer.Ordinal).ToArray());
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await RunBatchesBestEffortAsync(
                topicNames,
                name => admin.DeleteTopicAsync(name, cleanupTimeout.Token),
                batchSize: 8).ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task PublishBatch_reports_real_service_bus_results()
    {
        Skip.IfNot(fixture.SnsConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SNS conformance.");

        using var client = fixture.CreateSnsClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var topicName = SnsQueryApiClient.CreateTopicName("sns-real-batch");
        string? topicArn = null;

        try
        {
            var create = await SendAsync(client, "CreateTopic", [new("Name", topicName)]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(create, HttpStatusCode.OK, "CreateTopic");
            topicArn = SnsQueryApiClient.ReadTopicArn(create);
            var subscribe = await SendAsync(client, "Subscribe",
            [
                new("TopicArn", topicArn),
                new("Protocol", "sqs"),
                new("Endpoint", SnsQueryApiClient.CreateSubscriptionEndpoint()),
            ]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(subscribe, HttpStatusCode.OK, "Subscribe");
            var subscriptionName = SnsQueryApiClient.ExtractSubscriptionName(
                SnsQueryApiClient.ReadSubscriptionArn(subscribe));

            await using var serviceBusClient = new ServiceBusClient(fixture.CreateServiceBusConnectionString());
            await using var receiver = serviceBusClient.CreateReceiver(topicName, subscriptionName);
            var entries = Enumerable.Range(0, 4)
                .Select(i => (Id: $"entry-{i}", Body: $"batch-{Guid.NewGuid():N}-{i}"))
                .ToArray();
            var parameters = new List<KeyValuePair<string, string>> { new("TopicArn", topicArn) };
            for (var i = 0; i < entries.Length; i++)
            {
                parameters.Add(new($"PublishBatchRequestEntries.member.{i + 1}.Id", entries[i].Id));
                parameters.Add(new($"PublishBatchRequestEntries.member.{i + 1}.Message", entries[i].Body));
            }
            parameters.Add(new("PublishBatchRequestEntries.member.5.Id", "too-big"));
            parameters.Add(new("PublishBatchRequestEntries.member.5.Message", new string('x', 270 * 1024)));

            var publish = await SendAsync(client, "PublishBatch", parameters).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(publish, HttpStatusCode.OK, "PublishBatch");
            Assert.Equal(
                entries.Select(entry => entry.Id).Order(StringComparer.Ordinal).ToArray(),
                SnsQueryApiClient.ReadPublishBatchSuccessIds(publish).Order(StringComparer.Ordinal).ToArray());
            var failure = Assert.Single(SnsQueryApiClient.ReadPublishBatchFailures(publish));
            Assert.Equal("too-big", failure.Id);
            Assert.True(failure.SenderFault);

            var messages = await SnsServiceBusTestSupport.ReceiveMessagesAsync(
                receiver, entries.Length, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Assert.Equal(
                entries.Select(entry => entry.Body).Order(StringComparer.Ordinal).ToArray(),
                messages.Select(message => message.Body.ToString()).Order(StringComparer.Ordinal).ToArray());
            await SnsServiceBusTestSupport.CompleteMessagesAsync(receiver, messages).ConfigureAwait(false);
        }
        finally
        {
            if (topicArn is not null)
            {
                try
                {
                    await SendAsync(client, "DeleteTopic", [new("TopicArn", topicArn)]).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private static Task<SnsXmlResponse> SendAsync(
        HttpClient client,
        string action,
        IEnumerable<KeyValuePair<string, string>> parameters)
        => SnsQueryApiClient.SendActionAsync(
            client,
            action,
            parameters,
            RealAzureProxyFixture.AwsAccessKey,
            RealAzureProxyFixture.AwsSecret);

    private static async Task RunBatchesAsync<T>(
        IReadOnlyList<T> items,
        Func<T, Task> action,
        int batchSize)
    {
        for (var offset = 0; offset < items.Count; offset += batchSize)
        {
            await Task.WhenAll(items.Skip(offset).Take(batchSize).Select(action)).ConfigureAwait(false);
        }
    }

    private static async Task RunBatchesBestEffortAsync<T>(
        IReadOnlyList<T> items,
        Func<T, Task> action,
        int batchSize)
    {
        for (var offset = 0; offset < items.Count; offset += batchSize)
        {
            var tasks = items.Skip(offset).Take(batchSize).Select(async item =>
            {
                try { await action(item).ConfigureAwait(false); } catch { }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }
}
