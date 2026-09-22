using System.Diagnostics;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Aws2Azure.IntegrationTests.DynamoDb;
using Aws2Azure.IntegrationTests.Sqs;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;
using DdbMissing = Amazon.DynamoDBv2.Model.ResourceNotFoundException;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

[Trait("Category", "RcObservationOffline")]
public sealed class RcCrudCohortTests
{
    [Theory]
    [InlineData("dynamodb-basic-crud", "candidate", "")]
    [InlineData("sqs-standard-messaging", "candidate", "")]
    [InlineData("dynamodb-basic-crud", "stable", "")]
    [InlineData("sqs-standard-messaging", "stable", "")]
    [InlineData("dynamodb-basic-crud", "candidate", "worker")]
    [InlineData("sqs-standard-messaging", "candidate", "worker")]
    [InlineData("dynamodb-basic-crud", "candidate", "prepare")]
    [InlineData("sqs-standard-messaging", "candidate", "prepare")]
    [InlineData("dynamodb-basic-crud", "candidate", "restore")]
    [InlineData("sqs-standard-messaging", "candidate", "verify")]
    [InlineData("dynamodb-basic-crud", "candidate", "binding")]
    [InlineData("sqs-standard-messaging", "candidate", "cancel")]
    [InlineData("sqs-standard-messaging", "candidate", "cleanup")]
    public async Task Cohort_requires_canary_preserves_failures_and_never_invents_restoration(
        string profile, string role, string fault)
    {
        var ddb = profile == "dynamodb-basic-crud";
        var operations = ddb ? DynamoDbRealAzureLoadQualificationTests.Operations : SqsRealAzureLoadQualificationTests.Operations;
        var representative = ddb ? "GetItem" : "ReceiveMessage";
        using var cancellation = new CancellationTokenSource();
        var ready = 0;
        var switched = 0;
        var verified = 0;
        var cleaned = 0;
        var binding = new RcCohortBinding("backend", "config", "aws");
        var result = await RcCrudCohort.RunAsync(
            profile, ddb ? "dynamodb" : "sqs", ddb ? "cosmos" : "servicebus",
            representative, operations, role, "test-region", TimeSpan.FromMinutes(60), 8,
            RcObservationCaptureWriter.OperationMixIdentity(profile, operations),
            new("candidate-id", "candidate-bytes", "prior-id", "prior-bytes", "http://owned",
                () => true, () => binding, token =>
                {
                    token.ThrowIfCancellationRequested();
                    switched++;
                    if (fault == "restore") throw new InvalidDataException("restore failed");
                    if (fault == "binding") binding = binding with { Config = "changed" };
                    return Task.CompletedTask;
                }),
            token =>
            {
                if (fault == "prepare") throw new InvalidDataException("canary failed");
                return Task.CompletedTask;
            },
            token =>
            {
                verified++;
                if (fault == "verify") throw new InvalidDataException("restored state is wrong");
                return Task.CompletedTask;
            },
            token =>
            {
                Assert.True(token.CanBeCanceled);
                cleaned++;
                if (fault == "cleanup") throw new InvalidDataException("cleanup failed");
                return Task.CompletedTask;
            },
            (worker, tracker, duration, watch, token) =>
            {
                foreach (var operation in operations) tracker.RecordSuccess(operation, 1);
                if (fault == "cancel") cancellation.Cancel();
                if (fault == "worker")
                {
                    var error = new InvalidDataException("retained worker failure");
                    tracker.RecordFailure(representative, 1, false, error);
                    return Task.FromException(error);
                }
                return Task.CompletedTask;
            },
            token => { ready++; return Task.FromResult(DateTimeOffset.UtcNow); },
            index => $"member-{index}", $"{role}-test", cancellation.Token);
        Assert.Equal(1, cleaned);
        Assert.Equal(fault == "prepare" ? 0 : 1, ready);
        Assert.Equal(role, result.Capture.Cohort.Role);
        Assert.Equal(role == "candidate" ? "candidate-id" : "prior-id",
            result.Capture.Cohort.RuntimeIdentityDigest);
        Assert.Equal(8, result.Capture.Cohort.MemberDigests.Distinct().Count());
        if (fault is "prepare" or "cancel") Assert.Equal(0, switched);
        if (fault is "restore" or "binding" or "prepare" or "cancel") Assert.Equal(0, verified);
        var restored = role == "candidate" && fault is "" or "worker" or "cleanup";
        Assert.Equal(restored, result.Capture.Restoration?.Verified == true);
        if (fault == "")
        {
            Assert.Null(result.Failure);
            result.ThrowIfFailed();
            Assert.Equal(8, result.Capture.Metrics[0].Samples);
            Assert.Equal(56, result.Capture.Metrics[1].Samples);
            Assert.Equal(0, result.Capture.Metrics[1].Value);
        }
        else
        {
            Assert.NotNull(result.Failure);
            Assert.ThrowsAny<Exception>(result.ThrowIfFailed);
        }
        if (fault == "worker")
        {
            Assert.Equal(8, result.Capture.Cohort.OperationDiagnostics.Sum(row => row.Failures));
            Assert.Equal(8d / 64, result.Capture.Metrics[1].Value);
            Assert.Equal("prior-bytes", result.Capture.Restoration!.RuntimeDigest);
        }
    }

    [Fact]
    public async Task DynamoDb_canary_requires_persisted_value_then_prior_update_delete_and_absence()
    {
        var state = new DdbState();
        var canary = new DynamoDbRcCanary(() => new DdbClient(state));
        await canary.PrepareAsync(default);
        Assert.Single(state.Tables);
        state.Role = "prior";
        await canary.VerifyRestoredAsync(default);
        Assert.Empty(state.Tables);
        Assert.Contains("prior:UpdateItem", state.Calls);
        Assert.Contains("prior:DeleteItem", state.Calls);
        Assert.Contains("prior:DeleteTable", state.Calls);
        await canary.CleanupAsync(default);
    }

    [Fact]
    public async Task DynamoDb_canary_rejects_corruption_and_retains_cleanup_ownership()
    {
        var state = new DdbState();
        var canary = new DynamoDbRcCanary(() => new DdbClient(state));
        await canary.PrepareAsync(default);
        state.Tables.Values.Single().Values.Single()["payload"].S = "corrupted";
        await Assert.ThrowsAsync<InvalidDataException>(() => canary.VerifyRestoredAsync(default));
        await canary.CleanupAsync(default);
        Assert.Empty(state.Tables);
    }

    [Fact]
    public async Task Sqs_canary_abandons_candidate_lock_and_prior_receives_and_settles_owned_messages()
    {
        var state = new SqsState();
        var canary = new SqsRcCanary(() => new SqsClient(state));
        await canary.PrepareAsync(default);
        Assert.Null(state.Queues.Values.Single().Lease);
        Assert.Single(state.Queues.Values.Single().Available);
        state.Role = "prior";
        await canary.VerifyRestoredAsync(default);
        Assert.Empty(state.Queues);
        Assert.Equal(["prior", "prior"], state.SettledBy);
        await canary.CleanupAsync(default);
    }

    [Fact]
    public async Task Sqs_canary_rejects_wrong_message_and_cancellation()
    {
        var state = new SqsState();
        var canary = new SqsRcCanary(() => new SqsClient(state));
        await canary.PrepareAsync(default);
        var queue = state.Queues.Values.Single();
        queue.Available.Clear();
        queue.Available.Enqueue("not-the-candidate-canary");
        await Assert.ThrowsAsync<InvalidDataException>(() => canary.VerifyRestoredAsync(default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canary.CleanupAsync(cancellation.Token));
        Assert.Single(state.Queues);
        await canary.CleanupAsync(default);
        Assert.Empty(state.Queues);
    }

    [Fact]
    public async Task DynamoDb_observation_reuses_exact_qualification_cycle_and_bounds_failed_worker_inventory()
    {
        var state = new DdbState { FailSecondPut = true };
        using var client = new DdbClient(state);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tracker = new RealAzureWorkloadLoadTracker("dynamodb", DynamoDbRealAzureLoadQualificationTests.Operations);
        await Assert.ThrowsAsync<InvalidDataException>(() => DynamoDbRealAzureLoadQualificationTests.RunWorkerAsync(
            client, tracker, new CompletedIterationCounter(), 0, TimeSpan.FromDays(1),
            Stopwatch.StartNew(), timeout.Token, strictObservation: true));
        Assert.Equal(2, tracker.Snapshot("GetItem").Completions);
        Assert.Equal(2, tracker.Snapshot("DeleteItem").Completions);
        Assert.Equal(1, tracker.Snapshot("UpdateItem").Completions);
        Assert.Equal(1, tracker.Snapshot("PutItem").Failures);
        Assert.Empty(state.Tables);
    }

    [Fact]
    public async Task Sqs_observation_reuses_qualification_cycle_and_stops_instead_of_accumulating_messages()
    {
        var state = new SqsState { FailSecondSend = true };
        using var client = new SqsClient(state);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tracker = new RealAzureWorkloadLoadTracker("sqs", SqsRealAzureLoadQualificationTests.Operations);
        await Assert.ThrowsAsync<InvalidDataException>(() => SqsRealAzureLoadQualificationTests.RunWorkerAsync(
            client, tracker, new CompletedIterationCounter(), 0, TimeSpan.FromDays(1),
            Stopwatch.StartNew(), timeout.Token, strictObservation: true));
        foreach (var operation in SqsRealAzureLoadQualificationTests.Operations)
            Assert.Equal(1, tracker.Snapshot(operation).Completions);
        Assert.Equal(1, tracker.Snapshot("SendMessage").Failures);
        Assert.Empty(state.Queues);
    }

    private sealed class DdbState
    {
        public string Role = "candidate";
        public bool FailSecondPut;
        public int Puts;
        public List<string> Calls = [];
        public Dictionary<string, Dictionary<string, Dictionary<string, AttributeValue>>> Tables = [];
        public void Call(string name, CancellationToken token) { token.ThrowIfCancellationRequested(); Calls.Add(Role + ":" + name); }
    }

    private sealed class DdbClient(DdbState state) : AmazonDynamoDBClient("offline", "offline",
        new AmazonDynamoDBConfig { ServiceURL = "http://127.0.0.1:1", MaxErrorRetry = 0 })
    {
        public override Task<CreateTableResponse> CreateTableAsync(CreateTableRequest request, CancellationToken token = default)
        {
            state.Call("CreateTable", token);
            Assert.Equal("pk", Assert.Single(request.KeySchema).AttributeName);
            state.Tables.Add(request.TableName, []);
            return Task.FromResult(new CreateTableResponse());
        }
        public override Task<DescribeTableResponse> DescribeTableAsync(DescribeTableRequest request, CancellationToken token = default)
        {
            state.Call("DescribeTable", token);
            if (!state.Tables.ContainsKey(request.TableName)) throw new DdbMissing("absent");
            return Task.FromResult(new DescribeTableResponse { Table = new() { TableStatus = TableStatus.ACTIVE } });
        }
        public override Task<PutItemResponse> PutItemAsync(PutItemRequest request, CancellationToken token = default)
        {
            state.Call("PutItem", token);
            if (++state.Puts == 2 && state.FailSecondPut) throw new InvalidDataException("bounded injected write failure");
            state.Tables[request.TableName][request.Item["pk"].S] = request.Item;
            return Task.FromResult(new PutItemResponse());
        }
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken token = default)
        {
            state.Call("GetItem", token);
            Assert.True(request.ConsistentRead);
            state.Tables[request.TableName].TryGetValue(request.Key["pk"].S, out var item);
            return Task.FromResult(new GetItemResponse { Item = item ?? [] });
        }
        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken token = default)
        {
            state.Call("UpdateItem", token);
            Assert.Equal("SET version = version + :one", request.UpdateExpression);
            state.Tables[request.TableName][request.Key["pk"].S]["version"].N = "2";
            return Task.FromResult(new UpdateItemResponse());
        }
        public override Task<DeleteItemResponse> DeleteItemAsync(DeleteItemRequest request, CancellationToken token = default)
        {
            state.Call("DeleteItem", token);
            state.Tables[request.TableName].Remove(request.Key["pk"].S);
            return Task.FromResult(new DeleteItemResponse());
        }
        public override Task<DeleteTableResponse> DeleteTableAsync(DeleteTableRequest request, CancellationToken token = default)
        {
            state.Call("DeleteTable", token); state.Tables.Remove(request.TableName);
            return Task.FromResult(new DeleteTableResponse());
        }
    }

    private sealed class QueueState
    {
        public Queue<string> Available = [];
        public string? Lease;
        public string? Receipt;
    }
    private sealed class SqsState
    {
        public string Role = "candidate";
        public bool FailSecondSend;
        public int Sends;
        public Dictionary<string, QueueState> Queues = [];
        public List<string> SettledBy = [];
    }
    private sealed class SqsClient(SqsState state) : AmazonSQSClient("offline", "offline",
        new AmazonSQSConfig { ServiceURL = "http://127.0.0.1:1", MaxErrorRetry = 0 })
    {
        public override Task<CreateQueueResponse> CreateQueueAsync(CreateQueueRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Assert.DoesNotContain(".fifo", request.QueueName);
            state.Queues.Add(request.QueueName, new());
            return Task.FromResult(new CreateQueueResponse { QueueUrl = request.QueueName });
        }
        public override Task<GetQueueUrlResponse> GetQueueUrlAsync(GetQueueUrlRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (!state.Queues.ContainsKey(request.QueueName)) throw new QueueDoesNotExistException("absent");
            return Task.FromResult(new GetQueueUrlResponse { QueueUrl = request.QueueName });
        }
        public override Task<ListQueuesResponse> ListQueuesAsync(ListQueuesRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new ListQueuesResponse
                { QueueUrls = state.Queues.Keys.Where(name => name.StartsWith(request.QueueNamePrefix, StringComparison.Ordinal)).ToList() });
        }
        public override Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (++state.Sends == 2 && state.FailSecondSend) throw new InvalidDataException("bounded injected send failure");
            state.Queues[request.QueueUrl].Available.Enqueue(request.MessageBody);
            return Task.FromResult(new SendMessageResponse());
        }
        public override Task<ReceiveMessageResponse> ReceiveMessageAsync(ReceiveMessageRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var queue = state.Queues[request.QueueUrl];
            Assert.Null(queue.Lease);
            if (!queue.Available.TryDequeue(out var body))
                return Task.FromResult(new ReceiveMessageResponse { Messages = [] });
            queue.Lease = body;
            queue.Receipt = state.Role + "-" + Guid.NewGuid();
            return Task.FromResult(new ReceiveMessageResponse
                { Messages = [new() { Body = body, ReceiptHandle = queue.Receipt }] });
        }
        public override Task<ChangeMessageVisibilityResponse> ChangeMessageVisibilityAsync(ChangeMessageVisibilityRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var queue = state.Queues[request.QueueUrl];
            Assert.Equal(0, request.VisibilityTimeout);
            Assert.Equal(queue.Receipt, request.ReceiptHandle);
            queue.Available.Enqueue(queue.Lease!); queue.Lease = null; queue.Receipt = null;
            return Task.FromResult(new ChangeMessageVisibilityResponse());
        }
        public override Task<DeleteMessageResponse> DeleteMessageAsync(DeleteMessageRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var queue = state.Queues[request.QueueUrl];
            Assert.Equal(queue.Receipt, request.ReceiptHandle);
            Assert.StartsWith(state.Role + "-", request.ReceiptHandle);
            state.SettledBy.Add(state.Role); queue.Lease = null; queue.Receipt = null;
            return Task.FromResult(new DeleteMessageResponse());
        }
        public override Task<DeleteQueueResponse> DeleteQueueAsync(DeleteQueueRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); state.Queues.Remove(request.QueueUrl);
            return Task.FromResult(new DeleteQueueResponse());
        }
    }
}
