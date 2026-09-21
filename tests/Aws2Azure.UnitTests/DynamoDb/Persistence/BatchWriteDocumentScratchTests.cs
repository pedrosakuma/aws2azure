using System;
using System.Buffers;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

[Collection(DynamoDbTestCollection.Name)]
public sealed class BatchWriteDocumentScratchTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(256)]
    [InlineData(8192)]
    public void Text_document_avoids_fresh_scratch_allocation(int payloadLength)
    {
        using var document = JsonDocument.Parse(
            "{\"payload\":{\"S\":\"" + new string('x', payloadLength) + "\"}}");
        var item = document.RootElement;
        byte[] Legacy()
        {
            DynamoDbMetrics.RecordWriteBodyFormat(false);
            var writer = new ArrayBufferWriter<byte>(1024);
            InferredAttributeStorage.WriteCosmosDocument(writer, "k", "p", item);
            return writer.WrittenSpan.ToArray();
        }
        byte[] Current() => ItemHandlers.BuildItemDocumentBytes("k", "p", item, binary: false);
        Assert.Equal(Legacy(), Current());
        var legacy = TranslationAllocGate.MeasureMinBytesPerOp(() => Legacy().Length);
        var current = TranslationAllocGate.MeasureMinBytesPerOp(() => Current().Length);
        output.WriteLine($"payload={payloadLength}; legacy={legacy:F0} B/item; current={current:F0} B/item; saving={legacy - current:F0} B/item");
        Assert.True(legacy - current >= 1024, "The text document path must reuse its temporary scratch buffer.");
    }

    [Theory]
    [InlineData(false, 256)]
    [InlineData(false, 8192)]
    [InlineData(true, 8192)]
    public void Retained_batch_bodies_survive_input_disposal_and_scratch_reuse(bool binary, int payloadLength)
    {
        var bodies = new byte[25][];
        var expected = new byte[25][];
        for (var i = 0; i < bodies.Length; i++)
        {
            using var document = JsonDocument.Parse(
                "{\"payload\":{\"S\":\"" + new string((char)('a' + i), payloadLength) + "\"}}");
            bodies[i] = ItemHandlers.BuildItemDocumentBytes("k" + i, "p", document.RootElement, binary, ttlSeconds: 42);
            var writer = new ArrayBufferWriter<byte>();
            InferredAttributeStorage.WriteCosmosDocument(writer, "k" + i, "p", document.RootElement, ttlSeconds: 42);
            expected[i] = writer.WrittenSpan.ToArray();
        }
        for (var i = 0; i < bodies.Length; i++)
        {
            if (!binary) Assert.Equal(expected[i], bodies[i]);
            else
            {
                var decoded = new ArrayBufferWriter<byte>();
                CosmosBinaryDecoder.Decode(bodies[i], decoded);
                Assert.Equal(expected[i], decoded.WrittenSpan.ToArray());
            }
        }
    }

    [Fact]
    public void Encoding_failure_does_not_poison_subsequent_scratch_use()
    {
        var disposed = JsonDocument.Parse("""{"value":{"S":"before"}}""");
        var stale = disposed.RootElement;
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ItemHandlers.BuildItemDocumentBytes("k", "p", stale));
        using var valid = JsonDocument.Parse("""{"value":{"S":"after"}}""");
        var expected = new ArrayBufferWriter<byte>();
        InferredAttributeStorage.WriteCosmosDocument(expected, "k", "p", valid.RootElement);
        Assert.Equal(expected.WrittenSpan.ToArray(), ItemHandlers.BuildItemDocumentBytes("k", "p", valid.RootElement));
    }
}
