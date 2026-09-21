using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Operations;
using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.UnitTests.DynamoDb;

[Collection(DynamoDbTestCollection.Name)]
public sealed class BatchWriteBodyAccountingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(256)]
    [InlineData(8192)]
    public void Retained_body_capacity_and_encoding_allocation_are_explicit(int payloadBytes)
    {
        var documents = new JsonDocument[25];
        var keys = new string[25];
        var compact = new byte[25][];
        var owned = new ItemHandlers.ItemDocumentBody[25];
        try
        {
            for (var i = 0; i < documents.Length; i++)
            {
                var key = $"b004i{i:D2}";
                documents[i] = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    pk = new { S = key }, sk = new { S = key }, payload = new { S = new string('x', payloadBytes) },
                }));
                Assert.True(KeyScalarCodec.TryEncode("S",
                    new ParsedAttributeValue("S", documents[i].RootElement.GetProperty("pk").GetProperty("S")),
                    "key", out var encoded, out _));
                keys[i] = encoded;
            }
            for (var warmup = 0; warmup < 2; warmup++)
            {
                for (var i = 0; i < documents.Length; i++)
                {
                    _ = ItemHandlers.BuildItemDocumentBytes(keys[i], keys[i], documents[i].RootElement);
                    owned[i] = ItemHandlers.ItemDocumentBody.Create(keys[i], keys[i], documents[i].RootElement, false);
                }
                for (var i = 0; i < owned.Length; i++) { owned[i].Dispose(); owned[i] = default; }
            }
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < documents.Length; i++)
                compact[i] = ItemHandlers.BuildItemDocumentBytes(keys[i], keys[i], documents[i].RootElement);
            var compactAllocation = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < documents.Length; i++)
                owned[i] = ItemHandlers.ItemDocumentBody.Create(keys[i], keys[i], documents[i].RootElement, false);
            var ownedAllocation = GC.GetAllocatedBytesForCurrentThread() - before;
            long capacity = 0, bytes = 0;
            for (var i = 0; i < owned.Length; i++)
            {
                Assert.True(owned[i].Memory.Span.SequenceEqual(compact[i]));
                Assert.True(MemoryMarshal.TryGetArray(owned[i].Memory, out var segment));
                capacity += segment.Array!.Length;
                bytes += owned[i].Memory.Length;
            }
            Assert.True(capacity >= bytes);
            Assert.True(ownedAllocation < compactAllocation);
            output.WriteLine($"payloadBytes={payloadBytes}; items=25; compactBodyBytes={bytes}; rentedCapacityBytes={capacity}; " +
                $"compactEncodingAllocatedBytes={compactAllocation}; retainedEncodingAllocatedBytes={ownedAllocation}");
        }
        finally
        {
            for (var i = 0; i < owned.Length; i++) owned[i].Dispose();
            foreach (var document in documents) document?.Dispose();
        }
    }
}
