using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SQS.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class RealAzureRestartQualificationTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task S3_state_remains_readable_after_proxy_restart()
    {
        Skip.IfNot(fixture.BlobConfigured, "Real Azure Blob Storage is not configured.");
        await RealAzureRestartQualification.VerifyS3Async(fixture).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task DynamoDb_state_remains_readable_after_proxy_restart()
    {
        Skip.IfNot(fixture.CosmosConfigured, "Real Azure Cosmos DB is not configured.");
        var table = "rst" + Guid.NewGuid().ToString("N")[..10];
        using var client = fixture.CreateDynamoDbClient();
        try
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }).ConfigureAwait(false);
            await client.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new() { S = "state" },
                    ["value"] = new() { S = "survives-restart" },
                },
            }).ConfigureAwait(false);

            await fixture.RestartAsync().ConfigureAwait(false);

            var response = await client.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new() { S = "state" },
                },
                ConsistentRead = true,
            }).ConfigureAwait(false);
            Assert.Equal("survives-restart", response.Item["value"].S);
        }
        finally
        {
            try { await client.DeleteTableAsync(table).ConfigureAwait(false); } catch { }
        }
    }

    [SkippableFact]
    public async Task Sqs_queue_remains_addressable_after_proxy_restart()
    {
        Skip.IfNot(fixture.ServiceBusConfigured, "Real Azure Service Bus is not configured.");
        var queue = "aws2azure-restart-" + Guid.NewGuid().ToString("N")[..10];
        using var client = fixture.CreateSqsClient();
        string? queueUrl = null;
        try
        {
            queueUrl = (await client.CreateQueueAsync(queue).ConfigureAwait(false)).QueueUrl;

            await fixture.RestartAsync().ConfigureAwait(false);

            var response = await client.GetQueueUrlAsync(queue).ConfigureAwait(false);
            Assert.Equal(queueUrl, response.QueueUrl);
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
