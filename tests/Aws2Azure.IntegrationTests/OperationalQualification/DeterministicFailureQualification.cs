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
using Aws2Azure.Conformance.Canonicalization;
using Aws2Azure.IntegrationTests.FailureConformance;
using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal static class DeterministicFailureQualification
{
    public const string ThrottlingScenarioId = "throttling";
    public const string TimeoutScenarioId = "timeout";
    public const string ServiceUnavailableScenarioId = "service-unavailable";
    public const string CancellationScenarioId = "cancellation";
    public const string RetryExhaustionScenarioId = "retry-exhaustion";

    public static async Task VerifyS3ScenarioAsync(string scenarioId)
    {
        switch (scenarioId)
        {
            case ThrottlingScenarioId:
                await VerifyS3FailureAsync(new FailureCase(
                    HttpStatusCode.TooManyRequests,
                    null,
                    503,
                    "SlowDown")).ConfigureAwait(false);
                break;
            case TimeoutScenarioId:
                await VerifyS3FailureAsync(new FailureCase(
                    HttpStatusCode.RequestTimeout,
                    null,
                    400,
                    "RequestTimeout")).ConfigureAwait(false);
                break;
            case ServiceUnavailableScenarioId:
                await VerifyS3FailureAsync(new FailureCase(
                    HttpStatusCode.ServiceUnavailable,
                    null,
                    503,
                    "ServiceUnavailable")).ConfigureAwait(false);
                break;
            case CancellationScenarioId:
                await VerifyCancellationAsync(
                    "s3",
                    static baseAddress => CreateS3GetObjectRequest(baseAddress))
                    .ConfigureAwait(false);
                break;
            case RetryExhaustionScenarioId:
                await VerifyS3RetryExhaustionAsync().ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(scenarioId),
                    scenarioId,
                    "Unsupported S3 deterministic qualification scenario.");
        }
    }

    public static async Task VerifySecretsManagerScenarioAsync(string scenarioId)
    {
        switch (scenarioId)
        {
            case ThrottlingScenarioId:
                await VerifySecretsManagerFailureAsync(new FailureCase(
                    HttpStatusCode.TooManyRequests,
                    null,
                    429,
                    "ThrottlingException")).ConfigureAwait(false);
                break;
            case TimeoutScenarioId:
                await VerifySecretsManagerFailureAsync(new FailureCase(
                    HttpStatusCode.RequestTimeout,
                    null,
                    503,
                    "InternalServiceError")).ConfigureAwait(false);
                break;
            case ServiceUnavailableScenarioId:
                await VerifySecretsManagerFailureAsync(new FailureCase(
                    HttpStatusCode.ServiceUnavailable,
                    null,
                    503,
                    "InternalServiceError")).ConfigureAwait(false);
                break;
            case CancellationScenarioId:
                await VerifyCancellationAsync(
                    "secretsmanager",
                    static baseAddress => CreateSecretsManagerGetSecretValueRequest(baseAddress))
                    .ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(scenarioId),
                    scenarioId,
                    "Unsupported Secrets Manager deterministic qualification scenario.");
        }
    }

    public static async Task VerifyDynamoDbScenarioAsync(string scenarioId)
    {
        switch (scenarioId)
        {
            case ThrottlingScenarioId:
                await VerifyDynamoDbFailureAsync(new FailureCase(
                    HttpStatusCode.TooManyRequests,
                    null,
                    400,
                    "ProvisionedThroughputExceededException")).ConfigureAwait(false);
                break;
            case TimeoutScenarioId:
                await VerifyDynamoDbFailureAsync(new FailureCase(
                    HttpStatusCode.RequestTimeout,
                    null,
                    500,
                    "InternalServerError")).ConfigureAwait(false);
                break;
            case ServiceUnavailableScenarioId:
                await VerifyDynamoDbFailureAsync(new FailureCase(
                    HttpStatusCode.ServiceUnavailable,
                    null,
                    500,
                    "InternalServerError")).ConfigureAwait(false);
                break;
            case RetryExhaustionScenarioId:
                await VerifyDynamoDbRetryExhaustionAsync().ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(scenarioId),
                    scenarioId,
                    "Unsupported DynamoDB deterministic qualification scenario.");
        }
    }

    private static async Task VerifyDynamoDbFailureAsync(FailureCase failure)
    {
        using var harness = DeterministicFailureHarness.Create("dynamodb");
        using var sdk = CreateDynamoDbClient(harness);
        harness.Backend.PlanStatus(failure.BackendStatus, failure.AzureErrorCode);

        using var response = await harness.RawClient.SendAsync(
            CreateDynamoDbGetItemRequest(harness.RawClient.BaseAddress!)).ConfigureAwait(false);
        await AssertCanonicalErrorAsync(
            response,
            failure,
            CanonicalResponse.BodyKindJsonError).ConfigureAwait(false);

        var exception = await Assert.ThrowsAnyAsync<AmazonServiceException>(
            () => sdk.GetItemAsync(new GetItemRequest
            {
                TableName = "deterministic-failure",
                Key = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new AttributeValue { S = "item" },
                },
                ConsistentRead = true,
            }));
        AssertSdkError(exception, failure);
        AssertSdkRetriedOnce(harness);
    }

    private static async Task VerifyDynamoDbRetryExhaustionAsync()
    {
        using var harness = DeterministicFailureHarness.Create("dynamodb");
        harness.Backend.PlanStatus(HttpStatusCode.ServiceUnavailable);
        using var sdk = CreateDynamoDbClient(harness, maxErrorRetry: 2);

        await Assert.ThrowsAnyAsync<AmazonServiceException>(
            () => sdk.PutItemAsync(new PutItemRequest
            {
                TableName = "retry-exhaustion",
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new AttributeValue { S = "item" },
                },
            }));

        Assert.Equal(3, harness.Backend.BackendRequestCount);
    }

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

    private static HttpRequestMessage CreateDynamoDbGetItemRequest(Uri baseAddress)
    {
        const string body = """{"TableName":"deterministic-failure","Key":{"pk":{"S":"item"}},"ConsistentRead":true}""";
        var bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, "/"))
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.0");
        request.Headers.TryAddWithoutValidation(
            "X-Amz-Target",
            "DynamoDB_20120810.GetItem");
        TestSigV4Signer.SignHeader(
            request,
            bytes,
            DeterministicFailureHarness.AccessKey,
            DeterministicFailureHarness.SecretKey,
            DeterministicFailureHarness.Region,
            "dynamodb",
            extraSignedHeaders: ["x-amz-target"]);
        return request;
    }

    private static async Task VerifyS3FailureAsync(FailureCase failure)
    {
        using var harness = DeterministicFailureHarness.Create("s3");
        using var sdk = CreateS3Client(harness);
        harness.Backend.PlanStatus(failure.BackendStatus, failure.AzureErrorCode);

        using var response = await harness.RawClient.SendAsync(
            CreateS3GetObjectRequest(harness.RawClient.BaseAddress!)).ConfigureAwait(false);
        await AssertCanonicalErrorAsync(
            response,
            failure,
            CanonicalResponse.BodyKindXmlError).ConfigureAwait(false);

        var exception = await Assert.ThrowsAnyAsync<AmazonServiceException>(
            () => sdk.GetObjectAsync(new GetObjectRequest
            {
                BucketName = "deterministic-failure",
                Key = "object",
            }));
        AssertSdkError(exception, failure);
        AssertSdkRetriedOnce(harness);
    }

    private static async Task VerifySecretsManagerFailureAsync(FailureCase failure)
    {
        using var harness = DeterministicFailureHarness.Create("secretsmanager");
        using var sdk = CreateSecretsManagerClient(harness);
        harness.Backend.PlanStatus(failure.BackendStatus, failure.AzureErrorCode);

        using var response = await harness.RawClient.SendAsync(
            CreateSecretsManagerGetSecretValueRequest(harness.RawClient.BaseAddress!))
            .ConfigureAwait(false);
        await AssertCanonicalErrorAsync(
            response,
            failure,
            CanonicalResponse.BodyKindJsonError).ConfigureAwait(false);

        var exception = await Assert.ThrowsAnyAsync<AmazonServiceException>(
            () => sdk.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = "deterministic-failure",
            }));
        AssertSdkError(exception, failure);
        AssertSdkRetriedOnce(harness);
        Assert.InRange(harness.Backend.TokenRequestCount, 0, 1);
    }

    private static async Task VerifyCancellationAsync(
        string service,
        Func<Uri, HttpRequestMessage> createRequest)
    {
        using var harness = DeterministicFailureHarness.Create(service);
        harness.Backend.PlanCancellation();
        using var request = createRequest(harness.RawClient.BaseAddress!);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        var pending = harness.RawClient.SendAsync(request, cancellation.Token);
        await harness.Backend.RequestObserved.WaitAsync(TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        await harness.Backend.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Cancellation took {stopwatch.Elapsed}.");
        var exchange = Assert.IsType<ProxyExchangeSnapshot>(harness.LastProxyExchange);
        Assert.False(exchange.ResponseStarted);
        Assert.Equal(0, exchange.ResponseBodyLength);
    }

    private static async Task VerifyS3RetryExhaustionAsync()
    {
        using var harness = DeterministicFailureHarness.Create("s3");
        harness.Backend.PlanStatus(HttpStatusCode.ServiceUnavailable);
        using var sdk = CreateS3Client(harness, maxErrorRetry: 2);

        await Assert.ThrowsAnyAsync<AmazonServiceException>(
            () => sdk.PutObjectAsync(new PutObjectRequest
            {
                BucketName = "retry-exhaustion",
                Key = "object",
                ContentBody = "payload",
            }));

        Assert.Equal(3, harness.Backend.BackendRequestCount);
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

    private static AmazonSecretsManagerClient CreateSecretsManagerClient(
        DeterministicFailureHarness harness) =>
        new(
            DeterministicFailureHarness.AccessKey,
            DeterministicFailureHarness.SecretKey,
            new AmazonSecretsManagerConfig
            {
                RegionEndpoint = RegionEndpoint.USEast1,
                ServiceURL = harness.RawClient.BaseAddress!.GetLeftPart(UriPartial.Authority),
                UseHttp = true,
                AuthenticationRegion = DeterministicFailureHarness.Region,
                MaxErrorRetry = 1,
                HttpClientFactory = harness.AwsHttpClientFactory,
            });

    private static HttpRequestMessage CreateS3GetObjectRequest(Uri baseAddress)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(baseAddress, "/deterministic-failure/object"));
        TestSigV4Signer.SignHeader(
            request,
            [],
            DeterministicFailureHarness.AccessKey,
            DeterministicFailureHarness.SecretKey,
            DeterministicFailureHarness.Region,
            "s3");
        return request;
    }

    private static HttpRequestMessage CreateSecretsManagerGetSecretValueRequest(Uri baseAddress)
    {
        const string body = """{"SecretId":"deterministic-failure"}""";
        var bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, "/"))
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.0");
        request.Headers.TryAddWithoutValidation(
            "X-Amz-Target",
            "secretsmanager.GetSecretValue");
        TestSigV4Signer.SignHeader(
            request,
            bytes,
            DeterministicFailureHarness.AccessKey,
            DeterministicFailureHarness.SecretKey,
            DeterministicFailureHarness.Region,
            "secretsmanager",
            extraSignedHeaders: ["x-amz-target"]);
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

    private sealed record FailureCase(
        HttpStatusCode BackendStatus,
        string? AzureErrorCode,
        int ExpectedStatus,
        string ExpectedCode);
}
