using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Aws2Azure.Conformance.Canonicalization;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Xunit;

namespace Aws2Azure.IntegrationTests.FailureConformance;

[Trait("Category", "RealAzure")]
public sealed class DeterministicHttpFailureConformanceTests
{
    [Fact]
    public async Task S3_injected_backend_failures_return_native_retryable_errors()
    {
        await DeterministicFailureQualification.VerifyS3ScenarioAsync(
            DeterministicFailureQualification.ThrottlingScenarioId);
        await DeterministicFailureQualification.VerifyS3ScenarioAsync(
            DeterministicFailureQualification.TimeoutScenarioId);
        await DeterministicFailureQualification.VerifyS3ScenarioAsync(
            DeterministicFailureQualification.ServiceUnavailableScenarioId);
    }

    [Fact]
    public async Task Sqs_shared_qualification_helper_matches_rest_conformance_mappings()
    {
        // Exercises DeterministicFailureQualification.VerifySqsScenarioAsync
        // directly — the same entry point the SQS real-Azure load runner
        // calls to produce load-evidence scenario rows — so the shared
        // helper is covered independently of the raw wire assertions above.
        await DeterministicFailureQualification.VerifySqsScenarioAsync(
            DeterministicFailureQualification.ThrottlingScenarioId);
        await DeterministicFailureQualification.VerifySqsScenarioAsync(
            DeterministicFailureQualification.TimeoutScenarioId);
        await DeterministicFailureQualification.VerifySqsScenarioAsync(
            DeterministicFailureQualification.ServiceUnavailableScenarioId);
        await DeterministicFailureQualification.VerifySqsScenarioAsync(
            DeterministicFailureQualification.RetryExhaustionScenarioId);
    }

    [Fact]
    public async Task DynamoDb_injected_backend_failures_return_native_retryable_errors()
    {
        await DeterministicFailureQualification.VerifyDynamoDbScenarioAsync(
            DeterministicFailureQualification.ThrottlingScenarioId);
        await DeterministicFailureQualification.VerifyDynamoDbScenarioAsync(
            DeterministicFailureQualification.TimeoutScenarioId);
        await DeterministicFailureQualification.VerifyDynamoDbScenarioAsync(
            DeterministicFailureQualification.ServiceUnavailableScenarioId);
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
        await DeterministicFailureQualification.VerifySecretsManagerScenarioAsync(
            DeterministicFailureQualification.ThrottlingScenarioId);
        await DeterministicFailureQualification.VerifySecretsManagerScenarioAsync(
            DeterministicFailureQualification.TimeoutScenarioId);
        await DeterministicFailureQualification.VerifySecretsManagerScenarioAsync(
            DeterministicFailureQualification.ServiceUnavailableScenarioId);
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
        await DeterministicFailureQualification.VerifyS3ScenarioAsync(
            DeterministicFailureQualification.CancellationScenarioId);
        await DeterministicFailureQualification.VerifySecretsManagerScenarioAsync(
            DeterministicFailureQualification.CancellationScenarioId);
    }

    [Fact]
    public async Task Aws_sdk_retry_exhaustion_is_bounded_for_candidate_profile_operations()
    {
        await DeterministicFailureQualification.VerifyS3ScenarioAsync(
            DeterministicFailureQualification.RetryExhaustionScenarioId);
        await DeterministicFailureQualification.VerifyDynamoDbScenarioAsync(
            DeterministicFailureQualification.RetryExhaustionScenarioId);
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
