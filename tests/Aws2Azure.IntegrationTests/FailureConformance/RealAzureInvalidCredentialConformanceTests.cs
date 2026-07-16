using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Aws2Azure.Conformance.Canonicalization;
using Aws2Azure.IntegrationTests.Sns;
using Xunit;

namespace Aws2Azure.IntegrationTests.FailureConformance;

[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class RealAzureInvalidCredentialConformanceTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task S3_invalid_shared_key_returns_native_non_retryable_error()
    {
        Skip.IfNot(
            fixture.BlobConfigured,
            "AZURE_BLOB_ACCOUNT/AZURE_BLOB_KEY not set — skipping real-Azure invalid-credential conformance.");

        using var rawClient = CreateRawClient(fixture.GetServiceUrl("s3"));
        using var rawRequest = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(rawClient.BaseAddress!, "/"));
        Sign(rawRequest, [], "s3");
        using var rawResponse = await rawClient.SendAsync(rawRequest);
        await AssertCanonicalErrorAsync(
            rawResponse,
            HttpStatusCode.Forbidden,
            "AccessDenied",
            CanonicalResponse.BodyKindXmlError);

        var counter = new CountingSdkHttpClientFactory();
        using var sdk = new AmazonS3Client(
            RealAzureProxyFixture.InvalidBackendAwsAccessKey,
            RealAzureProxyFixture.InvalidBackendAwsSecret,
            new AmazonS3Config
            {
                ServiceURL = fixture.GetServiceUrl("s3"),
                ForcePathStyle = true,
                UseHttp = true,
                AuthenticationRegion = "us-east-1",
                MaxErrorRetry = 1,
                HttpClientFactory = counter,
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var exception = await Assert.ThrowsAnyAsync<AmazonServiceException>(
            () => sdk.ListBucketsAsync(timeout.Token));
        AssertNonRetryableSdkError(exception, counter, HttpStatusCode.Forbidden, "AccessDenied");
    }

    [SkippableFact]
    public async Task DynamoDb_invalid_primary_key_returns_native_non_retryable_error()
    {
        Skip.IfNot(
            fixture.CosmosConfigured || fixture.CosmosWorkloadIdentityConfigured,
            "AZURE_COSMOS_ENDPOINT/DATABASE not set — skipping real-Azure invalid-credential conformance.");

        using var rawClient = CreateRawClient(fixture.GetServiceUrl("dynamodb"));
        using var rawRequest = CreateJsonRequest(
            rawClient.BaseAddress!,
            "dynamodb",
            "DynamoDB_20120810.ListTables",
            "{}");
        using var rawResponse = await rawClient.SendAsync(rawRequest);
        await AssertCanonicalErrorAsync(
            rawResponse,
            HttpStatusCode.BadRequest,
            "AccessDeniedException",
            CanonicalResponse.BodyKindJsonError);

        var counter = new CountingSdkHttpClientFactory();
        using var sdk = new AmazonDynamoDBClient(
            RealAzureProxyFixture.InvalidBackendAwsAccessKey,
            RealAzureProxyFixture.InvalidBackendAwsSecret,
            new AmazonDynamoDBConfig
            {
                ServiceURL = fixture.GetServiceUrl("dynamodb"),
                UseHttp = true,
                AuthenticationRegion = "us-east-1",
                MaxErrorRetry = 1,
                HttpClientFactory = counter,
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var exception = await Assert.ThrowsAnyAsync<AmazonServiceException>(
            () => sdk.ListTablesAsync(new ListTablesRequest(), timeout.Token));
        AssertNonRetryableSdkError(
            exception,
            counter,
            HttpStatusCode.BadRequest,
            "AccessDeniedException");
    }

    [SkippableFact]
    public async Task Sqs_invalid_sas_key_returns_native_non_retryable_error()
    {
        Skip.IfNot(
            fixture.ServiceBusConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure invalid-credential conformance.");

        using var rawClient = CreateRawClient(fixture.GetServiceUrl("sqs"));
        using var rawRequest = CreateQueryRequest(
            rawClient.BaseAddress!,
            "sqs",
            "Action=ListQueues&Version=2012-11-05");
        using var rawResponse = await rawClient.SendAsync(rawRequest);
        await AssertCanonicalErrorAsync(
            rawResponse,
            HttpStatusCode.Forbidden,
            "AccessDenied",
            CanonicalResponse.BodyKindXmlError,
            "Sender");

        var counter = new CountingSdkHttpClientFactory();
        using var sdk = new AmazonSQSClient(
            RealAzureProxyFixture.InvalidBackendAwsAccessKey,
            RealAzureProxyFixture.InvalidBackendAwsSecret,
            new AmazonSQSConfig
            {
                ServiceURL = fixture.GetServiceUrl("sqs"),
                UseHttp = true,
                AuthenticationRegion = "us-east-1",
                MaxErrorRetry = 1,
                HttpClientFactory = counter,
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var exception = await Assert.ThrowsAnyAsync<AmazonServiceException>(
            () => sdk.ListQueuesAsync(new ListQueuesRequest(), timeout.Token));
        AssertNonRetryableSdkError(exception, counter, HttpStatusCode.Forbidden, "AccessDenied");
    }

    [SkippableFact]
    public async Task Kinesis_invalid_sas_key_returns_native_non_retryable_error()
    {
        Skip.IfNot(
            fixture.EventHubsConfigured || fixture.EventHubsWorkloadIdentityConfigured,
            "AZURE_EVENTHUBS_* / AZURE_EVENTHUBS_STREAM not set — skipping real-Azure invalid-credential conformance.");

        var body = $$"""{"StreamName":"{{fixture.EventHubStream}}","Data":"YQ==","PartitionKey":"invalid-auth"}""";
        using var rawClient = CreateRawClient(fixture.GetServiceUrl("kinesis"));
        using var rawRequest = CreateJsonRequest(
            rawClient.BaseAddress!,
            "kinesis",
            "Kinesis_20131202.PutRecord",
            body);
        using var rawResponse = await rawClient.SendAsync(rawRequest);
        await AssertCanonicalErrorAsync(
            rawResponse,
            HttpStatusCode.Forbidden,
            "AccessDeniedException",
            CanonicalResponse.BodyKindJsonError);

        var counter = new CountingSdkHttpClientFactory();
        using var sdk = new AmazonKinesisClient(
            RealAzureProxyFixture.InvalidBackendAwsAccessKey,
            RealAzureProxyFixture.InvalidBackendAwsSecret,
            new AmazonKinesisConfig
            {
                ServiceURL = fixture.GetServiceUrl("kinesis"),
                UseHttp = true,
                AuthenticationRegion = "us-east-1",
                MaxErrorRetry = 1,
                HttpClientFactory = counter,
            });
        using var payload = new MemoryStream("a"u8.ToArray());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var exception = await Assert.ThrowsAnyAsync<AmazonServiceException>(
            () => sdk.PutRecordAsync(new PutRecordRequest
            {
                StreamName = fixture.EventHubStream,
                Data = payload,
                PartitionKey = "invalid-auth",
            }, timeout.Token));
        AssertNonRetryableSdkError(
            exception,
            counter,
            HttpStatusCode.Forbidden,
            "AccessDeniedException");
    }

    [SkippableFact]
    public async Task Sns_invalid_sas_key_returns_native_non_retryable_error()
    {
        Skip.IfNot(
            fixture.SnsConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure invalid-credential conformance.");

        using var client = fixture.CreateSnsClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var topicName = SnsQueryApiClient.CreateTopicName("sns-invalid-auth");
        string? topicArn = null;
        try
        {
            var created = await SnsQueryApiClient.SendActionAsync(
                client,
                "CreateTopic",
                [new("Name", topicName)],
                RealAzureProxyFixture.AwsAccessKey,
                RealAzureProxyFixture.AwsSecret);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            topicArn = SnsQueryApiClient.ReadTopicArn(created);

            var response = await SnsQueryApiClient.SendActionAsync(
                client,
                "Publish",
                [
                    new("TopicArn", topicArn),
                    new("Message", "must-not-be-published"),
                ],
                RealAzureProxyFixture.InvalidBackendAwsAccessKey,
                RealAzureProxyFixture.InvalidBackendAwsSecret);

            AssertCanonicalError(
                (int)response.StatusCode,
                response.Body,
                HttpStatusCode.Forbidden,
                "AuthorizationError",
                CanonicalResponse.BodyKindXmlError,
                "Sender");
        }
        finally
        {
            if (topicArn is not null)
            {
                try
                {
                    await SnsQueryApiClient.SendActionAsync(
                        client,
                        "DeleteTopic",
                        [new("TopicArn", topicArn)],
                        RealAzureProxyFixture.AwsAccessKey,
                        RealAzureProxyFixture.AwsSecret);
                }
                catch
                {
                }
            }
        }
    }

    private static HttpClient CreateRawClient(string serviceUrl) => new()
    {
        BaseAddress = new Uri(serviceUrl),
        Timeout = TimeSpan.FromSeconds(30),
    };

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
        Sign(request, bytes, service, ["x-amz-target"]);
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
        Sign(request, bytes, service);
        return request;
    }

    private static void Sign(
        HttpRequestMessage request,
        byte[] body,
        string service,
        IReadOnlyList<string>? extraSignedHeaders = null) =>
        TestSigV4Signer.SignHeader(
            request,
            body,
            RealAzureProxyFixture.InvalidBackendAwsAccessKey,
            RealAzureProxyFixture.InvalidBackendAwsSecret,
            "us-east-1",
            service,
            extraSignedHeaders: extraSignedHeaders);

    private static async Task AssertCanonicalErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string expectedBodyKind,
        string? expectedFaultType = null)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var headers = response.Headers
            .Concat(response.Content.Headers)
            .SelectMany(static header => header.Value.Select(
                value => new KeyValuePair<string, string>(header.Key, value)));
        AssertCanonicalError(
            (int)response.StatusCode,
            body,
            expectedStatus,
            expectedCode,
            expectedBodyKind,
            expectedFaultType,
            headers);
    }

    private static void AssertCanonicalError(
        int statusCode,
        string body,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string expectedBodyKind,
        string? expectedFaultType = null,
        IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        var canonical = AwsErrorCanonicalizer.Canonicalize(
            statusCode,
            headers ?? [],
            body);
        Assert.Equal((int)expectedStatus, canonical.StatusCode);
        Assert.Equal(expectedBodyKind, canonical.BodyKind);
        Assert.Contains(
            canonical.BodyFields,
            field => field.Name == "Code" && field.Value == expectedCode);
        Assert.Contains(canonical.BodyFields, field => field.Name == "Message");
        if (expectedFaultType is not null)
        {
            Assert.Contains(
                canonical.BodyFields,
                field => field.Name == "Type" && field.Value == expectedFaultType);
        }
        Assert.False(string.IsNullOrWhiteSpace(canonical.RawBody));
    }

    private static void AssertNonRetryableSdkError(
        AmazonServiceException exception,
        CountingSdkHttpClientFactory counter,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, exception.StatusCode);
        Assert.Equal(expectedCode, exception.ErrorCode);
        Assert.Equal(1, counter.RequestCount);
    }
}

[Trait("Category", "RealAzure")]
[Collection(SecretsManagerRealAzureCollection.Name)]
public sealed class SecretsManagerRealAzureInvalidCredentialConformanceTests(
    SecretsManagerRealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task Invalid_client_secret_returns_native_non_retryable_error()
    {
        Skip.If(!fixture.Configured, fixture.SkipReason ?? "Key Vault real-Azure fixture is not configured.");

        using var rawClient = new HttpClient
        {
            BaseAddress = new Uri(fixture.ProxyServiceUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var body = """{"SecretId":"invalid-backend-auth"}""";
        var bytes = Encoding.UTF8.GetBytes(body);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(rawClient.BaseAddress!, "/"))
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.0");
        request.Headers.TryAddWithoutValidation("X-Amz-Target", "secretsmanager.GetSecretValue");
        TestSigV4Signer.SignHeader(
            request,
            bytes,
            SecretsManagerRealAzureProxyFixture.InvalidBackendAwsAccessKey,
            SecretsManagerRealAzureProxyFixture.InvalidBackendAwsSecret,
            "us-east-1",
            "secretsmanager",
            extraSignedHeaders: ["x-amz-target"]);

        using var response = await rawClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        var canonical = AwsErrorCanonicalizer.Canonicalize(
            (int)response.StatusCode,
            [],
            responseBody);
        Assert.Equal((int)HttpStatusCode.Forbidden, canonical.StatusCode);
        Assert.Equal(CanonicalResponse.BodyKindJsonError, canonical.BodyKind);
        Assert.Contains(
            canonical.BodyFields,
            field => field.Name == "Code" && field.Value == "AccessDeniedException");
        Assert.Contains(canonical.BodyFields, field => field.Name == "Message");

        var counter = new CountingSdkHttpClientFactory();
        using var sdk = new AmazonSecretsManagerClient(
            SecretsManagerRealAzureProxyFixture.InvalidBackendAwsAccessKey,
            SecretsManagerRealAzureProxyFixture.InvalidBackendAwsSecret,
            new AmazonSecretsManagerConfig
            {
                RegionEndpoint = RegionEndpoint.USEast1,
                ServiceURL = fixture.ProxyServiceUrl,
                UseHttp = true,
                AuthenticationRegion = "us-east-1",
                MaxErrorRetry = 1,
                HttpClientFactory = counter,
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var exception = await Assert.ThrowsAnyAsync<AmazonServiceException>(
            () => sdk.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = "invalid-backend-auth",
            }, timeout.Token));
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal("AccessDeniedException", exception.ErrorCode);
        Assert.Equal(1, counter.RequestCount);
    }
}

internal sealed class CountingSdkHttpClientFactory : HttpClientFactory
{
    private int _requestCount;

    public int RequestCount => Volatile.Read(ref _requestCount);

    public override HttpClient CreateHttpClient(IClientConfig clientConfig) =>
        new(new CountingHttpMessageHandler(this), disposeHandler: true);

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;

    private sealed class CountingHttpMessageHandler(CountingSdkHttpClientFactory owner)
        : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner._requestCount);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
