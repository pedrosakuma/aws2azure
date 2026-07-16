using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Aws2Azure.Conformance.Canonicalization;
using Xunit;

namespace Aws2Azure.IntegrationTests.FailureConformance;

[Trait("Category", "RealAzure")]
public sealed class DeterministicHttpFailureConformanceTests
{
    [Fact]
    public async Task S3_injected_backend_failures_return_native_retryable_errors()
    {
        using var harness = DeterministicFailureHarness.Create("s3");
        using var sdk = CreateS3Client(harness);
        var cases = new[]
        {
            new FailureCase(HttpStatusCode.TooManyRequests, null, 503, "SlowDown"),
            new FailureCase(HttpStatusCode.RequestTimeout, null, 400, "RequestTimeout"),
            new FailureCase(HttpStatusCode.ServiceUnavailable, null, 503, "ServiceUnavailable"),
        };

        foreach (var failure in cases)
        {
            harness.Backend.PlanStatus(failure.BackendStatus, failure.AzureErrorCode);
            using var response = await harness.RawClient.SendAsync(
                CreateS3ListBucketsRequest(harness.RawClient.BaseAddress!));
            await AssertCanonicalErrorAsync(response, failure, CanonicalResponse.BodyKindXmlError);

            var exception = await Assert.ThrowsAnyAsync<AmazonServiceException>(
                () => sdk.ListBucketsAsync());
            AssertSdkError(exception, failure);
            AssertSdkRetriedOnce(harness);
        }
    }

    [Fact]
    public async Task DynamoDb_injected_backend_failures_return_native_retryable_errors()
    {
        using var harness = DeterministicFailureHarness.Create("dynamodb");
        using var sdk = CreateDynamoDbClient(harness);
        var cases = new[]
        {
            new FailureCase(HttpStatusCode.TooManyRequests, null, 400, "ProvisionedThroughputExceededException"),
            new FailureCase(HttpStatusCode.RequestTimeout, null, 500, "InternalServerError"),
            new FailureCase(HttpStatusCode.ServiceUnavailable, null, 500, "InternalServerError"),
        };

        foreach (var failure in cases)
        {
            harness.Backend.PlanStatus(failure.BackendStatus, failure.AzureErrorCode);
            using var response = await harness.RawClient.SendAsync(
                CreateJsonRequest(
                    harness.RawClient.BaseAddress!,
                    "dynamodb",
                    "DynamoDB_20120810.ListTables",
                    "{}"));
            await AssertCanonicalErrorAsync(response, failure, CanonicalResponse.BodyKindJsonError);

            var exception = await Assert.ThrowsAnyAsync<AmazonServiceException>(
                () => sdk.ListTablesAsync(new ListTablesRequest()));
            AssertSdkError(exception, failure);
            AssertSdkRetriedOnce(harness);
        }
    }

    [Fact]
    public async Task Sqs_injected_rest_failures_cover_query_xml_and_sdk_json_retryability()
    {
        using var harness = DeterministicFailureHarness.Create("sqs");
        using var sdk = CreateSqsClient(harness);
        var cases = new[]
        {
            new FailureCase(HttpStatusCode.TooManyRequests, null, 503, "ServiceUnavailable", "Receiver"),
            new FailureCase(HttpStatusCode.RequestTimeout, null, 500, "InternalFailure", "Receiver"),
            new FailureCase(HttpStatusCode.ServiceUnavailable, null, 502, "ServiceUnavailable", "Receiver"),
        };

        foreach (var failure in cases)
        {
            harness.Backend.PlanStatus(failure.BackendStatus, failure.AzureErrorCode);
            using var response = await harness.RawClient.SendAsync(
                CreateQueryRequest(
                    harness.RawClient.BaseAddress!,
                    "sqs",
                    "Action=ListQueues&Version=2012-11-05"));
            await AssertCanonicalErrorAsync(response, failure, CanonicalResponse.BodyKindXmlError);

            var exception = await Assert.ThrowsAnyAsync<AmazonServiceException>(
                () => sdk.ListQueuesAsync(new ListQueuesRequest()));
            AssertSdkError(exception, failure);
            AssertSdkRetriedOnce(harness);
        }
    }

    [Fact]
    public async Task SecretsManager_injected_backend_failures_return_native_retryable_errors()
    {
        using var harness = DeterministicFailureHarness.Create("secretsmanager");
        using var sdk = CreateSecretsManagerClient(harness);
        var cases = new[]
        {
            new FailureCase(HttpStatusCode.TooManyRequests, null, 429, "ThrottlingException"),
            new FailureCase(HttpStatusCode.RequestTimeout, null, 503, "InternalServiceError"),
            new FailureCase(HttpStatusCode.ServiceUnavailable, null, 503, "InternalServiceError"),
        };

        foreach (var failure in cases)
        {
            harness.Backend.PlanStatus(failure.BackendStatus, failure.AzureErrorCode);
            using var response = await harness.RawClient.SendAsync(
                CreateJsonRequest(
                    harness.RawClient.BaseAddress!,
                    "secretsmanager",
                    "secretsmanager.GetSecretValue",
                    """{"SecretId":"deterministic-failure"}"""));
            await AssertCanonicalErrorAsync(response, failure, CanonicalResponse.BodyKindJsonError);

            var exception = await Assert.ThrowsAnyAsync<AmazonServiceException>(
                () => sdk.GetSecretValueAsync(new GetSecretValueRequest
                {
                    SecretId = "deterministic-failure",
                }));
            AssertSdkError(exception, failure);
            AssertSdkRetriedOnce(harness);
            Assert.InRange(harness.Backend.TokenRequestCount, 0, 1);
        }
    }

    [Fact]
    public async Task SecretsManager_injected_invalid_client_secret_returns_native_non_retryable_error()
    {
        using var harness = DeterministicFailureHarness.Create("secretsmanager");
        using var sdk = CreateSecretsManagerClient(harness);
        harness.Backend.PlanTokenFailure(HttpStatusCode.Unauthorized);
        var failure = new FailureCase(
            HttpStatusCode.Unauthorized,
            null,
            403,
            "AccessDeniedException");

        using var response = await harness.RawClient.SendAsync(
            CreateJsonRequest(
                harness.RawClient.BaseAddress!,
                "secretsmanager",
                "secretsmanager.GetSecretValue",
                """{"SecretId":"deterministic-failure"}"""));
        await AssertCanonicalErrorAsync(response, failure, CanonicalResponse.BodyKindJsonError);

        var exception = await Assert.ThrowsAnyAsync<AmazonServiceException>(
            () => sdk.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = "deterministic-failure",
            }));
        AssertSdkError(exception, failure);
        Assert.Equal(2, harness.Backend.TokenRequestCount);
        Assert.Equal(0, harness.Backend.BackendRequestCount);
    }

    [Fact]
    public async Task Http_client_cancellation_propagates_without_committed_success()
    {
        using var harness = DeterministicFailureHarness.Create("s3");
        harness.Backend.PlanCancellation();
        using var request = CreateS3ListBucketsRequest(harness.RawClient.BaseAddress!);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        var pending = harness.RawClient.SendAsync(request, cancellation.Token);
        await harness.Backend.RequestObserved.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
           () => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        await harness.Backend.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Cancellation took {stopwatch.Elapsed}.");
        var exchange = Assert.IsType<ProxyExchangeSnapshot>(harness.LastProxyExchange);
        Assert.False(exchange.ResponseStarted);
        Assert.Equal(0, exchange.ResponseBodyLength);
    }

    [Fact]
    public async Task Aws_sdk_retry_exhaustion_is_bounded_for_candidate_profile_operations()
    {
        await AssertRetryExhaustionAsync(
            "s3",
            async harness =>
            {
                using var sdk = CreateS3Client(harness, maxErrorRetry: 2);
                await sdk.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = "retry-exhaustion",
                    Key = "object",
                    ContentBody = "payload",
                }).ConfigureAwait(false);
            });
        await AssertRetryExhaustionAsync(
            "dynamodb",
            async harness =>
            {
                using var sdk = CreateDynamoDbClient(harness, maxErrorRetry: 2);
                await sdk.PutItemAsync(new PutItemRequest
                {
                    TableName = "retry-exhaustion",
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["pk"] = new() { S = "value" },
                    },
                }).ConfigureAwait(false);
            });
        await AssertRetryExhaustionAsync(
            "sqs",
            async harness =>
            {
                using var sdk = CreateSqsClient(harness, maxErrorRetry: 2);
                await sdk.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl =
                        "https://sqs.us-east-1.amazonaws.com/000000000000/retry-exhaustion",
                    MessageBody = "payload",
                }).ConfigureAwait(false);
            });
        await AssertRetryExhaustionAsync(
            "secretsmanager",
            async harness =>
            {
                using var sdk = CreateSecretsManagerClient(harness, maxErrorRetry: 2);
                await sdk.GetSecretValueAsync(new GetSecretValueRequest
                {
                    SecretId = "retry-exhaustion",
                }).ConfigureAwait(false);
            });
    }

    private static AmazonS3Client CreateS3Client(
        DeterministicFailureHarness harness,
        int maxErrorRetry = 1) =>
        new(
            DeterministicFailureHarness.AccessKey,
            DeterministicFailureHarness.SecretKey,
            new AmazonS3Config
            {
                ServiceURL = harness.RawClient.BaseAddress!.GetLeftPart(UriPartial.Authority),
                ForcePathStyle = true,
                UseHttp = true,
                AuthenticationRegion = DeterministicFailureHarness.Region,
                MaxErrorRetry = maxErrorRetry,
                HttpClientFactory = harness.AwsHttpClientFactory,
            });

    private static AmazonDynamoDBClient CreateDynamoDbClient(
        DeterministicFailureHarness harness,
        int maxErrorRetry = 1) =>
        new(
            DeterministicFailureHarness.AccessKey,
            DeterministicFailureHarness.SecretKey,
            new AmazonDynamoDBConfig
            {
                ServiceURL = harness.RawClient.BaseAddress!.GetLeftPart(UriPartial.Authority),
                UseHttp = true,
                AuthenticationRegion = DeterministicFailureHarness.Region,
                MaxErrorRetry = maxErrorRetry,
                HttpClientFactory = harness.AwsHttpClientFactory,
            });

    private static AmazonSQSClient CreateSqsClient(
        DeterministicFailureHarness harness,
        int maxErrorRetry = 1) =>
        new(
            DeterministicFailureHarness.AccessKey,
            DeterministicFailureHarness.SecretKey,
            new AmazonSQSConfig
            {
                ServiceURL = harness.RawClient.BaseAddress!.GetLeftPart(UriPartial.Authority),
                UseHttp = true,
                AuthenticationRegion = DeterministicFailureHarness.Region,
                MaxErrorRetry = maxErrorRetry,
                HttpClientFactory = harness.AwsHttpClientFactory,
            });

    private static AmazonSecretsManagerClient CreateSecretsManagerClient(
        DeterministicFailureHarness harness,
        int maxErrorRetry = 1) =>
        new(
            DeterministicFailureHarness.AccessKey,
            DeterministicFailureHarness.SecretKey,
            new AmazonSecretsManagerConfig
            {
                RegionEndpoint = RegionEndpoint.USEast1,
                ServiceURL = harness.RawClient.BaseAddress!.GetLeftPart(UriPartial.Authority),
                UseHttp = true,
                AuthenticationRegion = DeterministicFailureHarness.Region,
                MaxErrorRetry = maxErrorRetry,
                HttpClientFactory = harness.AwsHttpClientFactory,
            });

    private static HttpRequestMessage CreateS3ListBucketsRequest(Uri baseAddress)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, "/"));
        TestSigV4Signer.SignHeader(
            request,
            [],
            DeterministicFailureHarness.AccessKey,
            DeterministicFailureHarness.SecretKey,
            DeterministicFailureHarness.Region,
            "s3");
        return request;
    }

    private static HttpRequestMessage CreateJsonRequest(
        Uri baseAddress,
        string service,
        string target,
        string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, "/"))
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.0");
        request.Headers.TryAddWithoutValidation("X-Amz-Target", target);
        TestSigV4Signer.SignHeader(
            request,
            bytes,
            DeterministicFailureHarness.AccessKey,
            DeterministicFailureHarness.SecretKey,
            DeterministicFailureHarness.Region,
            service,
            extraSignedHeaders: ["x-amz-target"]);
        return request;
    }

    private static HttpRequestMessage CreateQueryRequest(
        Uri baseAddress,
        string service,
        string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, "/"))
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        TestSigV4Signer.SignHeader(
            request,
            bytes,
            DeterministicFailureHarness.AccessKey,
            DeterministicFailureHarness.SecretKey,
            DeterministicFailureHarness.Region,
            service);
        return request;
    }

    private static async Task AssertCanonicalErrorAsync(
        HttpResponseMessage response,
        FailureCase failure,
        string expectedBodyKind)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var headers = response.Headers
            .Concat(response.Content.Headers)
            .SelectMany(static header => header.Value.Select(
                value => new KeyValuePair<string, string>(header.Key, value)));
        var canonical = AwsErrorCanonicalizer.Canonicalize(
            (int)response.StatusCode,
            headers,
            body);

        Assert.Equal(failure.ExpectedStatus, canonical.StatusCode);
        Assert.Equal(expectedBodyKind, canonical.BodyKind);
        Assert.Contains(
            canonical.BodyFields,
            field => field.Name == "Code" && field.Value == failure.ExpectedCode);
        Assert.Contains(canonical.BodyFields, field => field.Name == "Message");
        if (failure.ExpectedFaultType is not null)
        {
            Assert.Contains(
                canonical.BodyFields,
                field => field.Name == "Type" && field.Value == failure.ExpectedFaultType);
        }
        Assert.False(string.IsNullOrWhiteSpace(canonical.RawBody));
    }

    private static void AssertSdkError(
        AmazonServiceException exception,
        FailureCase failure)
    {
        Assert.Equal(failure.ExpectedStatus, (int)exception.StatusCode);
        Assert.Equal(failure.ExpectedCode, exception.ErrorCode);
    }

    private static void AssertSdkRetriedOnce(DeterministicFailureHarness harness)
    {
        // One raw wire assertion, then the SDK's initial attempt plus one retry.
        Assert.Equal(3, harness.Backend.BackendRequestCount);
    }

    private static async Task AssertRetryExhaustionAsync(
        string service,
        Func<DeterministicFailureHarness, Task> operation)
    {
        using var harness = DeterministicFailureHarness.Create(service);
        harness.Backend.PlanStatus(HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAnyAsync<AmazonServiceException>(() => operation(harness));

        Assert.Equal(3, harness.Backend.BackendRequestCount);
    }

    private sealed record FailureCase(
        HttpStatusCode BackendStatus,
        string? AzureErrorCode,
        int ExpectedStatus,
        string ExpectedCode,
        string? ExpectedFaultType = null);
}
