using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DynamoDbResourceNotFoundException = Amazon.DynamoDBv2.Model.ResourceNotFoundException;
using Amazon.Kinesis.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Aws2Azure.IntegrationTests.Kinesis;
using Aws2Azure.TestSupport.OperationalQualification;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal sealed record RealAzureRollbackResult(
    double DurationSeconds,
    DateTimeOffset CapturedAtUtc,
    RealAzureRollbackProof Proof);

/// <summary>
/// SQS rollback additionally documents the receipt-handle/lock boundary
/// across a proxy process replacement (issue #626): the graded AMQP
/// <see cref="Proof"/> is required by the workload manifest, while the
/// REST-transport finding is supplementary evidence kept in its own
/// scenario row rather than a second <see cref="RealAzureRollbackProof"/>
/// (the qualifier requires exactly one rollback proof per run).
/// </summary>
internal sealed record RealAzureSqsRollbackResult(
    double DurationSeconds,
    DateTimeOffset CapturedAtUtc,
    RealAzureRollbackProof Proof,
    bool RestReceiptHandleSurvivedRestart,
    double RestDurationSeconds,
    DateTimeOffset RestCapturedAtUtc);

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
                    fixture.BackendIdentityDigest,
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

    public static async Task<RealAzureRollbackResult> VerifyKinesisAsync(
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
        var canary = "kinesis-rollback-" + Guid.NewGuid().ToString("N");
        var canaryDigest = Digest(canary);
        var boundary = DateTimeOffset.UtcNow.AddSeconds(-5);
        var candidateRestored = false;
        var candidateStoppedForRollback = false;
        Amazon.Kinesis.AmazonKinesisClient? priorClient = null;

        using var candidateClient = fixture.CreateKinesisClient();
        try
        {
            var target = await KinesisTestHelpers.ResolvePartitionTargetAsync(
                candidateClient,
                fixture.EventHubStream,
                cancellationToken).ConfigureAwait(false);
            using (var data = new MemoryStream(Encoding.UTF8.GetBytes(canary)))
            {
                await candidateClient.PutRecordAsync(
                    new PutRecordRequest
                    {
                        StreamName = fixture.EventHubStream,
                        PartitionKey = target.PartitionKey,
                        Data = data,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            var candidateCreateCompletedAt = DateTimeOffset.UtcNow;
            await AssertKinesisValueAsync(
                candidateClient,
                fixture.EventHubStream,
                target.ShardId,
                boundary,
                canary,
                cancellationToken).ConfigureAwait(false);
            var candidateReadCompletedAt = DateTimeOffset.UtcNow;

            await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
            candidateStoppedForRollback = true;
            var candidateStoppedAt = DateTimeOffset.UtcNow;
            await fixture.StartRuntimeAsync(SealedRuntimeRole.Prior).ConfigureAwait(false);
            var priorStartedAt = DateTimeOffset.UtcNow;
            priorClient = fixture.CreateKinesisClient();
            await AssertKinesisValueAsync(
                priorClient,
                fixture.EventHubStream,
                target.ShardId,
                boundary,
                canary,
                cancellationToken).ConfigureAwait(false);
            var priorReadCompletedAt = DateTimeOffset.UtcNow;

            // Event Hubs records are retention-managed and cannot be deleted
            // through the Kinesis data-plane profile. Record that boundary
            // explicitly rather than claiming a cleanup operation exists.
            var cleanupRequestedAt = TimestampAfter(priorReadCompletedAt);
            var cleanupVerifiedAt = TimestampAfter(cleanupRequestedAt);

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
                    "kinesis",
                    "GetRecords",
                    fixture,
                    fixture.EventHubsBackendIdentityDigest,
                    canaryDigest,
                    "event_hubs_retention_no_immediate_record_delete",
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
            if (candidateStoppedForRollback && !candidateRestored)
            {
                if (fixture.ProxyStarted)
                {
                    await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
                }
                await fixture.StartRuntimeAsync(SealedRuntimeRole.Candidate)
                    .ConfigureAwait(false);
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
        var initialCanary = "secrets-rollback-initial-" + Guid.NewGuid().ToString("N");
        var canary = "secrets-rollback-" + Guid.NewGuid().ToString("N");
        var replayToken = Guid.NewGuid().ToString();
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
                    SecretString = initialCanary,
                    Description = "aws2azure sealed runtime rollback canary",
                },
                cancellationToken).ConfigureAwait(false);
            secretCreated = true;
            await candidateClient.PutSecretValueAsync(
                new PutSecretValueRequest
                {
                    SecretId = secretName,
                    SecretString = canary,
                    ClientRequestToken = replayToken,
                },
                cancellationToken).ConfigureAwait(false);
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
            var replay = await priorClient.PutSecretValueAsync(
                new PutSecretValueRequest
                {
                    SecretId = secretName,
                    SecretString = canary,
                    ClientRequestToken = replayToken,
                },
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(replay.VersionId, replayToken, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Secrets Manager rollback replay returned a different ClientRequestToken.");
            }

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

    /// <summary>
    /// Sends a canary on both the AMQP-default and REST-lane transports,
    /// receives (but does not settle) each on the candidate, switches to the
    /// prior sealed runtime, and proves: (1) queued state remains usable —
    /// both canaries are still deliverable — and (2) the receipt-handle/lock
    /// boundary at the transport level. The AMQP receipt handle is minted
    /// against an in-process receiver link; when the candidate process is
    /// replaced that link is gone, so Service Bus cannot resolve the old lock
    /// token against the prior runtime's fresh receiver and redemption must
    /// fail (<c>ReceiptHandleIsInvalid</c>) before a freshly received handle
    /// completes the message. The REST receipt handle instead carries a
    /// Service Bus message-id/lock-token pair that the broker validates
    /// statelessly, so it redeems successfully even though the process that
    /// received it is gone — the boundary is transport-scoped, not merely a
    /// property of "the proxy restarted".
    /// </summary>
    public static async Task<RealAzureSqsRollbackResult> VerifySqsAsync(
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
        var restStopwatch = Stopwatch.StartNew();
        var amqpQueue = "aws2azure-rollback-" + Guid.NewGuid().ToString("N")[..20];
        var restQueue = RealAzureProxyFixture.SqsRestLaneQueueName;
        var amqpCanary = "sqs-rollback-amqp-" + Guid.NewGuid().ToString("N");
        var restCanary = "sqs-rollback-rest-" + Guid.NewGuid().ToString("N");
        var amqpCanaryDigest = Digest(amqpCanary);
        var amqpQueueCreated = false;
        var candidateRestored = false;
        var candidateStoppedForRollback = false;
        string? amqpQueueUrl = null;
        AmazonSQSClient? priorClient = null;

        using var candidateClient = fixture.CreateSqsClient(maxErrorRetry: 0);
        try
        {
            var restQueueUrl = await EnsureQueueUrlAsync(candidateClient, restQueue, cancellationToken)
                .ConfigureAwait(false);
            await PurgeQueueAsync(candidateClient, restQueueUrl, cancellationToken)
                .ConfigureAwait(false);

            amqpQueueUrl = (await candidateClient.CreateQueueAsync(amqpQueue, cancellationToken)
                .ConfigureAwait(false)).QueueUrl;
            amqpQueueCreated = true;

            await candidateClient.SendMessageAsync(
                new SendMessageRequest { QueueUrl = amqpQueueUrl, MessageBody = amqpCanary },
                cancellationToken).ConfigureAwait(false);
            await candidateClient.SendMessageAsync(
                new SendMessageRequest { QueueUrl = restQueueUrl, MessageBody = restCanary },
                cancellationToken).ConfigureAwait(false);
            var candidateCreateCompletedAt = DateTimeOffset.UtcNow;

            var candidateAmqpReceipt = await ReceiveExpectedAsync(
                candidateClient, amqpQueueUrl, amqpCanary, cancellationToken).ConfigureAwait(false);
            var candidateRestReceipt = await ReceiveExpectedAsync(
                candidateClient, restQueueUrl, restCanary, cancellationToken).ConfigureAwait(false);
            var candidateReadCompletedAt = DateTimeOffset.UtcNow;

            await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
            candidateStoppedForRollback = true;
            var candidateStoppedAt = DateTimeOffset.UtcNow;
            await fixture.StartRuntimeAsync(SealedRuntimeRole.Prior).ConfigureAwait(false);
            var priorStartedAt = DateTimeOffset.UtcNow;
            priorClient = fixture.CreateSqsClient(maxErrorRetry: 0);

            // Receipt-handle/lock boundary: the AMQP handle minted before the
            // switch must NOT redeem on the prior runtime's fresh receiver.
            var oldAmqpHandleRejected = await TryDeleteAsync(
                priorClient, amqpQueueUrl, candidateAmqpReceipt, cancellationToken)
                .ConfigureAwait(false);
            if (oldAmqpHandleRejected)
            {
                throw new InvalidDataException(
                    "The AMQP receipt handle minted before the proxy restart unexpectedly " +
                    "redeemed against the prior runtime; the link-scoped lock boundary no " +
                    "longer holds.");
            }

            // Queued state remains usable: a fresh receive on the prior
            // runtime redelivers the same canary, which can then be settled.
            var priorAmqpReceipt = await ReceiveExpectedAsync(
                priorClient, amqpQueueUrl, amqpCanary, cancellationToken).ConfigureAwait(false);
            await priorClient.DeleteMessageAsync(
                new DeleteMessageRequest
                {
                    QueueUrl = amqpQueueUrl,
                    ReceiptHandle = priorAmqpReceipt,
                },
                cancellationToken).ConfigureAwait(false);
            var priorReadCompletedAt = DateTimeOffset.UtcNow;

            // REST-transport counterpart: the broker-scoped lock token DOES
            // redeem against the prior runtime, because Service Bus validates
            // it statelessly rather than through a process-local receiver.
            var restHandleRedeemed = await TryDeleteAsync(
                priorClient, restQueueUrl, candidateRestReceipt, cancellationToken)
                .ConfigureAwait(false);
            restStopwatch.Stop();
            var restCapturedAt = DateTimeOffset.UtcNow;

            var cleanupRequestedAt = TimestampAfter(priorReadCompletedAt);
            await AssertSqsQueueEmptyAsync(priorClient, amqpQueueUrl, cancellationToken)
                .ConfigureAwait(false);
            // Best-effort cleanup: two independent production-shaped GA load
            // dispatches (runs 29843180286 and 29847167581) observed
            // GetQueueUrl's existence probe still finding this queue for
            // well over both a one-minute and a three-minute budget after
            // DeleteQueue had already returned success, on a namespace
            // churning many other queues from the same run's
            // representative-load workers. Unlike the ListQueues
            // creation-lag caveat (bounded at a few seconds), this
            // management-plane propagation has no empirically observed
            // upper bound, so it cannot be a reliable, immutable-evidence
            // gate — see docs/gaps/sqs/DeleteQueue.yaml. Delete the queue
            // for hygiene without blocking the qualifying scenario proof
            // (which is already complete at this point: receipt-handle/lock
            // boundary, redelivery, and REST/AMQP separation are all proven
            // above) on an unbounded existence-probe wait.
            try
            {
                await priorClient.DeleteQueueAsync(amqpQueueUrl, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AmazonSQSException exception)
                when (exception.ErrorCode is "AWS.SimpleQueueService.NonExistentQueue"
                      or "NonExistentQueue")
            {
            }
            amqpQueueCreated = false;
            var cleanupVerifiedAt = DateTimeOffset.UtcNow;

            await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
            priorClient.Dispose();
            priorClient = null;
            await fixture.StartRuntimeAsync(SealedRuntimeRole.Candidate).ConfigureAwait(false);
            candidateRestored = true;
            candidateStoppedForRollback = false;
            var candidateRestoredAt = DateTimeOffset.UtcNow;
            var completedAt = TimestampAfter(candidateRestoredAt);

            return new RealAzureSqsRollbackResult(
                stopwatch.Elapsed.TotalSeconds,
                completedAt,
                Proof(
                    "sqs",
                    "DeleteMessage",
                    fixture,
                    fixture.ServiceBusBackendIdentityDigest,
                    amqpCanaryDigest,
                    SqsAmqpRollbackCleanupSemantics,
                    startedAt,
                    candidateCreateCompletedAt,
                    candidateReadCompletedAt,
                    candidateStoppedAt,
                    priorStartedAt,
                    priorReadCompletedAt,
                    cleanupRequestedAt,
                    cleanupVerifiedAt,
                    candidateRestoredAt,
                    completedAt),
                restHandleRedeemed,
                restStopwatch.Elapsed.TotalSeconds,
                restCapturedAt);
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

            if (amqpQueueCreated && amqpQueueUrl is not null)
            {
                try
                {
                    await candidateClient.DeleteQueueAsync(
                        amqpQueueUrl, CancellationToken.None).ConfigureAwait(false);
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
        string backendIdentityDigest,
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
        CandidateBackendIdentityDigest = backendIdentityDigest,
        PriorBackendIdentityDigest = backendIdentityDigest,
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

    private static async Task AssertKinesisValueAsync(
        Amazon.Kinesis.IAmazonKinesis client,
        string streamName,
        string shardId,
        DateTimeOffset boundary,
        string expected,
        CancellationToken cancellationToken)
    {
        var iterator = await client.GetShardIteratorAsync(
            new GetShardIteratorRequest
            {
                StreamName = streamName,
                ShardId = shardId,
                ShardIteratorType = "AT_TIMESTAMP",
                Timestamp = KinesisTestHelpers.ToSdkTimestamp(boundary),
            },
            cancellationToken).ConfigureAwait(false);
        var records = await KinesisTestHelpers.ReadUntilAsync(
            (Amazon.Kinesis.AmazonKinesisClient)client,
            iterator.ShardIterator,
            record => KinesisTestHelpers.Utf8(record) == expected,
            TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        if (!records.Any(record => KinesisTestHelpers.Utf8(record) == expected))
        {
            throw new InvalidDataException(
                "Sealed runtime rollback did not return the Kinesis canary.");
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

    private const string SqsAmqpRollbackCleanupSemantics =
        "amqp_link_scoped_lock_requires_redelivery_after_restart_delete_queue_verify_empty";

    /// <summary>
    /// SQS <c>ReceiveMessage</c> uses long polling (5s) with a bounded
    /// overall deadline, then asserts the received body matches the
    /// expected canary before returning the receipt handle. Mirrors the
    /// receive loop already used by <c>SqsRealAzureSmokeTests</c>.
    /// </summary>
    private static async Task<string> ReceiveExpectedAsync(
        IAmazonSQS client,
        string queueUrl,
        string expectedBody,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + AbsenceTimeout;
        while (true)
        {
            var response = await client.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 5,
                },
                cancellationToken).ConfigureAwait(false);
            if (response.Messages is { Count: > 0 } messages)
            {
                if (!string.Equals(messages[0].Body, expectedBody, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Sealed runtime rollback returned the wrong SQS canary.");
                }
                return messages[0].ReceiptHandle;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidDataException(
                    "No message received from Service Bus within the rollback deadline.");
            }
        }
    }

    /// <summary>
    /// Attempts <c>DeleteMessage</c> with a receipt handle that may no longer
    /// be redeemable (the AMQP boundary case). Returns whether it succeeded
    /// instead of throwing, so callers can assert either outcome explicitly.
    /// </summary>
    private static async Task<bool> TryDeleteAsync(
        IAmazonSQS client,
        string queueUrl,
        string receiptHandle,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.DeleteMessageAsync(
                new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = receiptHandle },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ReceiptHandleIsInvalidException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates the shared, fixed-name REST-transport lane queue used by both
    /// this rollback proof and the load runner's REST-representative
    /// scenario. It is intentionally never deleted within a run (only
    /// purged — see <see cref="PurgeQueueAsync"/>), so a later caller in the
    /// same run creating it again is an ordinary idempotent re-create, not a
    /// conflict; fall back to resolving the existing queue's URL rather than
    /// failing the whole load run if Service Bus ever reports a transient
    /// attribute mismatch on that idempotent re-create.
    /// </summary>
    private static async Task<string> EnsureQueueUrlAsync(
        IAmazonSQS client,
        string queueName,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.CreateQueueAsync(queueName, cancellationToken)
                .ConfigureAwait(false);
            return response.QueueUrl;
        }
        catch (QueueNameExistsException)
        {
            var existing = await client.GetQueueUrlAsync(
                new GetQueueUrlRequest { QueueName = queueName },
                cancellationToken).ConfigureAwait(false);
            return existing.QueueUrl;
        }
    }

    private static async Task PurgeQueueAsync(
        IAmazonSQS client,
        string queueUrl,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var response = await client.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 1,
                },
                cancellationToken).ConfigureAwait(false);
            if (response.Messages is not { Count: > 0 } messages)
            {
                return;
            }
            foreach (var message in messages)
            {
                try
                {
                    await client.DeleteMessageAsync(
                        new DeleteMessageRequest
                        {
                            QueueUrl = queueUrl,
                            ReceiptHandle = message.ReceiptHandle,
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private static async Task AssertSqsQueueEmptyAsync(
        IAmazonSQS client,
        string queueUrl,
        CancellationToken cancellationToken)
    {
        var response = await client.ReceiveMessageAsync(
            new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 1,
            },
            cancellationToken).ConfigureAwait(false);
        if (response.Messages is { Count: > 0 })
        {
            throw new InvalidDataException(
                "Prior sealed runtime cleanup left an unexpected message queued.");
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
