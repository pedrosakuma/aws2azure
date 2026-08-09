using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Aws2Azure.Conformance.Cases;
using Aws2Azure.Conformance.Canonicalization;
using Aws2Azure.Conformance.DynamoDb;
using Aws2Azure.Conformance.Goldens;
using Aws2Azure.Conformance.Kinesis;
using Aws2Azure.Conformance.S3;
using Aws2Azure.Conformance.Sns;
using Aws2Azure.Conformance.Sqs;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;
using Xunit.Sdk;

namespace Aws2Azure.IntegrationTests.Conformance;

[Trait("Category", "RealAws")]
[Collection(RealAwsConformanceCaptureCollection.Name)]
public sealed class RealAwsConformanceCaptureTests(RealAwsConformanceCaptureFixture fixture)
{
    [SkippableFact]
    public async Task S3_happy_path_cases_capture_real_aws_goldens()
    {
        Skip.IfNot(fixture.IsConfigured, fixture.SkipReason);
        await ExecuteServiceCasesAsync(
            "s3",
            Enumerate(S3ErrorMatrix.Cases, S3HappyPathMatrix.Cases),
            CreateContext(
                "s3",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bucketName"] = fixture.CreateEphemeralName("s3bucket"),
                })).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task DynamoDb_happy_path_cases_capture_real_aws_goldens()
    {
        Skip.IfNot(fixture.IsConfigured, fixture.SkipReason);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var tableName = fixture.CreateEphemeralName("dynamodb");

        try
        {
            await fixture.DynamoDb.CreateTableAsync(new CreateTableRequest
            {
                TableName = tableName,
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
                Tags = fixture.CreateDynamoDbTags(),
            }, timeout.Token).ConfigureAwait(false);
            await WaitForTableActiveAsync(fixture.DynamoDb, tableName, timeout.Token).ConfigureAwait(false);

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
            await fixture.DeleteTableBestEffortAsync(tableName).ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task Kinesis_happy_path_cases_capture_real_aws_goldens()
    {
        Skip.IfNot(fixture.IsConfigured, fixture.SkipReason);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var streamName = fixture.CreateEphemeralName("kinesis");

        try
        {
            await fixture.Kinesis.CreateStreamAsync(new CreateStreamRequest
            {
                StreamName = streamName,
                StreamModeDetails = new StreamModeDetails
                {
                    StreamMode = StreamMode.ON_DEMAND,
                },
                Tags = fixture.CreateStringTagDictionary(),
            }, timeout.Token).ConfigureAwait(false);
            await WaitForStreamActiveAsync(fixture.Kinesis, streamName, timeout.Token).ConfigureAwait(false);

            await ExecuteServiceCasesAsync(
                "kinesis",
                Enumerate(KinesisErrorMatrix.Cases, KinesisHappyPathMatrix.Cases),
                CreateContext(
                    "kinesis",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["streamName"] = streamName,
                    })).ConfigureAwait(false);
        }
        finally
        {
            await fixture.DeleteStreamBestEffortAsync(streamName).ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task Sns_happy_path_cases_capture_real_aws_goldens()
    {
        Skip.IfNot(fixture.IsConfigured, fixture.SkipReason);

        const int topicCount = 101;
        var seededTopicArns = new List<string>(topicCount);

        try
        {
            for (var index = 0; index < topicCount; index++)
            {
                var response = await fixture.Sns.CreateTopicAsync(new CreateTopicRequest
                {
                    Name = fixture.CreateEphemeralName($"snsseed{index:D3}"),
                    Tags = fixture.CreateSnsTags(),
                }).ConfigureAwait(false);
                seededTopicArns.Add(response.TopicArn);
            }

            await ExecuteServiceCasesAsync(
                "sns",
                Enumerate(SnsErrorMatrix.Cases, SnsHappyPathMatrix.Cases),
                CreateContext(
                    "sns",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["topicName"] = fixture.CreateEphemeralName("snstopic"),
                    })).ConfigureAwait(false);
        }
        finally
        {
            await RunBatchesBestEffortAsync(
                seededTopicArns,
                fixture.DeleteTopicBestEffortAsync,
                batchSize: 8).ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task Sqs_happy_path_cases_capture_real_aws_goldens()
    {
        Skip.IfNot(fixture.IsConfigured, fixture.SkipReason);
        await ExecuteServiceCasesAsync(
            "sqs",
            Enumerate(SqsErrorMatrix.Cases, SqsHappyPathMatrix.Cases),
            CreateContext(
                "sqs",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["queueName"] = fixture.CreateEphemeralName("sqsqueue"),
                    ["queueName1"] = fixture.CreateEphemeralName("sqsqueue1"),
                    ["queueName2"] = fixture.CreateEphemeralName("sqsqueue2"),
                    ["queueName3"] = fixture.CreateEphemeralName("sqsqueue3"),
                })).ConfigureAwait(false);
    }

    private ConformanceCaseContext CreateContext(
        string service,
        IReadOnlyDictionary<string, string>? properties = null)
        => new(
            fixture.AccessKeyId,
            fixture.SecretAccessKey,
            fixture.GetServiceBaseAddress(service),
            fixture.Region,
            properties,
            fixture.SessionToken);

    private static IReadOnlyList<IConformanceCase> Enumerate(params IEnumerable<IConformanceCase>[] groups) =>
        groups.SelectMany(static group => group).ToArray();

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
            catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
            {
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Table '{tableName}' did not become active.");
    }

    private static async Task WaitForStreamActiveAsync(
        IAmazonKinesis client,
        string streamName,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var response = await client.DescribeStreamSummaryAsync(
                    new DescribeStreamSummaryRequest { StreamName = streamName },
                    cancellationToken).ConfigureAwait(false);
                if (response.StreamDescriptionSummary.StreamStatus == StreamStatus.ACTIVE)
                {
                    return;
                }
            }
            catch (Amazon.Kinesis.Model.ResourceNotFoundException)
            {
            }

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Stream '{streamName}' did not become active.");
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

        var store = GoldenStore.ForService(service);
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

                var expectedStep = testCase.Expected.Steps[index];
                var actualStatus = (int)response.StatusCode;
                var expectedStatus = expectedStep.ExpectedStatus;
                if (actualStatus != expectedStatus)
                {
                    throw new XunitException(
                        $"Conformance case '{service}/{testCase.Name}' step '{step.Name}' returned " +
                        $"{actualStatus} instead of {expectedStatus}. Body: {body}");
                }

                var headers = CollectHeaders(response);
                var canonical = AwsErrorCanonicalizer.Canonicalize(actualStatus, headers, body);
                AssertStepMatchesExpected(testCase, step.Name, expectedStep, response, body, canonical);
                store.SaveStep(
                    testCase.Name,
                    step.Name,
                    canonical,
                    new GoldenProvenance(
                        GoldenProvenance.SourceRealAws,
                        testCase.Operation,
                        DateTimeOffset.UtcNow,
                        "Captured from real AWS by capture-real-aws.yml"));

                exchanges.Add(new ConformanceObservedExchange(
                    step.Name,
                    actualStatus,
                    headers,
                    body));
            }
        }
    }

    private static void AssertStepMatchesExpected(
        IConformanceCase testCase,
        string stepName,
        ConformanceStepExpectation expectedStep,
        HttpResponseMessage response,
        string body,
        CanonicalResponse canonical)
    {
        if (expectedStep.ExpectedErrorCode is not null)
        {
            var code = canonical.BodyFields.FirstOrDefault(f => f.Name == "Code").Value;
            if (!string.Equals(expectedStep.ExpectedErrorCode, code, StringComparison.Ordinal))
            {
                throw new XunitException(
                    $"Conformance case '{testCase.Name}' step '{stepName}' returned error code '{code}' " +
                    $"instead of '{expectedStep.ExpectedErrorCode}'. Body: {body}");
            }

            return;
        }

        if (expectedStep.RequiredHeaders is not null)
        {
            foreach (var expectedHeader in expectedStep.RequiredHeaders)
            {
                if (!response.Headers.TryGetValues(expectedHeader.Name, out var headerValues)
                    && !(response.Content?.Headers.TryGetValues(expectedHeader.Name, out headerValues) ?? false)
                    || !headerValues.Any(value => !string.IsNullOrWhiteSpace(value)))
                {
                    throw new XunitException(
                        $"Conformance case '{testCase.Name}' step '{stepName}' missing required header '{expectedHeader.Name}'.");
                }
            }
        }

        if (expectedStep.RequiredBodyAssertions is not null)
        {
            foreach (var assertion in expectedStep.RequiredBodyAssertions)
            {
                if (!BodyAssertionSatisfied(canonical, body, assertion.Path))
                {
                    throw new XunitException(
                        $"Conformance case '{testCase.Name}' step '{stepName}' did not satisfy required body assertion '{assertion.Path}'.");
                }
            }
        }
    }

    private static bool BodyAssertionSatisfied(CanonicalResponse canonical, string body, string path)
    {
        if (string.Equals(path, "Body", StringComparison.Ordinal))
        {
            return !string.IsNullOrEmpty(body);
        }

        if (canonical.BodyKind == CanonicalResponse.BodyKindJsonError || LooksLikeJson(body))
        {
            return JsonPathExists(body, path);
        }

        return XmlPathExists(body, path);
    }

    private static bool LooksLikeJson(string body)
        => !string.IsNullOrWhiteSpace(body) && (body.TrimStart().StartsWith("{", StringComparison.Ordinal) || body.TrimStart().StartsWith("[", StringComparison.Ordinal));

    private static bool JsonPathExists(string body, string path)
    {
        using var doc = JsonDocument.Parse(body);
        var current = doc.RootElement;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind == JsonValueKind.Array)
            {
                if (current.GetArrayLength() == 0)
                {
                    return false;
                }

                current = current[0];
            }

            if (!current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.Null => false,
            JsonValueKind.Array => current.GetArrayLength() > 0,
            JsonValueKind.String => !string.IsNullOrEmpty(current.GetString()),
            _ => true,
        };
    }

    private static bool XmlPathExists(string body, string path)
    {
        var document = XDocument.Parse(body);
        IEnumerable<XElement> current = [document.Root!];
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.SelectMany(
                element => element.Descendants().Where(descendant => descendant.Name.LocalName == segment));
            if (!current.Any())
            {
                return false;
            }
        }

        return current.Any(element => !string.IsNullOrWhiteSpace(element.Value) || element.HasElements);
    }
}

public sealed class RealAwsConformanceCaptureFixture : IAsyncLifetime
{
    private SessionAWSCredentials? _credentials;

    public bool IsConfigured { get; private set; }

    public string SkipReason { get; private set; } =
        "AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY/AWS_SESSION_TOKEN not set — skipping real-AWS conformance capture.";

    public string Region => "us-east-1";

    public string AccessKeyId => _credentials?.GetCredentials().AccessKey ?? string.Empty;

    public string SecretAccessKey => _credentials?.GetCredentials().SecretKey ?? string.Empty;

    public string SessionToken => _credentials?.GetCredentials().Token ?? string.Empty;

    public IAmazonDynamoDB DynamoDb { get; private set; } = null!;

    public IAmazonKinesis Kinesis { get; private set; } = null!;

    public IAmazonSimpleNotificationService Sns { get; private set; } = null!;

    public Task InitializeAsync()
    {
        var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        var sessionToken = Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN");

        if (string.IsNullOrWhiteSpace(accessKey)
            || string.IsNullOrWhiteSpace(secretKey)
            || string.IsNullOrWhiteSpace(sessionToken))
        {
            IsConfigured = false;
            return Task.CompletedTask;
        }

        _credentials = new SessionAWSCredentials(accessKey, secretKey, sessionToken);
        DynamoDb = new AmazonDynamoDBClient(_credentials, Amazon.RegionEndpoint.USEast1);
        Kinesis = new AmazonKinesisClient(_credentials, Amazon.RegionEndpoint.USEast1);
        Sns = new AmazonSimpleNotificationServiceClient(_credentials, Amazon.RegionEndpoint.USEast1);
        IsConfigured = true;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        (DynamoDb as IDisposable)?.Dispose();
        (Kinesis as IDisposable)?.Dispose();
        (Sns as IDisposable)?.Dispose();
        return Task.CompletedTask;
    }

    public Uri GetServiceBaseAddress(string service)
        => service switch
        {
            "s3" => new Uri("https://s3.us-east-1.amazonaws.com/"),
            "dynamodb" => new Uri("https://dynamodb.us-east-1.amazonaws.com/"),
            "kinesis" => new Uri("https://kinesis.us-east-1.amazonaws.com/"),
            "sns" => new Uri("https://sns.us-east-1.amazonaws.com/"),
            "sqs" => new Uri("https://sqs.us-east-1.amazonaws.com/"),
            _ => throw new ArgumentOutOfRangeException(nameof(service), service, "Unknown service."),
        };

    public string CreateEphemeralName(string suffix)
        => $"aws2azure-it-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Random.Shared.Next(0, 1_000_000):D6}-{suffix}";

    public List<Amazon.DynamoDBv2.Model.Tag> CreateDynamoDbTags() =>
    [
        new() { Key = "purpose", Value = "aws2azure-it" },
        new() { Key = "created", Value = DateTimeOffset.UtcNow.ToString("O") },
    ];

    public List<Amazon.SimpleNotificationService.Model.Tag> CreateSnsTags() =>
    [
        new() { Key = "purpose", Value = "aws2azure-it" },
        new() { Key = "created", Value = DateTimeOffset.UtcNow.ToString("O") },
    ];

    public Dictionary<string, string> CreateStringTagDictionary() => new(StringComparer.Ordinal)
    {
        ["purpose"] = "aws2azure-it",
        ["created"] = DateTimeOffset.UtcNow.ToString("O"),
    };

    public async Task DeleteTableBestEffortAsync(string tableName)
    {
        // PAY_PER_REQUEST tables can briefly report ACTIVE via DescribeTable
        // just before backend provisioning fully settles, so an immediate
        // DeleteTable can still race and fail with ResourceInUseException
        // even though CreateTable + WaitForTableActive already succeeded.
        // Retry for up to ~40s; if it's still in use after that, give up
        // silently — this is best-effort teardown, so a straggler is left
        // for the nightly real-aws-reaper.yml rather than failing a capture
        // case whose actual assertions already passed.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                await DynamoDb.DeleteTableAsync(tableName).ConfigureAwait(false);
                return;
            }
            catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
            {
                return;
            }
            catch (Amazon.DynamoDBv2.Model.ResourceInUseException)
            {
                if (attempt == 19)
                {
                    return;
                }

                await Task.Delay(2000).ConfigureAwait(false);
            }
        }
    }

    public async Task DeleteStreamBestEffortAsync(string streamName)
    {
        try
        {
            await Kinesis.DeleteStreamAsync(new DeleteStreamRequest
            {
                StreamName = streamName,
                EnforceConsumerDeletion = true,
            }).ConfigureAwait(false);
        }
        catch (Amazon.Kinesis.Model.ResourceNotFoundException)
        {
        }
    }

    public async Task DeleteTopicBestEffortAsync(string topicArn)
    {
        try
        {
            await Sns.DeleteTopicAsync(topicArn).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealAwsConformanceCaptureCollection : ICollectionFixture<RealAwsConformanceCaptureFixture>
{
    public const string Name = "real-aws-conformance-capture";
}
