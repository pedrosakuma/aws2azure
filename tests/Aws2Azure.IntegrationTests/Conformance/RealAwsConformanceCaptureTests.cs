using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
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
                    // list-buckets-roundtrip creates two of its own buckets
                    // (prefix + "-a"/"-b") rather than reusing bucketName
                    // above, so it needs its own ephemeral, IAM-scoped prefix
                    // (the real-AWS least-privilege policy only allows
                    // s3:CreateBucket on arn:aws:s3:::aws2azure-it-*).
                    ["bucketPrefix"] = fixture.CreateEphemeralName("s3listbuckets"),
                })).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task S3_backend_error_cases_capture_real_aws_goldens()
    {
        Skip.IfNot(fixture.IsConfigured, fixture.SkipReason);

        var bucket = fixture.CreateEphemeralName("s3backenderr");
        // "bucketalreadyownedbyyou-recreate" is signed for eu-west-1 and, on
        // real AWS, must actually be created in eu-west-1 — a mismatched
        // signed scope is rejected with AuthorizationHeaderMalformed before
        // ownership is ever evaluated, so it needs its own regional bucket
        // rather than reusing the shared us-east-1 one above.
        var euWestBucket = fixture.CreateEphemeralName("s3backenderreuw");
        try
        {
            await fixture.S3.PutBucketAsync(bucket).ConfigureAwait(false);
            await fixture.S3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = S3BackendErrorMatrix.ExistingKey,
                ContentBody = "conformance conditional object",
            }).ConfigureAwait(false);
            await fixture.S3.PutBucketAsync(new PutBucketRequest
            {
                BucketName = euWestBucket,
                BucketRegion = S3Region.EUWest1,
            }).ConfigureAwait(false);

            await ExecuteServiceCasesAsync(
                "s3",
                S3BackendErrorMatrix.Cases.Cast<IConformanceCase>().ToArray(),
                CreateContext(
                    "s3",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["bucketName"] = bucket,
                        ["euWestBucketName"] = euWestBucket,
                    })).ConfigureAwait(false);
        }
        finally
        {
            await fixture.DeleteBucketBestEffortAsync(fixture.S3EuWest1, euWestBucket).ConfigureAwait(false);
            await fixture.DeleteBucketBestEffortAsync(bucket).ConfigureAwait(false);
        }
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
                        // The cases below each create and delete their own
                        // table rather than reusing tableName above, so each
                        // needs its own ephemeral, IAM-scoped name (the
                        // real-AWS least-privilege policy only allows
                        // dynamodb:CreateTable on arn:...:table/aws2azure-it-*).
                        ["createTableName"] = fixture.CreateEphemeralName("dynamodbcreate"),
                        ["transactTableName"] = fixture.CreateEphemeralName("dynamodbtransact"),
                        ["tagTableName"] = fixture.CreateEphemeralName("dynamodbtag"),
                        ["ttlTableName"] = fixture.CreateEphemeralName("dynamodbttl"),
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
        string? subscriptionQueueUrl = null;

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

            var (queueUrl, queueArn) = await fixture.CreateSnsAutoConfirmQueueAsync("snssubqueue").ConfigureAwait(false);
            subscriptionQueueUrl = queueUrl;

            await ExecuteServiceCasesAsync(
                "sns",
                Enumerate(SnsErrorMatrix.Cases, SnsHappyPathMatrix.Cases),
                CreateContext(
                    "sns",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["topicName"] = fixture.CreateEphemeralName("snstopic"),
                        ["subscriptionEndpoint"] = queueArn,
                    })).ConfigureAwait(false);
        }
        finally
        {
            await RunBatchesBestEffortAsync(
                seededTopicArns,
                fixture.DeleteTopicBestEffortAsync,
                batchSize: 8).ConfigureAwait(false);

            if (subscriptionQueueUrl is not null)
            {
                await fixture.DeleteQueueBestEffortAsync(subscriptionQueueUrl).ConfigureAwait(false);
            }
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
                    // Each of the cases below creates and deletes its own
                    // queue rather than reusing queueName above, so each
                    // needs its own ephemeral, IAM-scoped name (the real-AWS
                    // least-privilege policy only allows sqs:CreateQueue on
                    // arn:...:aws2azure-it-*).
                    ["queueTaggingQueueName"] = fixture.CreateEphemeralName("sqsqueuetag"),
                    ["queueAttributesQueueName"] = fixture.CreateEphemeralName("sqsqueueattr"),
                    ["changeVisibilityQueueName"] = fixture.CreateEphemeralName("sqsqueuevis"),
                    ["changeVisibilityBatchQueueName"] = fixture.CreateEphemeralName("sqsqueuevisb"),
                    ["purgeQueueName"] = fixture.CreateEphemeralName("sqsqueuepurge"),
                    ["dlqQueueName"] = fixture.CreateEphemeralName("sqsqueuedlq"),
                    ["dlqSourceQueueName"] = fixture.CreateEphemeralName("sqsqueuedlqsrc"),
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
                var expectedStep = testCase.Expected.Steps[index];
                var expectedStatus = expectedStep.ExpectedStatus;

                // DynamoDB CreateTable is asynchronous on real AWS (the table
                // stays in CREATING for a short window before ACTIVE), unlike
                // our proxy's synchronous contract. A DeleteTable issued
                // immediately after CreateTable can therefore race and get a
                // transient ResourceInUseException ("Table is being
                // created"). Retry with a short backoff — bounded so a
                // genuinely stuck table still fails the case rather than
                // hanging — mirroring the existing DeleteTableBestEffortAsync
                // teardown retry pattern.
                const int maxAttempts = 10;
                for (var attempt = 1; ; attempt++)
                {
                    var state = new ConformanceExecutionState(context, exchanges);
                    using var request = await step.BuildRequestAsync(state).ConfigureAwait(false);

                    // Force a fresh TCP connection per request. Root-caused via
                    // real-AWS diagnostic capture (2026-08-20): reusing one
                    // persistent HTTP/1.1 connection across a case sequence that
                    // deliberately provokes 400 auth-failure responses (the
                    // error-matrix cases) followed by a real write request
                    // desynchronized the connection just enough that the next
                    // request's response was misread by AWS's edge as an
                    // InvalidSignatureException, even though the signed request
                    // itself was verified byte-for-byte correct (canonical string,
                    // string-to-sign, and derived key all matched AWS's own
                    // reported expectations, and an official-AWSSDK.NET probe
                    // succeeded against the same table/credentials). Applies to
                    // all services, not just DynamoDB, since any real-AWS capture
                    // sequence that mixes deliberate-error and real cases on a
                    // shared HttpClient is susceptible to the same desync.
                    request.Headers.ConnectionClose = true;

                    using var response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
                    var body = response.Content is null
                        ? string.Empty
                        : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    var actualStatus = (int)response.StatusCode;
                    if (actualStatus != expectedStatus)
                    {
                        if (service == "dynamodb"
                            && actualStatus == 400
                            && attempt < maxAttempts
                            && body.Contains("ResourceInUseException", StringComparison.Ordinal)
                            && body.Contains("being created", StringComparison.Ordinal))
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                            continue;
                        }

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
                    break;
                }
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

    internal static bool BodyAssertionSatisfied(CanonicalResponse canonical, string body, string path)
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
        foreach (var rawSegment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            // Split "Records[0]" into a property name ("Records") and an
            // optional explicit array index (0). Without this, a path like
            // "Records[0].SequenceNumber" looks up a literal property named
            // "Records[0]", which never exists, and the assertion always fails
            // regardless of the actual response body.
            var propertyName = rawSegment;
            int? explicitIndex = null;
            var bracketStart = rawSegment.IndexOf('[', StringComparison.Ordinal);
            if (bracketStart >= 0 && rawSegment.EndsWith(']') && bracketStart < rawSegment.Length - 2)
            {
                propertyName = rawSegment[..bracketStart];
                var indexText = rawSegment[(bracketStart + 1)..^1];
                if (!int.TryParse(indexText, out var parsedIndex))
                {
                    // Malformed bracket syntax (non-numeric or nested brackets,
                    // e.g. "Records[abc]" or "Records[0][1]") must fail fast
                    // rather than silently degrading to a bracket-less lookup,
                    // which would mask a typo'd assertion path instead of
                    // surfacing it as a broken test fixture.
                    return false;
                }

                explicitIndex = parsedIndex;
            }

            if (current.ValueKind == JsonValueKind.Array)
            {
                var index = explicitIndex ?? 0;
                if (index < 0 || index >= current.GetArrayLength())
                {
                    return false;
                }

                current = current[index];
            }

            if (propertyName.Length == 0)
            {
                // The segment was purely an index applied to the array we're
                // already positioned on (already consumed above); nothing more
                // to navigate for this segment.
                continue;
            }

            if (!current.TryGetProperty(propertyName, out current))
            {
                return false;
            }

            if (explicitIndex.HasValue && current.ValueKind == JsonValueKind.Array)
            {
                var index = explicitIndex.Value;
                if (index < 0 || index >= current.GetArrayLength())
                {
                    return false;
                }

                current = current[index];
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
            // DescendantsAndSelf (not Descendants) so a path whose first
            // segment names the document's own root element (e.g.
            // "ListBucketResult.IsTruncated" against a <ListBucketResult>
            // root) still matches instead of unconditionally failing.
            current = current.SelectMany(
                element => element.DescendantsAndSelf().Where(descendant => descendant.Name.LocalName == segment));
            if (!current.Any())
            {
                return false;
            }
        }

        // Every path segment already proved to exist in the loop above (an
        // empty match short-circuits to false immediately), so no further
        // filtering is needed here. A prior version additionally required the
        // final element to be non-empty or have children, which incorrectly
        // failed assertions for elements that are legitimately present-but-
        // empty on the wire (e.g. real AWS's <TagSet/> after all tags are
        // removed) — this assertion only needs to confirm the path exists,
        // not that its terminal element has content.
        return true;
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

    public IAmazonS3 S3 { get; private set; } = null!;

    /// <summary>
    /// A client bound to the eu-west-1 endpoint. Real AWS S3 requires bucket
    /// operations (list/delete, not just create) to target the bucket's own
    /// regional endpoint outside us-east-1 — using the us-east-1-bound
    /// <see cref="S3"/> client against an eu-west-1 bucket fails with
    /// "The bucket you are attempting to access must be addressed using the
    /// specified endpoint." Needed for teardown of the dedicated eu-west-1
    /// bucket created for <c>bucketalreadyownedbyyou-recreate</c> (issue #752).
    /// </summary>
    public IAmazonS3 S3EuWest1 { get; private set; } = null!;

    public IAmazonDynamoDB DynamoDb { get; private set; } = null!;

    public IAmazonKinesis Kinesis { get; private set; } = null!;

    public IAmazonSimpleNotificationService Sns { get; private set; } = null!;

    public IAmazonSQS Sqs { get; private set; } = null!;

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
        S3 = new AmazonS3Client(_credentials, Amazon.RegionEndpoint.USEast1);
        S3EuWest1 = new AmazonS3Client(_credentials, Amazon.RegionEndpoint.EUWest1);
        DynamoDb = new AmazonDynamoDBClient(_credentials, Amazon.RegionEndpoint.USEast1);
        Kinesis = new AmazonKinesisClient(_credentials, Amazon.RegionEndpoint.USEast1);
        Sns = new AmazonSimpleNotificationServiceClient(_credentials, Amazon.RegionEndpoint.USEast1);
        Sqs = new AmazonSQSClient(_credentials, Amazon.RegionEndpoint.USEast1);
        IsConfigured = true;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        (S3 as IDisposable)?.Dispose();
        (S3EuWest1 as IDisposable)?.Dispose();
        (DynamoDb as IDisposable)?.Dispose();
        (Kinesis as IDisposable)?.Dispose();
        (Sns as IDisposable)?.Dispose();
        (Sqs as IDisposable)?.Dispose();
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

    public Task DeleteBucketBestEffortAsync(string bucket) => DeleteBucketBestEffortAsync(S3, bucket);

    public async Task DeleteBucketBestEffortAsync(IAmazonS3 client, string bucket)
    {
        try
        {
            var listing = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
            }).ConfigureAwait(false);
            foreach (var obj in listing.S3Objects)
            {
                await client.DeleteObjectAsync(bucket, obj.Key).ConfigureAwait(false);
            }

            await client.DeleteBucketAsync(bucket).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
    }

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

    /// <summary>
    /// Creates a real SQS queue with a policy allowing any SNS topic in this
    /// account to deliver to it, so that a real-AWS Subscribe(sqs) call
    /// auto-confirms immediately (real AWS - unlike the proxy - only
    /// auto-confirms an sqs-protocol subscription once it can actually
    /// deliver the confirmation handshake to the endpoint; a stub/foreign
    /// ARN leaves the subscription stuck in "PendingConfirmation" forever).
    /// This is purely a real-AWS capture-harness prerequisite: aws2azure
    /// itself never dispatches to subscribers (see docs/gaps/sns/Subscribe.yaml)
    /// and always auto-confirms unconditionally as opaque metadata, so this
    /// queue exists only to let real AWS's own golden response be captured.
    /// </summary>
    public async Task<(string QueueUrl, string QueueArn)> CreateSnsAutoConfirmQueueAsync(string suffix)
    {
        var queueName = CreateEphemeralName(suffix);
        var createResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName,
            Tags = CreateStringTagDictionary(),
        }).ConfigureAwait(false);

        var attributes = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = createResponse.QueueUrl,
            AttributeNames = ["QueueArn"],
        }).ConfigureAwait(false);
        var queueArn = attributes.QueueARN;
        var accountId = queueArn.Split(':')[4];

        var policy = $$"""
            {
              "Version": "2012-10-17",
              "Statement": [
                {
                  "Effect": "Allow",
                  "Principal": { "Service": "sns.amazonaws.com" },
                  "Action": "sqs:SendMessage",
                  "Resource": "{{queueArn}}",
                  "Condition": { "StringEquals": { "aws:SourceAccount": "{{accountId}}" } }
                }
              ]
            }
            """;
        await Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = createResponse.QueueUrl,
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { ["Policy"] = policy },
        }).ConfigureAwait(false);

        return (createResponse.QueueUrl, queueArn);
    }

    public async Task DeleteQueueBestEffortAsync(string queueUrl)
    {
        try
        {
            await Sqs.DeleteQueueAsync(queueUrl).ConfigureAwait(false);
        }
        catch (QueueDoesNotExistException)
        {
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealAwsConformanceCaptureCollection : ICollectionFixture<RealAwsConformanceCaptureFixture>
{
    public const string Name = "real-aws-conformance-capture";
}
