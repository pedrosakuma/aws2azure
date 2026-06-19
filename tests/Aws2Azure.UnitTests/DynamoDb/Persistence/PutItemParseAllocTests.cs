using System;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Tier 0 mechanism guard for the DOM-free PutItem parse path. The dev-only
/// BenchmarkDotNet <c>PutItemParseBenchmarks</c> proves the win but never runs in
/// CI; this is a cheap, deterministic per-PR assertion that the shipped parse
/// (<c>Item</c> bound to a <see cref="JsonRange"/> via converter + a transient,
/// pooled <see cref="JsonDocument"/> for the validators) keeps allocating
/// strictly — and substantially — less than the previous path that retained a
/// per-request <see cref="JsonElement"/> DOM. A refactor that switches
/// <c>PutItemRequest.Item</c> back to a retained <see cref="JsonElement"/> (whose
/// metadata DB is non-pooled and lives as long as the request) collapses the win
/// and fails the build.
///
/// <para>Both arms deserialize the SAME PutItem body through the SHIPPED
/// <see cref="PutItemRequest"/> (so the request-envelope cost is identical) and
/// open a <see cref="JsonDocument"/> over the captured item range; the only
/// difference is the document's lifetime:</para>
/// <list type="bullet">
///   <item><b>retained-dom</b>: the item <see cref="JsonDocument"/> is NEVER
///   disposed — its pooled metadata DB is never returned, so each op allocates a
///   fresh backing array sized to the item, exactly as the old
///   deserializer-retained <see cref="JsonElement"/> did.</item>
///   <item><b>pooled-range</b>: the SHIPPED path — the transient
///   <see cref="JsonDocument"/> is disposed, returning its rented metadata DB so
///   it is reused across ops.</item>
/// </list>
///
/// <para>Per-op managed allocation is measured with
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> (exact, not sampled) and the
/// minimum across rounds is taken so sporadic test-infra allocations cannot
/// inflate the figure. The retained DOM grows with item size (~0.5–4.4 KB across
/// the shapes below) while the pooled path is ~flat (~300 B); the per-case floor
/// leaves headroom for runtime drift while still catching a collapse of the
/// mechanism. Deterministic and reproducible — not a flaky timing benchmark.</para>
/// </summary>
public class PutItemParseAllocTests
{
    private const int Iterations = 20_000;
    private const int Rounds = 3;
    private const int Warmup = 500;

    private static readonly JsonDocumentOptions ParseOptions = new() { AllowTrailingCommas = true };

    private readonly ITestOutputHelper _output;

    public PutItemParseAllocTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(0, 0, 0, 32)]       // lean — small item, small retained DB (~150 B saved)
    [InlineData(0, 0, 512, 128)]    // payload-heavy (~280 B saved)
    [InlineData(20, 20, 0, 1024)]   // wide (~4 KB saved)
    public void Range_parse_allocates_less_than_retained_dom(int stringAttrs, int numberAttrs, int payloadBytes, double minSavingBytesPerOp)
    {
        byte[] body = BuildPutItemBody(stringAttrs, numberAttrs, payloadBytes, out byte[] itemBytes);

        // Correctness gate: the shipped converter must slice the exact item bytes
        // and the pooled re-parse must see the same object the DOM arm sees, so
        // the alloc numbers describe a correct parse and not a shortcut.
        var shipped = JsonSerializer.Deserialize(body, ItemJsonContext.Default.PutItemRequest)!;
        Assert.True(shipped.Item.IsPresent, "converter did not capture the Item range.");
        Assert.True(
            body.AsSpan(shipped.Item.Start, shipped.Item.Length).SequenceEqual(itemBytes),
            "converter range did not slice the exact Item bytes — alloc comparison would be meaningless.");
        using (var doc = JsonDocument.Parse(body.AsMemory(shipped.Item.Start, shipped.Item.Length), ParseOptions))
        {
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }

        for (int i = 0; i < Warmup; i++)
        {
            _ = RunRetainedDom(body);
            _ = RunRange(body);
        }

        double dom = MeasureMinBytesPerOp(() => RunRetainedDom(body));
        double range = MeasureMinBytesPerOp(() => RunRange(body));
        double saving = dom - range;

        _output.WriteLine(
            $"s={stringAttrs} n={numberAttrs} pay={payloadBytes}  " +
            $"retained-dom={dom:F0} B/op  pooled-range={range:F0} B/op  saving={saving:F0} B/op");

        Assert.True(range < dom,
            $"pooled-range parse allocated {range:F0} B/op, not less than retained-DOM {dom:F0} B/op — " +
            "the PutItem parse path lost its allocation advantage.");
        Assert.True(saving >= minSavingBytesPerOp,
            $"pooled-range saved only {saving:F0} B/op (floor {minSavingBytesPerOp:F0}) vs retained-DOM — " +
            "a per-request JsonElement DOM appears to have crept back onto the write path.");
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

    // Old shape, isolated: pay the identical request-envelope + item-parse cost as
    // the shipped path, but RETAIN the item's JsonDocument (never disposed) the way
    // the old deserializer-retained JsonElement did. Its pooled metadata DB is never
    // returned, so each op allocates a fresh backing array sized to the item — this
    // is exactly the per-request DOM the change eliminated.
    private static int RunRetainedDom(byte[] body)
    {
        var req = JsonSerializer.Deserialize(body, ItemJsonContext.Default.PutItemRequest)!;
        var doc = JsonDocument.Parse(body.AsMemory(req.Item.Start, req.Item.Length), ParseOptions);
        return (int)doc.RootElement.ValueKind;
    }

    // Shipped path: deserialize with the JsonRange converter, then open and DISPOSE
    // the same transient pooled JsonDocument the handler opens for its validators —
    // the metadata DB is rented and returned, so it is reused across ops.
    private static int RunRange(byte[] body)
    {
        var req = JsonSerializer.Deserialize(body, ItemJsonContext.Default.PutItemRequest)!;
        using var doc = JsonDocument.Parse(body.AsMemory(req.Item.Start, req.Item.Length), ParseOptions);
        return (int)doc.RootElement.ValueKind;
    }

    private static byte[] BuildPutItemBody(int stringAttrs, int numberAttrs, int payloadBytes, out byte[] itemBytes)
    {
        string itemJson = MakeItem(stringAttrs, numberAttrs, payloadBytes);
        itemBytes = Encoding.UTF8.GetBytes(itemJson);
        string body = "{\"TableName\":\"bench-table\",\"Item\":" + itemJson + ",\"ReturnValues\":\"NONE\"}";
        return Encoding.UTF8.GetBytes(body);
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
