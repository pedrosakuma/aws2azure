using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;
using DynamoDbResourceNotFoundException =
    Amazon.DynamoDBv2.Model.ResourceNotFoundException;

namespace Aws2Azure.IntegrationTests.DynamoDb;



public sealed partial class DynamoDbRealAzureTransactionLoadQualificationTests
{
    private async Task WaitForTransactionRoutesAsync(
        int outputOffset,
        string expectedAuthority,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var output = fixture.ProxyOutput;
            var captured = outputOffset < output.Length
                ? output[outputOffset..]
                : string.Empty;
            var routes = captured
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains(
                    "Selected Cosmos transaction endpoint ",
                    StringComparison.Ordinal))
                .ToArray();
            if (routes.Length >= expectedCount)
            {
                if (routes.Any(line => !line.Contains(
                        expectedAuthority,
                        StringComparison.Ordinal)))
                {
                    throw new InvalidDataException(
                        "The candidate routed a transaction outside the authoritative Cosmos endpoint.");
                }
                return;
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidDataException(
            "The candidate did not emit the required transaction-route telemetry.");
    }

    private static async Task SeedLoadItemsAsync(
        IAmazonDynamoDB client,
        string table,
        int concurrency,
        CancellationToken cancellationToken)
    {
        for (var worker = 0; worker < concurrency; worker++)
        {
            for (var index = 0; index < 5; index++)
            {
                await client.PutItemAsync(
                    new PutItemRequest
                    {
                        TableName = table,
                        Item = new()
                        {
                            ["pk"] = S(Partition),
                            ["sk"] = S($"load-{worker:D2}-seed-{index:D2}"),
                            ["version"] = S("seed"),
                        },
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task CreateTableAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        await client.CreateTableAsync(
            new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions =
                [
                    new("pk", ScalarAttributeType.S),
                    new("sk", ScalarAttributeType.S),
                ],
                KeySchema =
                [
                    new("pk", KeyType.HASH),
                    new("sk", KeyType.RANGE),
                ],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            },
            cancellationToken).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var response = await client.DescribeTableAsync(table, cancellationToken)
                    .ConfigureAwait(false);
                if (response.Table.TableStatus == TableStatus.ACTIVE)
                {
                    return;
                }
            }
            catch (DynamoDbResourceNotFoundException)
            {
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"Table '{table}' did not become active.");
    }

    private static async Task WriteVersionAsync(
        IAmazonDynamoDB client,
        string table,
        IReadOnlyList<string> sortKeys,
        string version,
        CancellationToken cancellationToken)
        => await client.TransactWriteItemsAsync(
            new TransactWriteItemsRequest
            {
                TransactItems = sortKeys
                    .Select(sort => Put(table, Partition, sort, version))
                    .ToList(),
            },
            cancellationToken).ConfigureAwait(false);

    private static async Task<bool> ExistsAsync(
        IAmazonDynamoDB client,
        string table,
        string partition,
        string sort,
        CancellationToken cancellationToken)
    {
        var response = await client.GetItemAsync(
            new GetItemRequest
            {
                TableName = table,
                Key = Key(partition, sort),
                ConsistentRead = true,
            },
            cancellationToken).ConfigureAwait(false);
        return response.Item is { Count: > 0 };
    }

    private static async Task<Dictionary<string, AttributeValue>> ReadAsync(
        IAmazonDynamoDB client,
        string table,
        string partition,
        string sort,
        CancellationToken cancellationToken)
    {
        var response = await client.GetItemAsync(
            new GetItemRequest
            {
                TableName = table,
                Key = Key(partition, sort),
                ConsistentRead = true,
            },
            cancellationToken).ConfigureAwait(false);
        Assert.NotEmpty(response.Item);
        return response.Item;
    }

    private static TransactWriteItem Put(
        string table,
        string partition,
        string sort,
        string version) => new()
    {
        Put = new Put
        {
            TableName = table,
            Item = new()
            {
                ["pk"] = S(partition),
                ["sk"] = S(sort),
                ["version"] = S(version),
                ["payload"] = S("aws2azure production-shaped transaction load"),
            },
        },
    };

    private static TransactGetItem Get(
        string table,
        string partition,
        string sort) => new()
    {
        Get = new Get
        {
            TableName = table,
            Key = Key(partition, sort),
            ProjectionExpression = "pk, sk, version",
        },
    };

    private static TransactGetItem GetWithMarker(
        string table,
        string partition,
        string sort) => new()
    {
        Get = new Get
        {
            TableName = table,
            Key = Key(partition, sort),
            ProjectionExpression = "pk, sk, version, marker",
        },
    };

    private static Dictionary<string, AttributeValue> Key(
        string partition,
        string sort) => new()
    {
        ["pk"] = S(partition),
        ["sk"] = S(sort),
    };

    private static AttributeValue S(string value) => new() { S = value };

    private static async Task<RealAzureWorkloadLoadScenario> VerifyScenarioAsync(
        string id,
        string operation,
        string evidenceSource,
        Func<Task> verification)
    {
        var started = Stopwatch.GetTimestamp();
        await verification().ConfigureAwait(false);
        return Scenario(
            id,
            Service,
            operation,
            evidenceSource,
            1,
            0,
            0,
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            DateTimeOffset.UtcNow);
    }

    private static List<RealAzureWorkloadLoadSignal> BuildSignals(
        RealAzureWorkloadLoadOperationMeasurement representative,
        double durationSeconds,
        IReadOnlyCollection<double> networkLatencies,
        long representativeAttempts,
        long representativeThrottles,
        DateTimeOffset loadEnd,
        DateTimeOffset loadWindowEnd) =>
    [
        Signal(
            "representative-load-throughput",
            "representative-load",
            "throughput_per_sec",
            representative.Completions / durationSeconds,
            representativeAttempts,
            loadEnd),
        Signal(
            "representative-load-p95",
            "representative-load",
            "p95_ms",
            representative.P95Milliseconds,
            representativeAttempts,
            loadEnd),
        Signal(
            "representative-load-p99",
            "representative-load",
            "p99_ms",
            representative.P99Milliseconds,
            representativeAttempts,
            loadEnd),
        Signal(
            "representative-load-throttle-rate",
            "representative-load",
            "throttle_rate",
            representativeAttempts == 0
                ? 0
                : (double)representativeThrottles / representativeAttempts,
            representativeAttempts,
            loadEnd),
        Signal(
            "representative-load-unauthenticated-connectivity-header-p95",
            "representative-load",
            "p95_ms",
            Percentile(networkLatencies, 0.95),
            networkLatencies.Count,
            loadWindowEnd),
    ];

    private static bool IsThrottle(Exception exception) =>
        exception is ProvisionedThroughputExceededException;
}

