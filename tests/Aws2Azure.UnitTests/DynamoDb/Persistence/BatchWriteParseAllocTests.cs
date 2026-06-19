using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aws2Azure.Modules.DynamoDb.Internal;
using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Tier 0 mechanism guard for the DOM-free BatchWriteItem parse path. Like the
/// PutItem guard, this is a cheap, deterministic per-PR assertion — but it
/// targets the property that makes BatchWriteItem worth changing: the request
/// carries up to 25 action envelopes, and the old shape
/// (<c>List&lt;JsonElement&gt;</c> + a per-entry <c>JsonElement.Clone()</c> retained
/// in the work unit for UnprocessedItems) retained <b>2N</b> non-pooled DOMs for
/// the whole batch. The shipped shape (<c>List&lt;JsonRange&gt;</c> + a transient,
/// pooled <see cref="JsonDocument"/> opened and disposed per entry, and the
/// envelope re-sliced from the request buffer only on the rare throttle echo)
/// retains <b>zero</b>. A refactor that switches the request list back to
/// retained <see cref="JsonElement"/> DOMs collapses the win and fails the build.
///
/// <para>Both arms deserialize the SAME N-entry array:</para>
/// <list type="bullet">
///   <item><b>retained-dom</b>: <c>List&lt;JsonElement&gt;</c> (N retained DOMs) plus
///   a <c>Clone()</c> per entry (the old work-unit echo), held to end-of-batch —
///   2N DOMs whose pooled metadata is never returned.</item>
///   <item><b>pooled-range</b>: the SHIPPED path — <c>List&lt;JsonRange&gt;</c> (zero
///   DOM) and a transient pooled <see cref="JsonDocument"/> opened+disposed per
///   entry, so its rented metadata DB is reused across entries.</item>
/// </list>
///
/// <para>Per-op managed allocation is measured with
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> (exact, not sampled) and the
/// minimum across rounds is taken so sporadic test-infra allocations cannot
/// inflate the figure. The retained arm scales with N×item-size (KBs); the pooled
/// arm is dominated by the small <c>List&lt;JsonRange&gt;</c> backing array and is
/// flat in item size. The per-case floor leaves headroom for runtime drift while
/// still catching a collapse of the mechanism.</para>
/// </summary>
public class BatchWriteParseAllocTests
{
    private const int Entries = 25;
    private const int Iterations = 5_000;
    private const int Rounds = 3;
    private const int Warmup = 200;

    private readonly ITestOutputHelper _output;

    public BatchWriteParseAllocTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(0, 0, 0, 5_000)]      // lean — 25 small envelopes (~7.2 KB saved)
    [InlineData(0, 0, 512, 10_000)]   // payload-heavy (~14.9 KB saved)
    [InlineData(20, 20, 0, 35_000)]   // wide (~48.5 KB saved)
    public void Range_parse_allocates_less_than_retained_dom(int stringAttrs, int numberAttrs, int payloadBytes, double minSavingBytesPerOp)
    {
        byte[] body = BuildEntriesArray(Entries, stringAttrs, numberAttrs, payloadBytes);

        // Correctness gate: both shapes must see the same N object envelopes, so
        // the alloc numbers describe a correct parse and not a shortcut.
        var ranges = JsonSerializer.Deserialize(body, BatchAllocContext.Default.ListJsonRange)!;
        var doms = JsonSerializer.Deserialize(body, BatchAllocContext.Default.ListJsonElement)!;
        Assert.Equal(Entries, ranges.Count);
        Assert.Equal(Entries, doms.Count);
        for (int i = 0; i < Entries; i++)
        {
            Assert.True(ranges[i].IsPresent, $"entry {i} range not captured.");
            using var doc = JsonDocument.Parse(body.AsMemory(ranges[i].Start, ranges[i].Length));
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.True(
                body.AsSpan(ranges[i].Start, ranges[i].Length).SequenceEqual(RawText(doms[i])),
                $"entry {i} range did not slice the same envelope bytes.");
        }

        for (int i = 0; i < Warmup; i++)
        {
            _ = RunRetainedDom(body);
            _ = RunPooledRange(body);
        }

        double dom = MeasureMinBytesPerOp(() => RunRetainedDom(body));
        double range = MeasureMinBytesPerOp(() => RunPooledRange(body));
        double saving = dom - range;

        _output.WriteLine(
            $"N={Entries} s={stringAttrs} n={numberAttrs} pay={payloadBytes}  " +
            $"retained-dom={dom:F0} B/op  pooled-range={range:F0} B/op  saving={saving:F0} B/op");

        Assert.True(range < dom,
            $"pooled-range allocated {range:F0} B/op, not less than retained-DOM {dom:F0} B/op — " +
            "the BatchWriteItem parse path lost its allocation advantage.");
        Assert.True(saving >= minSavingBytesPerOp,
            $"pooled-range saved only {saving:F0} B/op (floor {minSavingBytesPerOp:F0}) vs retained-DOM — " +
            "per-request JsonElement DOMs appear to have crept back onto the batch write path.");
    }

    private static double MeasureMinBytesPerOp(Func<int> arm)
    {
        double best = double.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
            {
                _ = arm();
            }
            double perOp = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Iterations;
            if (perOp < best)
            {
                best = perOp;
            }
        }

        return best;
    }

    // Old shape: deserialize N retained JsonElement DOMs and clone each (the old
    // work-unit echo), holding everything to the end of the "batch" exactly as the
    // previous handler did.
    private static int RunRetainedDom(byte[] body)
    {
        var entries = JsonSerializer.Deserialize(body, BatchAllocContext.Default.ListJsonElement)!;
        var held = new JsonElement[entries.Count];
        int acc = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            held[i] = entries[i].Clone();
            acc += (int)held[i].ValueKind;
        }

        return acc + held.Length;
    }

    // Shipped path: deserialize N JsonRanges (zero DOM), then open and dispose a
    // transient pooled JsonDocument per entry — the rented metadata DB is reused.
    private static int RunPooledRange(byte[] body)
    {
        var ranges = JsonSerializer.Deserialize(body, BatchAllocContext.Default.ListJsonRange)!;
        int acc = 0;
        for (int i = 0; i < ranges.Count; i++)
        {
            using var doc = JsonDocument.Parse(body.AsMemory(ranges[i].Start, ranges[i].Length));
            acc += (int)doc.RootElement.ValueKind;
        }

        return acc + ranges.Count;
    }

    private static byte[] RawText(JsonElement e) => Encoding.UTF8.GetBytes(e.GetRawText());

    private static byte[] BuildEntriesArray(int count, int stringAttrs, int numberAttrs, int payloadBytes)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            // Alternate Put / Delete envelopes to mirror a real mixed batch.
            if (i % 2 == 0)
            {
                sb.Append("{\"PutRequest\":{\"Item\":").Append(MakeItem(stringAttrs, numberAttrs, payloadBytes)).Append("}}");
            }
            else
            {
                sb.Append("{\"DeleteRequest\":{\"Key\":{\"pk\":{\"S\":\"k").Append(i).Append("\"}}}}");
            }
        }

        sb.Append(']');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string MakeItem(int stringAttrs, int numberAttrs, int payloadBytes)
    {
        var sb = new StringBuilder();
        sb.Append("{\"pk\":{\"S\":\"p\"}");
        for (int i = 0; i < stringAttrs; i++)
        {
            sb.Append(",\"s").Append(i).Append("\":{\"S\":\"value_").Append(i).Append("\"}");
        }

        for (int i = 0; i < numberAttrs; i++)
        {
            sb.Append(",\"n").Append(i).Append("\":{\"N\":\"").Append(i * 7).Append("\"}");
        }

        if (payloadBytes > 0)
        {
            sb.Append(",\"payload\":{\"S\":\"").Append('x', payloadBytes).Append("\"}");
        }

        sb.Append('}');
        return sb.ToString();
    }
}

[JsonSerializable(typeof(List<JsonRange>))]
[JsonSerializable(typeof(List<JsonElement>))]
internal sealed partial class BatchAllocContext : JsonSerializerContext
{
}
