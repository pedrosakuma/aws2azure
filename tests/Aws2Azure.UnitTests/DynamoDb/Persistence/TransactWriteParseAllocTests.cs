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
/// Tier 0 mechanism guard for the DOM-free TransactWriteItems parse path. Like
/// the PutItem/BatchWriteItem guards, this is a cheap, deterministic per-PR
/// assertion targeting the property that makes TransactWriteItems worth
/// changing: a request carries up to 100 per-action envelopes, and the old
/// shape (a <see cref="TransactWriteItem"/>-style model with four
/// <see cref="JsonElement"/> properties) materialized one retained
/// <see cref="JsonDocument"/> DOM for the present envelope of every action —
/// up to <b>N</b> non-pooled DOMs held until GC. The shipped shape (four
/// <see cref="JsonRange"/> properties + a transient, pooled
/// <see cref="JsonDocument"/> opened and disposed per present envelope) retains
/// <b>zero</b>. A refactor that switches the action envelopes back to retained
/// <see cref="JsonElement"/> DOMs collapses the win and fails the build.
///
/// <para>Both arms deserialize the SAME N-action array:</para>
/// <list type="bullet">
///   <item><b>retained-dom</b>: a model with <see cref="JsonElement"/> envelope
///   properties — N retained DOMs whose pooled metadata is never returned.</item>
///   <item><b>pooled-range</b>: the SHIPPED path — <see cref="JsonRange"/>
///   envelope properties (zero DOM) and a transient pooled
///   <see cref="JsonDocument"/> opened+disposed per present envelope, so its
///   rented metadata DB is reused across actions.</item>
/// </list>
///
/// <para>Per-op managed allocation is measured with
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> (exact, not sampled) and
/// the minimum across rounds is taken so sporadic test-infra allocations cannot
/// inflate the figure. The retained arm scales with N×item-size (KBs); the
/// pooled arm is dominated by the small backing arrays and is flat in item
/// size. The per-case floor leaves headroom for runtime drift while still
/// catching a collapse of the mechanism.</para>
/// </summary>
public class TransactWriteParseAllocTests
{
    private const int Actions = 100;
    private const int Iterations = 2_000;
    private const int Rounds = 3;
    private const int Warmup = 200;

    private readonly ITestOutputHelper _output;

    public TransactWriteParseAllocTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(0, 0, 0, 18_000)]      // lean — 100 small envelopes (~25.6 KB saved)
    [InlineData(0, 0, 512, 44_000)]    // payload-heavy (~55.2 KB saved)
    [InlineData(20, 20, 0, 147_000)]   // wide (~184 KB saved)
    public void Range_parse_allocates_less_than_retained_dom(int stringAttrs, int numberAttrs, int payloadBytes, double minSavingBytesPerOp)
    {
        byte[] body = BuildActionsArray(Actions, stringAttrs, numberAttrs, payloadBytes);

        // Correctness gate: both shapes must see the same N action envelopes and
        // the range must slice the same bytes the DOM model captured.
        var ranges = JsonSerializer.Deserialize(body, TransactAllocContext.Default.ListTransactItemRange)!;
        var doms = JsonSerializer.Deserialize(body, TransactAllocContext.Default.ListTransactItemDom)!;
        Assert.Equal(Actions, ranges.Count);
        Assert.Equal(Actions, doms.Count);
        for (int i = 0; i < Actions; i++)
        {
            bool put = i % 2 == 0;
            JsonRange r = put ? ranges[i].Put : ranges[i].Delete;
            JsonElement d = put ? doms[i].Put : doms[i].Delete;
            Assert.True(r.IsPresent, $"action {i} envelope range not captured.");
            Assert.True(
                body.AsSpan(r.Start, r.Length).SequenceEqual(RawText(d)),
                $"action {i} range did not slice the same envelope bytes.");
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
            $"N={Actions} s={stringAttrs} n={numberAttrs} pay={payloadBytes}  " +
            $"retained-dom={dom:F0} B/op  pooled-range={range:F0} B/op  saving={saving:F0} B/op");

        Assert.True(range < dom,
            $"pooled-range allocated {range:F0} B/op, not less than retained-DOM {dom:F0} B/op — " +
            "the TransactWriteItems parse path lost its allocation advantage.");
        Assert.True(saving >= minSavingBytesPerOp,
            $"pooled-range saved only {saving:F0} B/op (floor {minSavingBytesPerOp:F0}) vs retained-DOM — " +
            "per-request JsonElement DOMs appear to have crept back onto the transact write path.");
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

    // Old shape: deserialize N actions whose present envelope is a retained
    // JsonElement DOM, holding everything as the previous typed model did.
    private static int RunRetainedDom(byte[] body)
    {
        var items = JsonSerializer.Deserialize(body, TransactAllocContext.Default.ListTransactItemDom)!;
        int acc = 0;
        for (int i = 0; i < items.Count; i++)
        {
            JsonElement op = items[i].Put.ValueKind == JsonValueKind.Object ? items[i].Put : items[i].Delete;
            acc += (int)op.ValueKind;
        }

        return acc + items.Count;
    }

    // Shipped path: deserialize N JsonRange envelopes (zero DOM), then open and
    // dispose a transient pooled JsonDocument per present envelope.
    private static int RunPooledRange(byte[] body)
    {
        var items = JsonSerializer.Deserialize(body, TransactAllocContext.Default.ListTransactItemRange)!;
        int acc = 0;
        for (int i = 0; i < items.Count; i++)
        {
            JsonRange op = items[i].Put.IsPresent ? items[i].Put : items[i].Delete;
            using var doc = JsonDocument.Parse(body.AsMemory(op.Start, op.Length));
            acc += (int)doc.RootElement.ValueKind;
        }

        return acc + items.Count;
    }

    private static byte[] RawText(JsonElement e) => Encoding.UTF8.GetBytes(e.GetRawText());

    private static byte[] BuildActionsArray(int count, int stringAttrs, int numberAttrs, int payloadBytes)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            // Alternate Put / Delete to mirror a real mixed transaction.
            if (i % 2 == 0)
            {
                sb.Append("{\"Put\":{\"TableName\":\"t\",\"Item\":")
                  .Append(MakeItem(stringAttrs, numberAttrs, payloadBytes)).Append("}}");
            }
            else
            {
                sb.Append("{\"Delete\":{\"TableName\":\"t\",\"Key\":{\"pk\":{\"S\":\"k").Append(i).Append("\"}}}}");
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

internal sealed class TransactItemDom
{
    [JsonPropertyName("Put")] public JsonElement Put { get; set; }
    [JsonPropertyName("Delete")] public JsonElement Delete { get; set; }
}

internal sealed class TransactItemRange
{
    [JsonPropertyName("Put")] public JsonRange Put { get; set; }
    [JsonPropertyName("Delete")] public JsonRange Delete { get; set; }
}

[JsonSerializable(typeof(List<TransactItemDom>))]
[JsonSerializable(typeof(List<TransactItemRange>))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, AllowTrailingCommas = true)]
internal sealed partial class TransactAllocContext : JsonSerializerContext
{
}
