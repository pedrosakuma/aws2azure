using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DynamoDbResourceNotFoundException = Amazon.DynamoDBv2.Model.ResourceNotFoundException;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Aws2Azure.TestSupport.OperationalQualification;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal sealed record RealAzureRollbackResult(
    double DurationSeconds,
    DateTimeOffset CapturedAtUtc,
    RealAzureRollbackProof Proof);

internal static class RealAzureRollbackQualification
{
    private static readonly TimeSpan AbsenceTimeout = TimeSpan.FromMinutes(1);

    public static async Task<RealAzureRollbackResult> VerifyDynamoDbAsync(
        DynamoDbRealAzureProxyFixture fixture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (!fixture.SealedRollbackConfigured)
        {
            throw new InvalidOperationException(
                "Real rollback requires verified candidate and prior sealed runtimes.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var table = "a2a-rollback-" + Guid.NewGuid().ToString("N")[..20];
        const string key = "canary";
        var canary = "dynamodb-rollback-" + Guid.NewGuid().ToString("N");
        var canaryDigest = Digest(canary);
        var tableCreated = false;
        var candidateRestored = false;
        var candidateStoppedForRollback = false;
        AmazonDynamoDBClient? priorClient = null;

        using var candidateClient = fixture.CreateDynamoDbClient(maxErrorRetry: 0);
        try
        {
            await candidateClient.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }, cancellationToken).ConfigureAwait(false);
            tableCreated = true;
            await WaitForTableActiveAsync(candidateClient, table, cancellationToken)
                .ConfigureAwait(false);

            await candidateClient.PutItemAsync(
                new PutItemRequest
                {
                    TableName = table,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["pk"] = new AttributeValue { S = key },
                        ["payload"] = new AttributeValue { S = canary },
                    },
                },
                cancellationToken).ConfigureAwait(false);
            var candidateCreateCompletedAt = DateTimeOffset.UtcNow;
            await AssertDynamoDbValueAsync(
                candidateClient,
                table,
                key,
                canary,
                cancellationToken).ConfigureAwait(false);
            var candidateReadCompletedAt = DateTimeOffset.UtcNow;

            await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
            candidateStoppedForRollback = true;
            var candidateStoppedAt = DateTimeOffset.UtcNow;
            await fixture.StartRuntimeAsync(SealedRuntimeRole.Prior).ConfigureAwait(false);
            var priorStartedAt = DateTimeOffset.UtcNow;
            priorClient = fixture.CreateDynamoDbClient(maxErrorRetry: 0);

            await AssertDynamoDbValueAsync(
                priorClient,
                table,
                key,
                canary,
                cancellationToken).ConfigureAwait(false);
            var priorReadCompletedAt = DateTimeOffset.UtcNow;
            var cleanupRequestedAt = TimestampAfter(priorReadCompletedAt);
            await priorClient.DeleteTableAsync(
                new DeleteTableRequest { TableName = table },
                cancellationToken).ConfigureAwait(false);
            tableCreated = false;
            await AssertDynamoDbTableAbsentAsync(
                priorClient,
                table,
                cancellationToken).ConfigureAwait(false);
            var cleanupVerifiedAt = DateTimeOffset.UtcNow;

            await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
            priorClient.Dispose();
            priorClient = null;
            await fixture.StartRuntimeAsync(SealedRuntimeRole.Candidate).ConfigureAwait(false);
            candidateRestored = true;
            candidateStoppedForRollback = false;
            var candidateRestoredAt = DateTimeOffset.UtcNow;
            var completedAt = TimestampAfter(candidateRestoredAt);

            return new RealAzureRollbackResult(
                stopwatch.Elapsed.TotalSeconds,
                completedAt,
                Proof(
                    "dynamodb",
                    "GetItem",
                    fixture,
                    canaryDigest,
                    "delete_table_verify_resource_not_found_exception",
                    startedAt,
                    candidateCreateCompletedAt,
                    candidateReadCompletedAt,
                    candidateStoppedAt,
                    priorStartedAt,
                    priorReadCompletedAt,
                    cleanupRequestedAt,
                    cleanupVerifiedAt,
                    candidateRestoredAt,
                    completedAt));
        }
        finally
        {
            priorClient?.Dispose();
            priorClient = null;
            if (candidateStoppedForRollback && !candidateRestored)
            {
                if (fixture.ProxyStarted)
                {
                    await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
                }
                await fixture.StartRuntimeAsync(SealedRuntimeRole.Candidate)
                    .ConfigureAwait(false);
            }

            if (tableCreated)
            {
                try
                {
                    await candidateClient.DeleteTableAsync(
                        new DeleteTableRequest { TableName = table },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    public static async Task<RealAzureRollbackResult> VerifyS3Async(
        RealAzureProxyFixture fixture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (!fixture.SealedRollbackConfigured)
        {
            throw new InvalidOperationException(
                "Real rollback requires verified candidate and prior sealed runtimes.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var bucket = "a2a-rollback-" + Guid.NewGuid().ToString("N")[..24];
        const string key = "canary/state.txt";
        var canary = "s3-rollback-" + Guid.NewGuid().ToString("N");
        var canaryDigest = Digest(canary);
        var bucketCreated = false;
        var objectCreated = false;
        var candidateRestored = false;
        var candidateStoppedForRollback = false;
        AmazonS3Client? priorClient = null;

        using var candidateClient = fixture.CreateS3Client(maxErrorRetry: 0);
        try
        {
            await candidateClient.PutBucketAsync(
                new PutBucketRequest { BucketName = bucket },
                cancellationToken).ConfigureAwait(false);
            bucketCreated = true;
            await candidateClient.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    ContentBody = canary,
                    ContentType = "text/plain",
                },
                cancellationToken).ConfigureAwait(false);
            objectCreated = true;
            var candidateCreateCompletedAt = DateTimeOffset.UtcNow;
            await AssertS3ValueAsync(
                candidateClient,
                bucket,
                key,
                canary,
                cancellationToken).ConfigureAwait(false);
            var candidateReadCompletedAt = DateTimeOffset.UtcNow;

            await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
            candidateStoppedForRollback = true;
            var candidateStoppedAt = DateTimeOffset.UtcNow;
            await fixture.StartRuntimeAsync(SealedRuntimeRole.Prior).ConfigureAwait(false);
            var priorStartedAt = DateTimeOffset.UtcNow;
            priorClient = fixture.CreateS3Client(maxErrorRetry: 0);

            await AssertS3ValueAsync(
                priorClient,
                bucket,
                key,
                canary,
                cancellationToken).ConfigureAwait(false);
            var priorReadCompletedAt = DateTimeOffset.UtcNow;
            var cleanupRequestedAt = TimestampAfter(priorReadCompletedAt);
            await priorClient.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = bucket, Key = key },
                cancellationToken).ConfigureAwait(false);
            objectCreated = false;
            await priorClient.DeleteBucketAsync(
                new DeleteBucketRequest { BucketName = bucket },
                cancellationToken).ConfigureAwait(false);
            bucketCreated = false;
            await AssertS3AbsentAsync(
                priorClient,
                bucket,
                key,
                cancellationToken).ConfigureAwait(false);
            var cleanupVerifiedAt = DateTimeOffset.UtcNow;

            await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
            priorClient.Dispose();
            priorClient = null;
            await fixture.StartRuntimeAsync(SealedRuntimeRole.Candidate).ConfigureAwait(false);
            candidateRestored = true;
            candidateStoppedForRollback = false;
            var candidateRestoredAt = DateTimeOffset.UtcNow;
            var completedAt = TimestampAfter(candidateRestoredAt);

            return new RealAzureRollbackResult(
                stopwatch.Elapsed.TotalSeconds,
                completedAt,
                Proof(
                    "s3",
                    "GetObject",
                    fixture,
                    canaryDigest,
                    "delete_object_delete_bucket_verify_no_such_bucket",
                    startedAt,
                    candidateCreateCompletedAt,
                    candidateReadCompletedAt,
                    candidateStoppedAt,
                    priorStartedAt,
                    priorReadCompletedAt,
                    cleanupRequestedAt,
                    cleanupVerifiedAt,
                    candidateRestoredAt,
                    completedAt));
        }
        finally
        {
            priorClient?.Dispose();
            priorClient = null;
            if (candidateStoppedForRollback && !candidateRestored)
            {
                if (fixture.ProxyStarted)
                {
                    await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
                }
                await fixture.StartRuntimeAsync(SealedRuntimeRole.Candidate)
                    .ConfigureAwait(false);
            }

            if (objectCreated || bucketCreated)
            {
                if (objectCreated)
                {
                    try
                    {
                        await candidateClient.DeleteObjectAsync(
                            new DeleteObjectRequest { BucketName = bucket, Key = key },
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
                if (bucketCreated)
                {
                    try
                    {
                        await candidateClient.DeleteBucketAsync(
                            new DeleteBucketRequest { BucketName = bucket },
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    public static async Task<RealAzureRollbackResult> VerifySecretsManagerAsync(
        SecretsManagerRealAzureProxyFixture fixture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (!fixture.SealedRollbackConfigured)
        {
            throw new InvalidOperationException(
                "Real rollback requires verified candidate and prior sealed runtimes.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var secretName = "a2a-rollback-" + Guid.NewGuid().ToString("N");
        var canary = "secrets-rollback-" + Guid.NewGuid().ToString("N");
        var canaryDigest = Digest(canary);
        var secretCreated = false;
        var candidateRestored = false;
        var candidateStoppedForRollback = false;
        AmazonSecretsManagerClient? priorClient = null;

        using var candidateClient = fixture.CreateSecretsManagerClient(maxErrorRetry: 0);
        try
        {
            await candidateClient.CreateSecretAsync(
                new CreateSecretRequest
                {
                    Name = secretName,
                    SecretString = canary,
                    Description = "aws2azure sealed runtime rollback canary",
                },
                cancellationToken).ConfigureAwait(false);
            secretCreated = true;
            var candidateCreateCompletedAt = DateTimeOffset.UtcNow;
            await AssertSecretValueAsync(
                candidateClient,
                secretName,
                canary,
                cancellationToken).ConfigureAwait(false);
            var candidateReadCompletedAt = DateTimeOffset.UtcNow;

            await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
            candidateStoppedForRollback = true;
            var candidateStoppedAt = DateTimeOffset.UtcNow;
            await fixture.StartRuntimeAsync(SealedRuntimeRole.Prior).ConfigureAwait(false);
            var priorStartedAt = DateTimeOffset.UtcNow;
            priorClient = fixture.CreateSecretsManagerClient(maxErrorRetry: 0);

            await AssertSecretValueAsync(
                priorClient,
                secretName,
                canary,
                cancellationToken).ConfigureAwait(false);
            var priorReadCompletedAt = DateTimeOffset.UtcNow;
            var cleanupRequestedAt = TimestampAfter(priorReadCompletedAt);
            await priorClient.DeleteSecretAsync(
                new DeleteSecretRequest
                {
                    SecretId = secretName,
                    ForceDeleteWithoutRecovery = true,
                },
                cancellationToken).ConfigureAwait(false);
            await AssertSecretAbsentAsync(
                priorClient,
                secretName,
                cancellationToken).ConfigureAwait(false);
            secretCreated = false;
            var cleanupVerifiedAt = DateTimeOffset.UtcNow;

            await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
            priorClient.Dispose();
            priorClient = null;
            await fixture.StartRuntimeAsync(SealedRuntimeRole.Candidate).ConfigureAwait(false);
            candidateRestored = true;
            candidateStoppedForRollback = false;
            var candidateRestoredAt = DateTimeOffset.UtcNow;
            var completedAt = TimestampAfter(candidateRestoredAt);

            return new RealAzureRollbackResult(
                stopwatch.Elapsed.TotalSeconds,
                completedAt,
                Proof(
                    "secretsmanager",
                    "GetSecretValue",
                    fixture,
                    canaryDigest,
                    "force_delete_without_recovery_verify_resource_not_found_key_vault_soft_delete",
                    startedAt,
                    candidateCreateCompletedAt,
                    candidateReadCompletedAt,
                    candidateStoppedAt,
                    priorStartedAt,
                    priorReadCompletedAt,
                    cleanupRequestedAt,
                    cleanupVerifiedAt,
                    candidateRestoredAt,
                    completedAt));
        }
        finally
        {
            priorClient?.Dispose();
            priorClient = null;
            if (candidateStoppedForRollback && !candidateRestored)
            {
                if (fixture.HasDefaultInstance)
                {
                    await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
                }
                await fixture.StartRuntimeAsync(SealedRuntimeRole.Candidate)
                    .ConfigureAwait(false);
            }

            if (secretCreated)
            {
                try
                {
                    await candidateClient.DeleteSecretAsync(
                        new DeleteSecretRequest
                        {
                            SecretId = secretName,
                            ForceDeleteWithoutRecovery = true,
                        },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private static RealAzureRollbackProof Proof(
        string service,
        string operation,
        RealAzureProxyFixture fixture,
        string canaryDigest,
        string cleanupSemantics,
        DateTimeOffset startedAt,
        DateTimeOffset candidateCreateCompletedAt,
        DateTimeOffset candidateReadCompletedAt,
        DateTimeOffset candidateStoppedAt,
        DateTimeOffset priorStartedAt,
        DateTimeOffset priorReadCompletedAt,
        DateTimeOffset cleanupRequestedAt,
        DateTimeOffset cleanupVerifiedAt,
        DateTimeOffset candidateRestoredAt,
        DateTimeOffset completedAt) => new()
    {
        ScenarioId = "rollback",
        Service = service,
        Operation = operation,
        EvidenceRunId = RequiredEnvironment("GITHUB_RUN_ID"),
        EvidenceRunAttempt = ReadPositiveInt("GITHUB_RUN_ATTEMPT"),
        Candidate = fixture.CandidateRuntimeIdentity,
        Prior = fixture.PriorRuntimeIdentity,
        CandidateConfigDigest = fixture.ProxyConfigDigest,
        PriorConfigDigest = fixture.ProxyConfigDigest,
        CandidateBackendIdentityDigest = fixture.BackendIdentityDigest,
        PriorBackendIdentityDigest = fixture.BackendIdentityDigest,
        CandidateAwsBindingDigest = fixture.AwsBindingDigest,
        PriorAwsBindingDigest = fixture.AwsBindingDigest,
        CanaryDigest = canaryDigest,
        CleanupSemantics = cleanupSemantics,
        StartedAtUtc = startedAt,
        CandidateCreateCompletedAtUtc = candidateCreateCompletedAt,
        CandidateReadCompletedAtUtc = candidateReadCompletedAt,
        CandidateStoppedAtUtc = candidateStoppedAt,
        PriorStartedAtUtc = priorStartedAt,
        PriorReadCompletedAtUtc = priorReadCompletedAt,
        CleanupRequestedAtUtc = cleanupRequestedAt,
        CleanupVerifiedAtUtc = cleanupVerifiedAt,
        CandidateRestoredAtUtc = candidateRestoredAt,
        CompletedAtUtc = completedAt,
    };

    private static RealAzureRollbackProof Proof(
        string service,
        string operation,
        DynamoDbRealAzureProxyFixture fixture,
        string canaryDigest,
        string cleanupSemantics,
        DateTimeOffset startedAt,
        DateTimeOffset candidateCreateCompletedAt,
        DateTimeOffset candidateReadCompletedAt,
        DateTimeOffset candidateStoppedAt,
        DateTimeOffset priorStartedAt,
        DateTimeOffset priorReadCompletedAt,
        DateTimeOffset cleanupRequestedAt,
        DateTimeOffset cleanupVerifiedAt,
        DateTimeOffset candidateRestoredAt,
        DateTimeOffset completedAt) => new()
    {
        ScenarioId = "rollback",
        Service = service,
        Operation = operation,
        EvidenceRunId = RequiredEnvironment("GITHUB_RUN_ID"),
        EvidenceRunAttempt = ReadPositiveInt("GITHUB_RUN_ATTEMPT"),
        Candidate = fixture.CandidateRuntimeIdentity,
        Prior = fixture.PriorRuntimeIdentity,
        CandidateConfigDigest = fixture.ProxyConfigDigest,
        PriorConfigDigest = fixture.ProxyConfigDigest,
        CandidateBackendIdentityDigest = fixture.BackendIdentityDigest,
        PriorBackendIdentityDigest = fixture.BackendIdentityDigest,
        CandidateAwsBindingDigest = fixture.AwsBindingDigest,
        PriorAwsBindingDigest = fixture.AwsBindingDigest,
        CanaryDigest = canaryDigest,
        CleanupSemantics = cleanupSemantics,
        StartedAtUtc = startedAt,
        CandidateCreateCompletedAtUtc = candidateCreateCompletedAt,
        CandidateReadCompletedAtUtc = candidateReadCompletedAt,
        CandidateStoppedAtUtc = candidateStoppedAt,
        PriorStartedAtUtc = priorStartedAt,
        PriorReadCompletedAtUtc = priorReadCompletedAt,
        CleanupRequestedAtUtc = cleanupRequestedAt,
        CleanupVerifiedAtUtc = cleanupVerifiedAt,
        CandidateRestoredAtUtc = candidateRestoredAt,
        CompletedAtUtc = completedAt,
    };

    private static async Task AssertDynamoDbValueAsync(
        IAmazonDynamoDB client,
        string table,
        string key,
        string expected,
        CancellationToken cancellationToken)
    {
        var response = await client.GetItemAsync(
            new GetItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = key } },
                ConsistentRead = true,
            },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsItemSet
            || !response.Item.TryGetValue("payload", out var payload)
            || !string.Equals(payload.S, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Sealed runtime rollback returned the wrong DynamoDB canary.");
        }
    }

    private static async Task AssertDynamoDbTableAbsentAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + AbsenceTimeout;
        while (true)
        {
            try
            {
                await client.DescribeTableAsync(table, cancellationToken).ConfigureAwait(false);
            }
            catch (DynamoDbResourceNotFoundException)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidDataException(
                    "Prior sealed runtime cleanup did not make the DynamoDB table absent.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WaitForTableActiveAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var description = await client.DescribeTableAsync(table, cancellationToken)
                    .ConfigureAwait(false);
                if (description.Table.TableStatus == TableStatus.ACTIVE)
                {
                    return;
                }
            }
            catch (DynamoDbResourceNotFoundException)
            {
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private static RealAzureRollbackProof Proof(
        string service,
        string operation,
        SecretsManagerRealAzureProxyFixture fixture,
        string canaryDigest,
        string cleanupSemantics,
        DateTimeOffset startedAt,
        DateTimeOffset candidateCreateCompletedAt,
        DateTimeOffset candidateReadCompletedAt,
        DateTimeOffset candidateStoppedAt,
        DateTimeOffset priorStartedAt,
        DateTimeOffset priorReadCompletedAt,
        DateTimeOffset cleanupRequestedAt,
        DateTimeOffset cleanupVerifiedAt,
        DateTimeOffset candidateRestoredAt,
        DateTimeOffset completedAt) => new()
    {
        ScenarioId = "rollback",
        Service = service,
        Operation = operation,
        EvidenceRunId = RequiredEnvironment("GITHUB_RUN_ID"),
        EvidenceRunAttempt = ReadPositiveInt("GITHUB_RUN_ATTEMPT"),
        Candidate = fixture.CandidateRuntimeIdentity,
        Prior = fixture.PriorRuntimeIdentity,
        CandidateConfigDigest = fixture.ProxyConfigDigest,
        PriorConfigDigest = fixture.ProxyConfigDigest,
        CandidateBackendIdentityDigest = fixture.BackendIdentityDigest,
        PriorBackendIdentityDigest = fixture.BackendIdentityDigest,
        CandidateAwsBindingDigest = fixture.AwsBindingDigest,
        PriorAwsBindingDigest = fixture.AwsBindingDigest,
        CanaryDigest = canaryDigest,
        CleanupSemantics = cleanupSemantics,
        StartedAtUtc = startedAt,
        CandidateCreateCompletedAtUtc = candidateCreateCompletedAt,
        CandidateReadCompletedAtUtc = candidateReadCompletedAt,
        CandidateStoppedAtUtc = candidateStoppedAt,
        PriorStartedAtUtc = priorStartedAt,
        PriorReadCompletedAtUtc = priorReadCompletedAt,
        CleanupRequestedAtUtc = cleanupRequestedAt,
        CleanupVerifiedAtUtc = cleanupVerifiedAt,
        CandidateRestoredAtUtc = candidateRestoredAt,
        CompletedAtUtc = completedAt,
    };

    private static async Task AssertS3ValueAsync(
        IAmazonS3 client,
        string bucket,
        string key,
        string expected,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetObjectAsync(
            new GetObjectRequest { BucketName = bucket, Key = key },
            cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(response.ResponseStream);
        var actual = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (!actual.Equals(expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Sealed runtime rollback returned the wrong S3 canary.");
        }
    }

    private static async Task AssertS3AbsentAsync(
        IAmazonS3 client,
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + AbsenceTimeout;
        var objectAbsent = false;
        while (true)
        {
            if (!objectAbsent)
            {
                try
                {
                    await client.GetObjectMetadataAsync(
                        new GetObjectMetadataRequest { BucketName = bucket, Key = key },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AmazonS3Exception exception)
                    when (exception.StatusCode == HttpStatusCode.NotFound
                          || exception.ErrorCode is "NoSuchBucket" or "NoSuchKey")
                {
                    objectAbsent = true;
                }
            }

            try
            {
                await client.ListObjectsV2Async(
                    new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1 },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception exception)
                when (exception.StatusCode == HttpStatusCode.NotFound
                      || exception.ErrorCode == "NoSuchBucket")
            {
                if (objectAbsent)
                {
                    return;
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidDataException(
                    "Prior sealed runtime cleanup did not make the S3 canary absent.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AssertSecretValueAsync(
        IAmazonSecretsManager client,
        string name,
        string expected,
        CancellationToken cancellationToken)
    {
        var response = await client.GetSecretValueAsync(
            new GetSecretValueRequest { SecretId = name },
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(response.SecretString, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Sealed runtime rollback returned the wrong Secrets Manager canary.");
        }
    }

    private static async Task AssertSecretAbsentAsync(
        IAmazonSecretsManager client,
        string name,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + AbsenceTimeout;
        while (true)
        {
            try
            {
                await client.GetSecretValueAsync(
                    new GetSecretValueRequest { SecretId = name },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Amazon.SecretsManager.Model.ResourceNotFoundException)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidDataException(
                    "Prior sealed runtime cleanup did not make the secret absent.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Digest(string value) =>
        "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string RequiredEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"{name} is required for rollback evidence.")
            : value;
    }

    private static int ReadPositiveInt(string name)
    {
        var value = RequiredEnvironment(name);
        return int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new InvalidDataException($"{name} must be a positive integer.");
    }

    private static DateTimeOffset TimestampAfter(DateTimeOffset previous)
    {
        var current = DateTimeOffset.UtcNow;
        return current > previous ? current : previous.AddTicks(1);
    }
}
