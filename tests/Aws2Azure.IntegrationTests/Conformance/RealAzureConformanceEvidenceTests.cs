using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Azure.Messaging.ServiceBus.Administration;
using Aws2Azure.Conformance.Cases;
using Aws2Azure.Conformance.Canonicalization;
using Aws2Azure.Conformance.DynamoDb;
using Aws2Azure.Conformance.Evidence;
using Aws2Azure.Conformance.Kinesis;
using Aws2Azure.Conformance.S3;
using Aws2Azure.Conformance.Sns;
using Aws2Azure.Conformance.Sqs;
using Xunit;
using Xunit.Sdk;

namespace Aws2Azure.IntegrationTests.Conformance;

/// <summary>
/// Captures canonical real-Azure proxy evidence for the shared happy-path
/// conformance matrix (issue #708). The existing offline Tier-1 harness seeds
/// these cases with a skip reason because it has no live backend; this fixture
/// is the backend-backed execution tier that finally runs those plans and emits
/// step-by-step canonical artifacts as a nightly byproduct.
/// </summary>
[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class RealAzureConformanceEvidenceTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task S3_happy_path_cases_emit_real_azure_evidence()
    {
        Skip.IfNot(fixture.BlobConfigured,
            "AZURE_BLOB_ACCOUNT/AZURE_BLOB_KEY not set — skipping real-Azure S3 conformance.");

        await ExecuteServiceCasesAsync(
            "s3",
            Enumerate(S3ErrorMatrix.Cases, S3HappyPathMatrix.Cases),
            CreateContext("s3")).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task DynamoDb_happy_path_cases_emit_real_azure_evidence()
    {
        Skip.IfNot(fixture.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure DynamoDB conformance.");

        using var client = fixture.CreateDynamoDbClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var tableName = "conf-evidence-" + Guid.NewGuid().ToString("N")[..12];

        try
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = tableName,
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }, timeout.Token).ConfigureAwait(false);
            await WaitForTableActiveAsync(client, tableName, timeout.Token).ConfigureAwait(false);

            await ExecuteServiceCasesAsync(
                "dynamodb",
                Enumerate(DynamoDbErrorMatrix.Cases, DynamoDbHappyPathMatrix.Cases),
                CreateContext(
                    "dynamodb",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["tableName"] = tableName,
                    })).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await client.DeleteTableAsync(tableName).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    [SkippableFact]
    public async Task Kinesis_happy_path_cases_emit_real_azure_evidence()
    {
        Skip.IfNot(fixture.EventHubsConfigured,
            "AZURE_EVENTHUBS_* / AZURE_EVENTHUBS_STREAM not set — skipping real-Azure Kinesis conformance.");

        await ExecuteServiceCasesAsync(
            "kinesis",
            Enumerate(KinesisErrorMatrix.Cases, KinesisHappyPathMatrix.Cases),
            CreateContext(
                "kinesis",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["streamName"] = fixture.EventHubStream,
                })).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Sns_happy_path_cases_emit_real_azure_evidence()
    {
        Skip.IfNot(fixture.SnsConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SNS conformance.");

        const int topicCount = 101;
        var run = Guid.NewGuid().ToString("N")[..10];
        var seededTopics = Enumerable.Range(0, topicCount)
            .Select(index => $"sns-conf-evidence-{run}-{index:D3}")
            .ToArray();
        var admin = new ServiceBusAdministrationClient(fixture.CreateServiceBusConnectionString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));

        try
        {
            await RunBatchesAsync(
                seededTopics,
                name => admin.CreateTopicAsync(name, timeout.Token),
                batchSize: 8).ConfigureAwait(false);

            await ExecuteServiceCasesAsync(
                "sns",
                Enumerate(SnsErrorMatrix.Cases, SnsHappyPathMatrix.Cases),
                CreateContext("sns")).ConfigureAwait(false);
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await RunBatchesBestEffortAsync(
                seededTopics,
                name => admin.DeleteTopicAsync(name, cleanupTimeout.Token),
                batchSize: 8).ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task Sqs_happy_path_cases_emit_real_azure_evidence()
    {
        Skip.IfNot(fixture.ServiceBusConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SQS conformance.");

        await ExecuteServiceCasesAsync(
            "sqs",
            Enumerate(SqsErrorMatrix.Cases, SqsHappyPathMatrix.Cases),
            CreateContext("sqs")).ConfigureAwait(false);
    }

    private static ConformanceEvidenceStore CreateEvidenceStore()
        => new(
            ConformanceEvidenceStore.ResolveRoot(
                Environment.GetEnvironmentVariable("AWS2AZURE_CONFORMANCE_EVIDENCE_DIR")));

    private ConformanceCaseContext CreateContext(
        string service,
        IReadOnlyDictionary<string, string>? properties = null)
        => new(
            RealAzureProxyFixture.AwsAccessKey,
            RealAzureProxyFixture.AwsSecret,
            new Uri(fixture.GetServiceUrl(service)),
            Properties: properties);

    private static IReadOnlyList<IConformanceCase> Enumerate(params IEnumerable<IConformanceCase>[] groups) =>
        groups.SelectMany(static group => group).ToArray();

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
                try
                {
                    await action(item).ConfigureAwait(false);
                }
                catch
                {
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private static async Task WaitForTableActiveAsync(
        IAmazonDynamoDB client,
        string tableName,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var response = await client.DescribeTableAsync(tableName, cancellationToken).ConfigureAwait(false);
                if (response.Table.TableStatus == TableStatus.ACTIVE)
                {
                    return;
                }
            }
            catch (ResourceNotFoundException)
            {
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Table '{tableName}' did not become active.");
    }

    private static List<KeyValuePair<string, string>> CollectHeaders(HttpResponseMessage response)
    {
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var header in response.Headers)
        {
            foreach (var value in header.Value)
            {
                headers.Add(new KeyValuePair<string, string>(header.Key, value));
            }
        }

        if (response.Content is not null)
        {
            foreach (var header in response.Content.Headers)
            {
                foreach (var value in header.Value)
                {
                    headers.Add(new KeyValuePair<string, string>(header.Key, value));
                }
            }
        }

        return headers;
    }

    private async Task ExecuteServiceCasesAsync(
        string service,
        IReadOnlyList<IConformanceCase> cases,
        ConformanceCaseContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(cases);

        var store = CreateEvidenceStore();
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2),
        };

        foreach (var testCase in cases)
        {
            var plan = await testCase.CreatePlanAsync(context).ConfigureAwait(false);
            if (plan.Steps.Count != testCase.Expected.Steps.Count)
            {
                throw new XunitException(
                    $"Conformance case '{service}/{testCase.Name}' planned {plan.Steps.Count} steps, " +
                    $"but exposed {testCase.Expected.Steps.Count} expected steps.");
            }

            // The shared happy-path matrices intentionally carry a Tier-1-only
            // offline skip reason (#708). This real-Azure runner is the backend-
            // backed tier that executes those same plans regardless.
            var exchanges = new List<ConformanceObservedExchange>(plan.Steps.Count);
            for (var index = 0; index < plan.Steps.Count; index++)
            {
                var step = plan.Steps[index];
                var state = new ConformanceExecutionState(context, exchanges);
                using var request = await step.BuildRequestAsync(state).ConfigureAwait(false);
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
                var body = response.Content is null
                    ? string.Empty
                    : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                var actualStatus = (int)response.StatusCode;
                var expectedStatus = testCase.Expected.Steps[index].ExpectedStatus;
                if (actualStatus != expectedStatus)
                {
                    throw new XunitException(
                        $"Conformance case '{service}/{testCase.Name}' step '{step.Name}' returned " +
                        $"{actualStatus} instead of {expectedStatus}. Body: {body}");
                }

                var headers = CollectHeaders(response);
                var canonical = AwsErrorCanonicalizer.Canonicalize(actualStatus, headers, body);
                // The step name must match the real-AWS golden's step name
                // verbatim (see RealAwsConformanceCaptureTests.SaveStep) so the
                // offline Tier-3 diff can pair the two files up by identical
                // service/case/step path — do not add an index prefix here.
                store.Save(
                    canonical,
                    new ConformanceEvidenceMetadata(
                        ConformanceEvidenceMetadata.SourceRealAzureProxy,
                        service,
                        testCase.Name,
                        testCase.Operation,
                        step.Name,
                        DateTimeOffset.UtcNow,
                        plan.SkipReason));

                exchanges.Add(new ConformanceObservedExchange(
                    step.Name,
                    actualStatus,
                    headers,
                    body));
            }
        }
    }
}
