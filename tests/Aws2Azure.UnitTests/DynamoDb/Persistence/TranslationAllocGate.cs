using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aws2Azure.Core.Buffers;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Tier 0 micro-benchmark regression gate (issue #459). Promotes the
/// backendless DynamoDb translation micro-benchmarks
/// (<c>CosmosFusedEnvelopeBenchmarks</c> decode,
/// <c>Spike332.CosmosWriteEncodeBenchmarks</c> encode) — which prove the
/// CPU/alloc win but never run in CI — into a cheap, deterministic per-PR
/// alloc/op ceiling gate.
///
/// <para>Each scenario drives a <b>production</b> DDB→Cosmos translation entry
/// point over a representative document shape with no backend, measures managed
/// bytes allocated per op via <see cref="GC.GetAllocatedBytesForCurrentThread"/>
/// (exact, not sampled — deterministic byte-for-byte across runs, independent of
/// CPU/runner noise), and fails when it exceeds the committed ceiling in
/// <c>docs/perf/microbench-reference.json</c>. Only <b>alloc/op</b> is gated;
/// absolute time is a non-goal (runner-bound noise), per #459.</para>
/// </summary>
internal static class TranslationAllocGate
{
    private const string Id = "route-0001";
    private const string Pk = "p00";

    /// <summary>Document shapes mirror the BenchmarkDotNet <c>Docs</c> set.</summary>
    private static readonly (string Name, int StringAttrs, int NumberAttrs, int PayloadBytes)[] Shapes =
    [
        ("lean", 0, 0, 0),
        ("payload_512", 0, 0, 512),
        ("wide_20s_20n", 20, 20, 0),
    ];

    /// <summary>
    /// One measured translation op: a stable name (matched against the
    /// reference JSON) and a closure that runs the production path once,
    /// returning the bytes produced so the JIT cannot elide it.
    /// </summary>
    internal sealed record Scenario(string Name, Func<int> Run);

    /// <summary>
    /// Builds the full backendless scenario matrix: decode (fused binary +
    /// text) and encode (single-pass wire text + binary) over every shape.
    /// Inputs are materialized once here (outside the measured window) and
    /// captured by the per-op closures.
    /// </summary>
    public static IReadOnlyList<Scenario> BuildScenarios()
    {
        var scenarios = new List<Scenario>(Shapes.Length * 4);

        foreach (var shape in Shapes)
        {
            byte[] itemUtf8 = Encoding.UTF8.GetBytes(BuildDdbItem(shape.StringAttrs, shape.NumberAttrs, shape.PayloadBytes));

            // Derive the Cosmos document (text + CosmosBinary) the read path
            // consumes, straight from the same DDB item via the production
            // encoder, so the decode/encode arms share one input shape.
            byte[] cosmosTextUtf8;
            using (var itemDoc = JsonDocument.Parse(itemUtf8))
            {
                string cosmosText = InferredAttributeStorage.BuildCosmosDocument(Id, Pk, itemDoc.RootElement);
                cosmosTextUtf8 = Encoding.UTF8.GetBytes(cosmosText);
            }

            byte[] cosmosBinary = CosmosBinaryTestEncoder.Encode(Encoding.UTF8.GetString(cosmosTextUtf8));

            string s = shape.Name;

            scenarios.Add(new Scenario(
                $"ddb.decode.GetItemEnvelope.fused ({s})",
                () => DecodeFused(cosmosBinary)));

            scenarios.Add(new Scenario(
                $"ddb.decode.GetItemEnvelope.text ({s})",
                () => DecodeText(cosmosTextUtf8)));

            scenarios.Add(new Scenario(
                $"ddb.encode.CosmosDocument.text.wire ({s})",
                () => EncodeTextWire(itemUtf8)));

            scenarios.Add(new Scenario(
                $"ddb.encode.CosmosDocument.binary.wire ({s})",
                () => EncodeBinaryWire(itemUtf8)));
        }

        AddProjectionScenarios(scenarios);

        return scenarios;
    }

    // ---------------- projection (ProjectionExpression apply) ----------------
    //
    // Projection.Apply is the in-proxy compute a ProjectionExpression drives on
    // the materialized read path (GetItem/Query/Scan/BatchGetItem): it was
    // previously ungated. Four scenarios isolate the distinct allocation
    // behaviours over one representative wide item:
    //   toplevel    — whole top-level attributes (zero-copy value references)
    //   nested_map  — prune a subset of a map's members (BuildPruned+Materialize)
    //   nested_list — compact a list to selected indices (BuildPruned+Materialize)
    //   nested_mixed— a blend of whole, map-member and list-index paths
    // The projection is compiled once here (outside the measured window) via the
    // production parser; the measured op is a single Apply over the parsed item.

    private static void AddProjectionScenarios(List<Scenario> scenarios)
    {
        // Kept rooted for process lifetime so the extracted JsonElements stay
        // valid (they reference their parent JsonDocument).
        var item = ParseItem(BuildProjectionItem());

        scenarios.Add(new Scenario(
            "ddb.project.toplevel (wide)",
            RunProjection(item, "pk, sk, str0, str5, num3, num7, profile, tags")));

        scenarios.Add(new Scenario(
            "ddb.project.nested_map (wide)",
            RunProjection(item, "profile.name, profile.email, profile.score")));

        scenarios.Add(new Scenario(
            "ddb.project.nested_list (wide)",
            RunProjection(item, "tags[0], tags[2], tags[4]")));

        scenarios.Add(new Scenario(
            "ddb.project.nested_mixed (wide)",
            RunProjection(item, "pk, profile.name, tags[1], str0")));
    }

    private static Func<int> RunProjection(Dictionary<string, JsonElement> item, string expression)
    {
        var projection = ProjectionExpressionParser.Parse(expression, names: null);
        return () => projection.Apply(item).Count;
    }

    private static Dictionary<string, JsonElement> ParseItem(string json)
    {
        // Intentionally not disposed: the extracted JsonElements alias this
        // document and must outlive the closure that captures them.
        var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = prop.Value;
        }
        return dict;
    }

    /// <summary>
    /// A representative DynamoDB item with a wide top-level attribute set plus a
    /// nested map and list, so the projection scenarios exercise whole-attribute,
    /// map-member and list-index pruning against realistic sibling counts.
    /// </summary>
    private static string BuildProjectionItem()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"pk\":{\"S\":\"partition-0001\"},");
        sb.Append("\"sk\":{\"S\":\"sort-0001\"},");

        for (int i = 0; i < 10; i++)
        {
            sb.Append("\"str").Append(i.ToString(inv)).Append("\":{\"S\":\"value-")
                .Append(i.ToString(inv)).Append("\"},");
        }

        for (int i = 0; i < 10; i++)
        {
            sb.Append("\"num").Append(i.ToString(inv)).Append("\":{\"N\":\"")
                .Append((i * 31 + 7).ToString(inv)).Append("\"},");
        }

        sb.Append("\"profile\":{\"M\":{")
            .Append("\"name\":{\"S\":\"Ada Lovelace\"},")
            .Append("\"age\":{\"N\":\"37\"},")
            .Append("\"email\":{\"S\":\"ada@example.com\"},")
            .Append("\"active\":{\"BOOL\":true},")
            .Append("\"score\":{\"N\":\"91\"},")
            .Append("\"bio\":{\"S\":\"analyst and first programmer\"}")
            .Append("}},");

        sb.Append("\"tags\":{\"L\":[")
            .Append("{\"S\":\"t0\"},{\"S\":\"t1\"},{\"S\":\"t2\"},")
            .Append("{\"S\":\"t3\"},{\"S\":\"t4\"},{\"S\":\"t5\"}")
            .Append("]}");

        sb.Append('}');
        return sb.ToString();
    }

    // ---------------- production translation ops ----------------------

    private static int DecodeFused(byte[] cosmosBinary)
    {
        using var scratch = new PooledByteBufferWriter(1024);
        using (var writer = new Utf8JsonWriter(scratch))
        {
            var reader = new CosmosBinaryReader(cosmosBinary);
            try
            {
                InferredAttributeStorage.WriteGetItemEnvelope(writer, ref reader);
            }
            finally
            {
                reader.Dispose();
            }

            writer.Flush();
        }

        return scratch.WrittenMemory.Length;
    }

    private static int DecodeText(byte[] cosmosTextUtf8)
    {
        using var scratch = new PooledByteBufferWriter(1024);
        using (var writer = new Utf8JsonWriter(scratch))
        {
            InferredAttributeStorage.WriteGetItemEnvelope(writer, cosmosTextUtf8);
            writer.Flush();
        }

        return scratch.WrittenMemory.Length;
    }

    private static int EncodeTextWire(byte[] itemUtf8)
    {
        using var bw = new PooledByteBufferWriter(Math.Max(1024, itemUtf8.Length * 2));
        InferredAttributeStorage.WriteCosmosDocument(bw, Id, Pk, itemUtf8);
        return bw.WrittenMemory.Length;
    }

    private static int EncodeBinaryWire(byte[] itemUtf8)
    {
        using var writer = InferredAttributeStorage.WriteCosmosDocumentBinary(Id, Pk, itemUtf8);
        return writer.WrittenMemory.Length;
    }

    // ---------------- measurement ----------------------
    //
    // Mirrors CosmosFusedEnvelopeAllocTests: warm the JIT + ArrayPool buckets,
    // then take the MINIMUM bytes/op across rounds so a sporadic test-infra
    // allocation on the measuring thread cannot inflate the figure.

    private const int Warmup = 500;
    private const int Iterations = 20_000;
    private const int Rounds = 3;

    public static double MeasureMinBytesPerOp(Func<int> op)
    {
        for (int i = 0; i < Warmup; i++)
        {
            _ = op();
        }

        double best = double.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
            {
                _ = op();
            }

            double perOp = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Iterations;
            if (perOp < best)
            {
                best = perOp;
            }
        }

        return best;
    }

    // ---------------- synthetic input ----------------------

    /// <summary>
    /// Builds a synthetic DynamoDB item (typed AttributeValue wire form) shaped
    /// like the BenchmarkDotNet <c>SyntheticDdbItem</c> fixture: a sort-key
    /// string, configurable S/N attributes, an optional large payload, and a
    /// fixed BOOL/NULL/M/L tail.
    /// </summary>
    private static string BuildDdbItem(int stringAttrs, int numberAttrs, int payloadBytes)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append('{');

        sb.Append("\"sk\":{\"S\":\"s0001\"},");
        if (payloadBytes > 0)
        {
            sb.Append("\"payload\":{\"S\":\"").Append('x', payloadBytes).Append("\"},");
        }

        for (int i = 0; i < stringAttrs; i++)
        {
            sb.Append("\"str").Append(i.ToString(inv)).Append("\":{\"S\":\"v")
                .Append(i.ToString(inv)).Append("\"},");
        }

        for (int i = 0; i < numberAttrs; i++)
        {
            sb.Append("\"num").Append(i.ToString(inv)).Append("\":{\"N\":\"")
                .Append((i * 31 + 7).ToString(inv)).Append("\"},");
        }

        sb.Append("\"active\":{\"BOOL\":true},");
        sb.Append("\"deleted\":{\"NULL\":true},");
        sb.Append("\"nested\":{\"M\":{\"a\":{\"S\":\"x\"},\"b\":{\"N\":\"7\"},\"c\":{\"BOOL\":false}}},");
        sb.Append("\"tags\":{\"L\":[{\"S\":\"x\"},{\"S\":\"y\"},{\"N\":\"3\"}]}");

        sb.Append('}');
        return sb.ToString();
    }
}

/// <summary>
/// Committed alloc/op ceilings loaded from <c>docs/perf/microbench-reference.json</c>.
/// Mirrors <c>Aws2Azure.FootprintTests.FootprintReferenceBaseline</c> /
/// <c>Aws2Azure.PerfTests.PerfReferenceBaseline</c>: a scenario absent from the
/// JSON is treated as not gated, and a ceiling of <c>0</c> opts that scenario
/// out (a coverage drift guard separately requires every gate scenario to carry
/// an entry, so an absent ceiling fails loudly rather than silently passing).
/// </summary>
internal static class MicrobenchAllocReference
{
    private static readonly Lazy<MicrobenchReferenceDocument?> Doc = new(LoadOrNull);

    public static MicrobenchReferenceEntry? TryGet(string scenario)
    {
        var doc = Doc.Value;
        if (doc?.Scenarios is null)
        {
            return null;
        }

        return doc.Scenarios.TryGetValue(scenario, out var entry) ? entry : null;
    }

    public static IReadOnlyDictionary<string, MicrobenchReferenceEntry> LoadAll()
    {
        var doc = Doc.Value;
        return doc?.Scenarios ?? new Dictionary<string, MicrobenchReferenceEntry>();
    }

    private static MicrobenchReferenceDocument? LoadOrNull()
    {
        var path = ReferencePath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        var options = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
        };

        return JsonSerializer.Deserialize<MicrobenchReferenceDocument>(File.ReadAllText(path), options);
    }

    public static string ReferencePath()
    {
        var overrideDir = Environment.GetEnvironmentVariable("AWS2AZURE_PERF_DIR");
        if (!string.IsNullOrEmpty(overrideDir))
        {
            // AWS2AZURE_PERF_DIR redirects where run RESULTS are written and may
            // also carry a synthetic reference to inject thresholds — honor it
            // when present. But the reference is COMMITTED config, not a run
            // output (the real-Azure perf workflow points this at a results-only
            // temp dir), so fall back to the repo's committed reference when the
            // override dir has none, rather than failing the always-on gate.
            // Mirrors Aws2Azure.PerfTests.PerfReferenceBaseline.GetReferencePath.
            var overridePath = Path.Combine(overrideDir, "microbench-reference.json");
            if (File.Exists(overridePath))
            {
                return overridePath;
            }
        }

        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "docs")) && Directory.Exists(Path.Combine(dir, "src")))
            {
                return Path.Combine(dir, "docs", "perf", "microbench-reference.json");
            }

            dir = Path.GetDirectoryName(dir);
        }

        return string.Empty;
    }
}

internal sealed class MicrobenchReferenceDocument
{
    [JsonPropertyName("scenarios")]
    public Dictionary<string, MicrobenchReferenceEntry>? Scenarios { get; set; }
}

internal sealed class MicrobenchReferenceEntry
{
    /// <summary>Per-op managed allocation ceiling in bytes. 0 opts out.</summary>
    [JsonPropertyName("maxAllocBytesPerOp")]
    public double MaxAllocBytesPerOp { get; set; }
}
