using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using ResourceNotFoundException = Amazon.DynamoDBv2.Model.ResourceNotFoundException;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal sealed class DynamoDbRcCanary(Func<IAmazonDynamoDB> clientFactory)
{
    private readonly string _table = "a2a-rc-canary-" + Guid.NewGuid().ToString("N");
    private readonly string _payload = "rc-state-" + Guid.NewGuid().ToString("N");
    private bool _created;
    private static Dictionary<string, AttributeValue> Key => new() { ["pk"] = new() { S = "state" } };

    public async Task PrepareAsync(CancellationToken token)
    {
        using var client = clientFactory();
        await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = _table, BillingMode = BillingMode.PAY_PER_REQUEST,
            AttributeDefinitions = [new("pk", ScalarAttributeType.S)],
            KeySchema = [new("pk", KeyType.HASH)],
        }, token).ConfigureAwait(false);
        _created = true;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var description = await client.DescribeTableAsync(
                new DescribeTableRequest { TableName = _table }, token).ConfigureAwait(false);
            if (description.Table.TableStatus == TableStatus.ACTIVE) break;
            if (DateTimeOffset.UtcNow >= deadline)
                throw new InvalidDataException("The RC canary table did not become ACTIVE.");
            await Task.Delay(500, token).ConfigureAwait(false);
        }
        var item = Key;
        item["payload"] = new() { S = _payload };
        item["version"] = new() { N = "1" };
        await client.PutItemAsync(new PutItemRequest { TableName = _table, Item = item }, token)
            .ConfigureAwait(false);
        await VerifyAsync(client, "1", token).ConfigureAwait(false);
    }

    public async Task VerifyRestoredAsync(CancellationToken token)
    {
        using var client = clientFactory();
        await VerifyAsync(client, "1", token).ConfigureAwait(false);
        await client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _table, Key = Key, UpdateExpression = "SET version = version + :one",
            ExpressionAttributeValues = new() { [":one"] = new() { N = "1" } },
        }, token).ConfigureAwait(false);
        await VerifyAsync(client, "2", token).ConfigureAwait(false);
        await client.DeleteItemAsync(new DeleteItemRequest { TableName = _table, Key = Key }, token)
            .ConfigureAwait(false);
        var absent = await client.GetItemAsync(new GetItemRequest
        {
            TableName = _table, Key = Key, ConsistentRead = true,
        }, token).ConfigureAwait(false);
        if (absent.Item is { Count: > 0 })
            throw new InvalidDataException("The restored runtime did not delete the RC canary item.");
        await client.DeleteTableAsync(new DeleteTableRequest { TableName = _table }, token)
            .ConfigureAwait(false);
        try
        {
            await client.DescribeTableAsync(new DescribeTableRequest { TableName = _table }, token)
                .ConfigureAwait(false);
            throw new InvalidDataException("The restored runtime did not delete the RC canary table.");
        }
        catch (ResourceNotFoundException)
        {
            _created = false;
        }
    }

    private async Task VerifyAsync(IAmazonDynamoDB client, string version, CancellationToken token)
    {
        var response = await client.GetItemAsync(new GetItemRequest
        {
            TableName = _table, Key = Key, ConsistentRead = true,
        }, token).ConfigureAwait(false);
        if (!response.IsItemSet || !response.Item.TryGetValue("payload", out var payload)
            || payload.S != _payload || !response.Item.TryGetValue("version", out var actual)
            || actual.N != version)
            throw new InvalidDataException("The RC canary item state was not preserved.");
    }

    public async Task CleanupAsync(CancellationToken token)
    {
        if (!_created) return;
        using var client = clientFactory();
        try
        {
            await client.DeleteTableAsync(new DeleteTableRequest { TableName = _table }, token)
                .ConfigureAwait(false);
        }
        catch (ResourceNotFoundException) { }
        _created = false;
    }
}

internal sealed class SqsRcCanary(Func<IAmazonSQS> clientFactory)
{
    private readonly string _name = "a2a-rc-canary-" + Guid.NewGuid().ToString("N");
    private readonly string _body = "rc-state-" + Guid.NewGuid().ToString("N");
    private string? _url;

    public async Task PrepareAsync(CancellationToken token)
    {
        using var client = clientFactory();
        _url = (await client.CreateQueueAsync(
            new CreateQueueRequest { QueueName = _name }, token).ConfigureAwait(false)).QueueUrl;
        if (string.IsNullOrWhiteSpace(_url))
            throw new InvalidDataException("CreateQueue returned no RC canary queue URL.");
        await SendAsync(client, _body, token).ConfigureAwait(false);
        var receipt = await ReceiveAsync(client, _body, token).ConfigureAwait(false);
        // No candidate-owned AMQP lock is carried across the runtime switch.
        await client.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = _url, ReceiptHandle = receipt, VisibilityTimeout = 0,
        }, token).ConfigureAwait(false);
    }

    public async Task VerifyRestoredAsync(CancellationToken token)
    {
        using var client = clientFactory();
        var selected = await client.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = _name }, token)
            .ConfigureAwait(false);
        if (selected.QueueUrl != _url)
            throw new InvalidDataException("The restored runtime selected a different canary queue.");
        await SettleAsync(client, await ReceiveAsync(client, _body, token).ConfigureAwait(false), token)
            .ConfigureAwait(false);
        await SendAsync(client, _body + "-restored", token).ConfigureAwait(false);
        await SettleAsync(client, await ReceiveAsync(client, _body + "-restored", token).ConfigureAwait(false), token)
            .ConfigureAwait(false);
        var empty = await client.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = _url, MaxNumberOfMessages = 1, WaitTimeSeconds = 1,
        }, token).ConfigureAwait(false);
        if (empty.Messages is { Count: > 0 })
            throw new InvalidDataException("The restored runtime did not settle its RC canary messages.");
        await client.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = _url }, token).ConfigureAwait(false);
        try
        {
            await client.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = _name }, token).ConfigureAwait(false);
            throw new InvalidDataException("The restored runtime did not delete its RC canary queue.");
        }
        catch (QueueDoesNotExistException)
        {
            _url = null;
        }
    }

    private Task SendAsync(IAmazonSQS client, string body, CancellationToken token) =>
        client.SendMessageAsync(new SendMessageRequest { QueueUrl = _url, MessageBody = body }, token);

    private async Task<string> ReceiveAsync(IAmazonSQS client, string body, CancellationToken token)
    {
        var response = await client.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = _url, MaxNumberOfMessages = 1, WaitTimeSeconds = 5,
        }, token).ConfigureAwait(false);
        if (response.Messages is not { Count: 1 } || response.Messages[0].Body != body
            || string.IsNullOrWhiteSpace(response.Messages[0].ReceiptHandle))
            throw new InvalidDataException("The RC canary message or receipt was not preserved.");
        return response.Messages[0].ReceiptHandle;
    }

    private Task SettleAsync(IAmazonSQS client, string receipt, CancellationToken token) =>
        client.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = _url, ReceiptHandle = receipt }, token);

    public async Task CleanupAsync(CancellationToken token)
    {
        if (_url is null) return;
        using var client = clientFactory();
        try
        {
            await client.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = _url }, token).ConfigureAwait(false);
        }
        catch (QueueDoesNotExistException) { }
        _url = null;
    }
}
