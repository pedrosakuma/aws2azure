using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Kinesis.Model;
using DynamoDbResourceNotFoundException = Amazon.DynamoDBv2.Model.ResourceNotFoundException;
using Amazon.S3.Model;
using Amazon.SecretsManager.Model;
using Amazon.SQS.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Aws2Azure.IntegrationTests.Kinesis;
using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal static class RealAzureRestartQualification
{
    public static async Task VerifyDynamoDbAsync(DynamoDbRealAzureProxyFixture fixture)
    {
        var table = "aws2azure-restart-" + Guid.NewGuid().ToString("N")[..10];
        using var client = fixture.CreateDynamoDbClient();
        var tableCreated = false;
        try
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }).ConfigureAwait(false);
            tableCreated = true;
            await WaitForTableActiveAsync(client, table).ConfigureAwait(false);

            await client.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new AttributeValue { S = "state" },
                    ["payload"] = new AttributeValue { S = "survives-restart" },
                },
            }).ConfigureAwait(false);

            await fixture.RestartAsync().ConfigureAwait(false);

            var response = await client.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = "state" } },
                ConsistentRead = true,
            }).ConfigureAwait(false);
            Assert.True(response.IsItemSet);
            Assert.Equal("survives-restart", response.Item["payload"].S);
        }
        finally
        {
            if (tableCreated)
            {
                try
                {
                    await client.DeleteTableAsync(new DeleteTableRequest { TableName = table })
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private static async Task WaitForTableActiveAsync(
        Amazon.DynamoDBv2.IAmazonDynamoDB client,
        string table)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var desc = await client.DescribeTableAsync(table).ConfigureAwait(false);
                if (desc.Table.TableStatus == TableStatus.ACTIVE)
                {
                    return;
                }
            }
            catch (DynamoDbResourceNotFoundException)
            {
            }

            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    public static async Task VerifyS3Async(RealAzureProxyFixture fixture)
    {
        var bucket = "aws2azure-restart-" + Guid.NewGuid().ToString("N")[..10];
        const string key = "state";
        using var client = fixture.CreateS3Client();
        try
        {
            await client.PutBucketAsync(bucket).ConfigureAwait(false);
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = "survives-restart",
            }).ConfigureAwait(false);

            await fixture.RestartAsync().ConfigureAwait(false);

            using var response = await client.GetObjectAsync(bucket, key).ConfigureAwait(false);
            using var reader = new StreamReader(response.ResponseStream);
            Assert.Equal("survives-restart",
                await reader.ReadToEndAsync().ConfigureAwait(false));
        }
        finally
        {
            try { await client.DeleteObjectAsync(bucket, key).ConfigureAwait(false); } catch { }
            try { await client.DeleteBucketAsync(bucket).ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// Writes a record before restart, then confirms it is still readable
    /// after the proxy is terminated and restarted with the same immutable
    /// configuration, using a *durable* AT_TIMESTAMP iterator obtained after
    /// the restart. A LATEST-type iterator deliberately is not carried across
    /// the restart here: per the documented "shared broker cursor per
    /// consumer group" design gap, an unread LATEST token is resolved
    /// relative to the pooled AMQP link's live position, which is proxy-
    /// process-local and is not expected to durably resume across a process
    /// restart. AT_TIMESTAMP instead encodes a concrete Event-Hubs
    /// enqueued-time position, so it round-trips correctly regardless of
    /// which process resolves it, proving the write survived the restart
    /// without asserting a stronger cross-process cursor guarantee this
    /// module does not make.
    /// </summary>
    public static async Task VerifyKinesisAsync(RealAzureProxyFixture fixture)
    {
        using var client = fixture.CreateKinesisClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var target = await KinesisTestHelpers.ResolvePartitionTargetAsync(
            client, fixture.EventHubStream, timeout.Token).ConfigureAwait(false);

        // Event Hubs applies AT_TIMESTAMP exclusively at millisecond precision.
        // Leave a margin so the immediately following write cannot share and be
        // excluded by the encoded boundary.
        var boundary = DateTimeOffset.UtcNow.AddSeconds(-5);
        var payload = "restart-" + Guid.NewGuid().ToString("N");
        using var data = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        await client.PutRecordAsync(new PutRecordRequest
        {
            StreamName = fixture.EventHubStream,
            PartitionKey = target.PartitionKey,
            Data = data,
        }, timeout.Token).ConfigureAwait(false);

        await fixture.RestartAsync().ConfigureAwait(false);

        var iterator = await client.GetShardIteratorAsync(new GetShardIteratorRequest
        {
            StreamName = fixture.EventHubStream,
            ShardId = target.ShardId,
            ShardIteratorType = "AT_TIMESTAMP",
            Timestamp = KinesisTestHelpers.ToSdkTimestamp(boundary),
        }, timeout.Token).ConfigureAwait(false);

        var records = await KinesisTestHelpers.ReadUntilAsync(
            client,
            iterator.ShardIterator,
            record => KinesisTestHelpers.Utf8(record) == payload,
            TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        Assert.Contains(records, record => KinesisTestHelpers.Utf8(record) == payload);
    }

    public static async Task VerifySecretsManagerAsync(
        SecretsManagerRealAzureProxyFixture fixture)
    {
        var secret = "aws2azure-restart-" + Guid.NewGuid().ToString("N");
        using var client = fixture.CreateSecretsManagerClient();
        try
        {
            await client.CreateSecretAsync(new CreateSecretRequest
            {
                Name = secret,
                SecretString = "survives-restart",
            }).ConfigureAwait(false);

            await fixture.RestartAsync().ConfigureAwait(false);

            var response = await client.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = secret,
            }).ConfigureAwait(false);
            Assert.Equal("survives-restart", response.SecretString);
        }
        finally
        {
            try
            {
                await client.DeleteSecretAsync(new DeleteSecretRequest
                {
                    SecretId = secret,
                    ForceDeleteWithoutRecovery = true,
                }).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Proves a queued message survives a proxy process replacement: the
    /// queue itself stays addressable (matching the pre-existing addressability
    /// check) and, more importantly, a message enqueued before the restart is
    /// still deliverable afterwards — the message lives in Azure Service Bus,
    /// not in proxy process memory, so a bounce of the proxy must not lose or
    /// corrupt in-flight queue contents.
    /// </summary>
    public static async Task VerifySqsAsync(RealAzureProxyFixture fixture)
    {
        var queue = "aws2azure-restart-" + Guid.NewGuid().ToString("N")[..10];
        const string body = "survives-restart";
        using var client = fixture.CreateSqsClient();
        string? queueUrl = null;
        try
        {
            queueUrl = (await client.CreateQueueAsync(queue).ConfigureAwait(false)).QueueUrl;
            await client.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = body,
            }).ConfigureAwait(false);

            await fixture.RestartAsync().ConfigureAwait(false);

            var urlResponse = await client.GetQueueUrlAsync(queue).ConfigureAwait(false);
            Assert.Equal(queueUrl, urlResponse.QueueUrl);

            ReceiveMessageResponse received = new();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline && received.Messages is not { Count: > 0 })
            {
                received = await client.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 5,
                }).ConfigureAwait(false);
            }
            Assert.True(received.Messages is { Count: > 0 },
                "No message received from the restarted proxy within timeout.");
            Assert.Equal(body, received.Messages[0].Body);

            await client.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = queueUrl,
                ReceiptHandle = received.Messages[0].ReceiptHandle,
            }).ConfigureAwait(false);
        }
        finally
        {
            if (queueUrl is not null)
            {
                try { await client.DeleteQueueAsync(queueUrl).ConfigureAwait(false); } catch { }
            }
        }
    }
}
