using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Buffers;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

[Collection(DynamoDbTestCollection.Name)]
public sealed class BatchWriteOwnershipTests
{
    private const string Metadata = """
        {"id":"__aws2azure_table_meta__","_a2a_pk":"__aws2azure_table_meta__","_meta":"table",
        "tableName":"orders","attributeDefinitions":[{"name":"pk","type":"S"}],
        "keySchema":[{"name":"pk","keyType":"HASH"}],"billingMode":"PAY_PER_REQUEST"}
        """;

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(false, 0, true)]
    [InlineData(true, 0, true)]
    [InlineData(false, 1, true)]
    [InlineData(false, 2, false)]
    [InlineData(false, 3, false)]
    public async Task Pending_consumers_keep_independent_bodies_until_settled(bool cancel, int scenario, bool binary)
    {
        CosmosOpsShared.MetadataCache.Clear();
        using var pool = new PoolObserver();
        using var consumer = new ControlledConsumer(pool);
        var writeHosts = new ConcurrentDictionary<string, byte>();
        using var handler = new Handler(async (request, _) =>
        {
            if (scenario == 2 && request.RequestUri!.AbsolutePath == "/")
                return Response(200, """
                    {"enableMultipleWriteLocations":false,
                    "writableLocations":[{"name":"East US","databaseAccountEndpoint":"https://example-east.documents.azure.com/"}],
                    "readableLocations":[{"name":"East US","databaseAccountEndpoint":"https://example-east.documents.azure.com/"}]}
                    """);
            if (request.Method == HttpMethod.Get) return Response(200, Metadata);
            writeHosts.TryAdd(request.RequestUri!.Host, 0);
            using var stream = new BorrowStream(consumer);
            await request.Content!.CopyToAsync(stream, CancellationToken.None);
            var ordinal = Interlocked.Increment(ref consumer.Responses);
            var response = Response(scenario switch
            {
                1 => ordinal switch { 1 => 503, 2 => 429, _ => 201 },
                2 => ordinal == 1 ? 403 : 201,
                3 => ordinal == 1 ? 400 : 201,
                _ => 201,
            });
            if (scenario == 2 && ordinal == 1) response.Headers.TryAddWithoutValidation("x-ms-substatus", "3");
            return response;
        });
        using var transport = new AzureHttpClient(handler, ownsHandler: false,
            new AzureHttpClientOptions { MaxAttempts = scenario == 2 ? 1 : 2, BaseRetryDelay = TimeSpan.FromMilliseconds(1) });
        var client = Client(transport, binary, scenario == 2);
        using var responseBody = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = responseBody;
        using var cancellation = new CancellationTokenSource();
        var requestBody = Request(25);
        var operation = BatchWriteItemHandler.HandleBatchWriteItemAsync(context, requestBody, client, cancellation.Token);
        await consumer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(operation.IsCompleted);
            Assert.Equal(0, pool.WatchedReturns);
            if (cancel)
            {
                cancellation.Cancel();
                Assert.False(operation.IsCompleted);
                Assert.Equal(0, pool.WatchedReturns);
            }
        }
        finally { consumer.Release.Set(); }
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(10)));
        else await operation.WaitAsync(TimeSpan.FromSeconds(10));
        await context.Response.BodyWriter.CompleteAsync();
        Assert.True(consumer.Errors.IsEmpty, "A consumer observed changed or poisoned bytes.");
        Assert.True(pool.WatchedCount > 0);
        Assert.Equal(pool.WatchedCount, pool.WatchedReturns);
        Assert.All(pool.ReturnCounts.Values, count => Assert.Equal(1, count));
        Assert.All(consumer.RentingThreads, thread => Assert.NotEqual(consumer.ThreadId, thread));
        if (!cancel)
        {
            Assert.Equal(scenario == 3 ? 400 : 200, context.Response.StatusCode);
            if (scenario == 1)
            {
                Assert.Equal(26, consumer.Responses);
                using var response = JsonDocument.Parse(responseBody.ToArray());
                Assert.Equal(1, response.RootElement.GetProperty("UnprocessedItems").GetProperty("orders").GetArrayLength());
            }
            if (scenario == 2)
            {
                Assert.Equal(26, consumer.Responses);
                Assert.Equal(2, writeHosts.Count);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Later_validation_metadata_or_encoding_failure_returns_prior_leases_without_mutation(int failure)
    {
        CosmosOpsShared.MetadataCache.Clear();
        var writes = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.Method != HttpMethod.Get) Interlocked.Increment(ref writes);
            if (failure == 1 && request.RequestUri!.AbsolutePath.Contains("/later/", StringComparison.Ordinal))
                throw new InvalidOperationException("Controlled metadata failure.");
            return Task.FromResult(Response(200, Metadata));
        });
        using var transport = new AzureHttpClient(handler, ownsHandler: false,
            new AzureHttpClientOptions { MaxAttempts = 1 });
        using var responseBody = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = responseBody;
        var valid = """{"PutRequest":{"Item":{"pk":{"S":"a"}}}}""";
        var body = failure switch
        {
            1 => """{"RequestItems":{"orders":[""" + valid + """],"later":[""" + valid + "]}}",
            2 => """{"RequestItems":{"orders":[""" + valid +
                 """,{"PutRequest":{"Item":{"pk":{"S":"b"},"payload":{"S":"\uD800"}}}}]}}""",
            _ => """{"RequestItems":{"orders":[""" + valid + """,{"PutRequest":{}}]}}""",
        };
        using var pool = new PoolObserver();
        var operation = BatchWriteItemHandler.HandleBatchWriteItemAsync(context, Encoding.UTF8.GetBytes(body), Client(transport), default);
        if (failure != 0) await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        else
        {
            await operation;
            Assert.Equal(400, context.Response.StatusCode);
        }
        await context.Response.BodyWriter.CompleteAsync();
        Assert.Equal(0, writes);
        Assert.True(pool.Rents > 0);
        Assert.Equal(pool.Rents, pool.Returns);
    }

    [Fact]
    public async Task Poisoned_return_sensor_detects_a_premature_return()
    {
        using var pool = new PoolObserver();
        using var consumer = new ControlledConsumer(pool);
        using var owner = new PooledByteBufferWriter();
        owner.GetSpan(16)[..16].Fill(0x61);
        owner.Advance(16);
        var consume = consumer.Write(owner.WrittenMemory);
        owner.Dispose();
        consumer.Release.Set();
        await consume;
        Assert.False(consumer.Errors.IsEmpty);
        Assert.Equal(1, pool.WatchedReturns);
    }

    private static CosmosClient Client(AzureHttpClient transport, bool binary = false, bool regional = false) => new(transport,
        new CosmosCredentials
        {
            Endpoint = "https://example.documents.azure.com/", DatabaseName = "main",
            PreferredRegions = regional ? ["East US"] : null,
        },
        new MasterKeyCosmosAuthenticator("MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY="),
        cosmosBinaryRequests: binary);

    private static HttpResponseMessage Response(int status, string body = "{}") => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static byte[] Request(int count) => Encoding.UTF8.GetBytes(
        "{\"RequestItems\":{\"orders\":[" + string.Join(",", Enumerable.Range(0, count).Select(i =>
            "{\"PutRequest\":{\"Item\":{\"pk\":{\"S\":\"k" + i + "\"},\"payload\":{\"S\":\"" +
            new string((char)('a' + i), 8192) + "\"}}}}")) + "]}}");

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class PoolObserver : EventListener
    {
        private readonly ConcurrentDictionary<int, int> _rentalThreads = new();
        private readonly ConcurrentDictionary<int, byte[]> _watched = new();
        private readonly ConcurrentDictionary<int, bool> _reusedAfterReturn = new();
        public readonly ConcurrentDictionary<int, int> ReturnCounts = new();
        public int Rents, Returns;
        public int WatchedCount => _watched.Count;
        public int WatchedReturns => ReturnCounts.Values.Sum();

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource") EnableEvents(source, EventLevel.Verbose);
        }

        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (_rentalThreads is null || data.Payload is not { Count: > 0 } || data.Payload[0] is not int id) return;
            if (data.EventName == "BufferRented")
            {
                Interlocked.Increment(ref Rents);
                _rentalThreads[id] = Environment.CurrentManagedThreadId;
                if (ReturnCounts.ContainsKey(id)) _reusedAfterReturn[id] = true;
            }
            else if (data.EventName == "BufferReturned")
            {
                Interlocked.Increment(ref Returns);
                if (!_reusedAfterReturn.ContainsKey(id) && _watched.TryGetValue(id, out var buffer))
                {
                    ReturnCounts.AddOrUpdate(id, 1, (_, count) => count + 1);
                    Array.Fill(buffer, (byte)0xdd);
                }
            }
        }

        public int? Watch(ReadOnlyMemory<byte> memory)
        {
            if (!MemoryMarshal.TryGetArray(memory, out var segment)) return null;
            var id = segment.Array!.GetHashCode();
            if (!_rentalThreads.TryGetValue(id, out var thread)) return null;
            _watched.TryAdd(id, segment.Array);
            return thread;
        }
    }

    private sealed class ControlledConsumer : IDisposable
    {
        private readonly BlockingCollection<(ReadOnlyMemory<byte> Memory, byte[] Expected, TaskCompletionSource Completion)> _queue = new();
        private readonly PoolObserver _pool;
        private readonly Thread _thread;
        public readonly ManualResetEventSlim Release = new();
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<int> RentingThreads = new();
        public readonly ConcurrentQueue<bool> Errors = new();
        public int Responses;
        public int ThreadId => _thread.ManagedThreadId;

        public ControlledConsumer(PoolObserver pool)
        {
            _pool = pool;
            _thread = new Thread(() =>
            {
                foreach (var entry in _queue.GetConsumingEnumerable())
                {
                    Release.Wait();
                    if (!entry.Memory.Span.SequenceEqual(entry.Expected)) Errors.Enqueue(true);
                    entry.Completion.SetResult();
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public ValueTask Write(ReadOnlyMemory<byte> memory)
        {
            var thread = _pool.Watch(memory);
            if (thread.HasValue) RentingThreads.Enqueue(thread.Value);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((memory, memory.ToArray(), completion));
            Entered.TrySetResult();
            return new ValueTask(completion.Task);
        }

        public void Dispose()
        {
            Release.Set();
            _queue.CompleteAdding();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)));
            Release.Dispose();
            _queue.Dispose();
        }
    }

    private sealed class BorrowStream(ControlledConsumer consumer) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => consumer.Write(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            consumer.Write(buffer.AsMemory(offset, count)).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
