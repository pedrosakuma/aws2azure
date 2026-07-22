using System.Collections.Concurrent;
using System.Net;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sns;

[Trait("Category", "RealAzure")]
[Trait("Category", "SnsSubscriptionLoad")]
[Collection(RealAzureCollection.Name)]
public sealed class SnsSubscriptionRealAzureLoadScenarioTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task Representative_subscription_lifecycle_load_completes_and_cleans_up()
    {
        Skip.If(!string.Equals(
            Environment.GetEnvironmentVariable("AWS2AZURE_SNS_SUBSCRIPTION_LOAD"),
            "1",
            StringComparison.Ordinal),
            "AWS2AZURE_SNS_SUBSCRIPTION_LOAD=1 not set.");
        Skip.IfNot(fixture.SnsConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SNS subscription load.");

        var concurrency = ReadPositiveInt("AWS2AZURE_LOAD_CONCURRENCY", 4);
        var iterationsPerWorker = ReadPositiveInt("AWS2AZURE_LOAD_ITERATIONS", 10);
        var failures = new ConcurrentQueue<string>();
        var completed = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));

        var workers = Enumerable.Range(0, concurrency)
            .Select(worker => RunWorkerAsync(
                fixture,
                worker,
                iterationsPerWorker,
                failures,
                () => Interlocked.Increment(ref completed),
                timeout.Token))
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);

        Assert.True(
            failures.IsEmpty,
            "SNS subscription load failures: " + string.Join(" | ", failures));
        Assert.Equal(concurrency * iterationsPerWorker, completed);
    }

    private static async Task RunWorkerAsync(
        RealAzureProxyFixture fixture,
        int worker,
        int iterations,
        ConcurrentQueue<string> failures,
        Action recordCompletion,
        CancellationToken cancellationToken)
    {
        using var client = fixture.CreateSnsClient();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var topicName = $"sns-sub-load-{worker:D2}-{iteration:D3}-{Guid.NewGuid():N}"[..50];
            string? topicArn = null;
            string? subscriptionArn = null;
            try
            {
                var create = await SendAsync(client, "CreateTopic", [new("Name", topicName)]).ConfigureAwait(false);
                RequireOk(create, "CreateTopic");
                topicArn = SnsQueryApiClient.ReadTopicArn(create);

                var subscribe = await SendAsync(client, "Subscribe",
                [
                    new("TopicArn", topicArn),
                    new("Protocol", "sqs"),
                    new("Endpoint", SnsQueryApiClient.CreateSubscriptionEndpoint()),
                    new("ReturnSubscriptionArn", "true"),
                ]).ConfigureAwait(false);
                RequireOk(subscribe, "Subscribe");
                subscriptionArn = SnsQueryApiClient.ReadSubscriptionArn(subscribe);

                var confirm = await SendAsync(client, "ConfirmSubscription",
                [
                    new("TopicArn", topicArn),
                    new("Token", subscriptionArn),
                ]).ConfigureAwait(false);
                RequireOk(confirm, "ConfirmSubscription");

                var get = await SendAsync(client, "GetSubscriptionAttributes",
                    [new("SubscriptionArn", subscriptionArn)]).ConfigureAwait(false);
                RequireOk(get, "GetSubscriptionAttributes");

                var set = await SendAsync(client, "SetSubscriptionAttributes",
                [
                    new("SubscriptionArn", subscriptionArn),
                    new("AttributeName", "RawMessageDelivery"),
                    new("AttributeValue", "true"),
                ]).ConfigureAwait(false);
                RequireOk(set, "SetSubscriptionAttributes");

                var byTopic = await SendAsync(client, "ListSubscriptionsByTopic",
                    [new("TopicArn", topicArn)]).ConfigureAwait(false);
                RequireOk(byTopic, "ListSubscriptionsByTopic");
                Assert.Contains(
                    SnsQueryApiClient.ReadListedSubscriptions(byTopic),
                    item => string.Equals(item.SubscriptionArn, subscriptionArn, StringComparison.Ordinal));

                var all = await SendAsync(client, "ListSubscriptions", []).ConfigureAwait(false);
                RequireOk(all, "ListSubscriptions");

                var unsubscribe = await SendAsync(client, "Unsubscribe",
                    [new("SubscriptionArn", subscriptionArn)]).ConfigureAwait(false);
                RequireOk(unsubscribe, "Unsubscribe");
                subscriptionArn = null;

                var delete = await SendAsync(client, "DeleteTopic",
                    [new("TopicArn", topicArn)]).ConfigureAwait(false);
                RequireOk(delete, "DeleteTopic");
                topicArn = null;
                recordCompletion();
            }
            catch (Exception ex)
            {
                failures.Enqueue($"worker={worker} iteration={iteration}: {ex.Message}");
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
                        await SendAsync(client, "DeleteTopic",
                            [new("TopicArn", topicArn)]).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
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

    private static void RequireOk(SnsXmlResponse response, string operation)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"{operation} returned {(int)response.StatusCode}: {response.Body}");
        }
    }

    private static int ReadPositiveInt(string name, int defaultValue)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0
            ? value
            : defaultValue;
}
