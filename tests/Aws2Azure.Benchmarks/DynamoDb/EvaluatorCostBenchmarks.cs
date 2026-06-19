using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Aws2Azure.Modules.DynamoDb.Internal;
using BenchmarkDotNet.Attributes;

namespace Aws2Azure.Benchmarks.DynamoDb;

/// <summary>
/// Quantifies the CPU + allocation cost of the DynamoDB expression
/// <i>evaluators</i> relative to the full per-request translation work the
/// proxy does with the IO removed. "Translation" here is everything the module
/// spends CPU on for one request: decode the Cosmos response page (reads),
/// run the evaluators, and encode the DynamoDB response / write document.
///
/// <para>Each class exposes the individual components plus a
/// <c>Full_Translation</c> end-to-end measurement so the evaluator share can be
/// computed as <c>(evaluator components) / Full_Translation</c>. All payloads
/// are emulator-independent synthetic data — pure CPU micro-benchmarks, no
/// Azure round-trip.</para>
/// </summary>
public static class EvaluatorBenchData
{
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Dictionary<string, JsonElement> ParseValues(string json)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            dict[p.Name] = p.Value.Clone();
        }
        return dict;
    }
}

/// <summary>
/// Scenario 1 — Scan with a <c>FilterExpression</c>. The evaluators are
/// <see cref="FilterPushdownVisitor.Translate"/> (once per request) and
/// <see cref="ConditionEvaluator.Evaluate"/> (the residual path, once per
/// returned item when the filter is not fully pushed into Cosmos SQL).
/// </summary>
[MemoryDiagnoser]
public class ScanFilterEvaluatorBenchmarks
{
    public sealed record PageCase(string Name, int DocumentCount, int PayloadBytes, int ExtraAttributes = 0)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<PageCase> Pages =>
    [
        new("small_10x128", DocumentCount: 10, PayloadBytes: 128),
        new("medium_50x512", DocumentCount: 50, PayloadBytes: 512),
        new("wide_50x24attrs", DocumentCount: 50, PayloadBytes: 0, ExtraAttributes: 24),
    ];

    [ParamsSource(nameof(Pages))]
    public PageCase Page { get; set; } = null!;

    // A filter that is both pushable (so Translate produces SQL) and evaluable
    // per item (so the residual ConditionEvaluator path has work to do).
    private const string FilterExpression = "n >= :min";

    private byte[] _pageBytes = null!;
    private Dictionary<string, JsonElement> _values = null!;

    [GlobalSetup]
    public void Setup()
    {
        string json = SyntheticCosmosPage.Build(Page.DocumentCount, Page.PayloadBytes, Page.ExtraAttributes);
        _pageBytes = Encoding.UTF8.GetBytes(json);
        _values = EvaluatorBenchData.ParseValues("{\":min\":{\"N\":\"5\"}}");
    }

    private List<Dictionary<string, JsonElement>> ExtractItems()
    {
        var items = new List<Dictionary<string, JsonElement>>(Page.DocumentCount);
        var reader = new Utf8JsonTokenReader(_pageBytes);
        InferredAttributeStorage.ExtractItemsFused(ref reader, items);
        return items;
    }

    [Benchmark]
    public int Decode_ExtractItems() => ExtractItems().Count;

    [Benchmark]
    public int Eval_ParseAndTranslateFilter()
    {
        var node = ConditionExpressionParser.Parse(FilterExpression, null, _values);
        var pushdown = FilterPushdownVisitor.Translate(node);
        return pushdown.Sql is null ? 0 : pushdown.Sql.Length;
    }

    [Benchmark]
    public int Eval_ResidualEvaluatePerPage()
    {
        var node = ConditionExpressionParser.Parse(FilterExpression, null, _values);
        var items = ExtractItems();
        int kept = 0;
        foreach (var item in items)
        {
            if (ConditionEvaluator.Evaluate(node, item)) kept++;
        }
        return kept;
    }

    [Benchmark]
    public int Encode_Response()
    {
        var items = ExtractItems();
        var response = new ScanResponse { Items = items, Count = items.Count, ScannedCount = items.Count };
        var bw = new ArrayBufferWriter<byte>(4096);
        using var writer = new Utf8JsonWriter(bw, EvaluatorBenchData.WriterOptions);
        JsonSerializer.Serialize(writer, response, ScanJsonContext.Default.ScanResponse);
        return bw.WrittenCount;
    }

    /// <summary>
    /// Full non-IO request translation, residual (worst-case evaluator) path:
    /// parse + translate filter, extract the page, evaluate the residual filter
    /// per item, and encode the response.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int Full_Translation()
    {
        var node = ConditionExpressionParser.Parse(FilterExpression, null, _values);
        _ = FilterPushdownVisitor.Translate(node);

        var items = ExtractItems();
        var kept = new List<Dictionary<string, JsonElement>>(items.Count);
        foreach (var item in items)
        {
            if (ConditionEvaluator.Evaluate(node, item)) kept.Add(item);
        }

        var response = new ScanResponse { Items = kept, Count = kept.Count, ScannedCount = items.Count };
        var bw = new ArrayBufferWriter<byte>(4096);
        using var writer = new Utf8JsonWriter(bw, EvaluatorBenchData.WriterOptions);
        JsonSerializer.Serialize(writer, response, ScanJsonContext.Default.ScanResponse);
        return bw.WrittenCount;
    }
}

/// <summary>
/// Scenario 2 — conditional write (PutItem with a <c>ConditionExpression</c>).
/// The evaluator is parse + <see cref="ConditionEvaluator.Evaluate"/> against
/// the existing item; the dominant non-evaluator cost is encoding the item
/// document for Cosmos.
/// </summary>
[MemoryDiagnoser]
public class ConditionalWriteEvaluatorBenchmarks
{
    private const string ConditionExpression = "attribute_exists(pk) AND n < :limit";

    private JsonElement _item;
    private Dictionary<string, JsonElement> _existing = null!;
    private Dictionary<string, JsonElement> _values = null!;
    private JsonDocument _itemDoc = null!;

    [GlobalSetup]
    public void Setup()
    {
        const string itemJson =
            "{\"pk\":{\"S\":\"p01\"},\"sk\":{\"S\":\"s0001\"},\"n\":{\"N\":\"5\"}," +
            "\"status\":{\"S\":\"active\"},\"a\":{\"N\":\"1\"},\"b\":{\"S\":\"hello\"}," +
            "\"c\":{\"BOOL\":true},\"d\":{\"S\":\"world\"}}";
        _itemDoc = JsonDocument.Parse(itemJson);
        _item = _itemDoc.RootElement;
        _existing = EvaluatorBenchData.ParseValues(itemJson);
        _values = EvaluatorBenchData.ParseValues("{\":limit\":{\"N\":\"100\"}}");
    }

    [GlobalCleanup]
    public void Cleanup() => _itemDoc.Dispose();

    [Benchmark]
    public int Eval_ParseCondition()
    {
        var node = ConditionExpressionParser.Parse(ConditionExpression, null, _values);
        return node.GetHashCode();
    }

    [Benchmark]
    public bool Eval_ParseAndEvaluateCondition()
    {
        var node = ConditionExpressionParser.Parse(ConditionExpression, null, _values);
        return ConditionEvaluator.Evaluate(node, _existing);
    }

    [Benchmark]
    public int Encode_ItemDocument()
        => ItemHandlers.BuildItemDocumentBytes("p01:s0001", "p01", _item).Length;

    /// <summary>Full non-IO write translation: parse + evaluate the condition,
    /// then encode the item document.</summary>
    [Benchmark(Baseline = true)]
    public int Full_Translation()
    {
        var node = ConditionExpressionParser.Parse(ConditionExpression, null, _values);
        _ = ConditionEvaluator.Evaluate(node, _existing);
        return ItemHandlers.BuildItemDocumentBytes("p01:s0001", "p01", _item).Length;
    }
}

/// <summary>
/// Scenario 3 — UpdateItem with an <c>UpdateExpression</c> executed in-proxy.
/// The evaluator is parse + <see cref="UpdateExecutor.Apply"/>; the
/// non-evaluator cost is re-encoding the mutated item as a Cosmos document.
/// </summary>
[MemoryDiagnoser]
public class UpdateExpressionEvaluatorBenchmarks
{
    private const string UpdateExpression = "SET n = n + :inc, status = :s REMOVE d";

    private Dictionary<string, JsonElement> _existing = null!;
    private Dictionary<string, JsonElement> _values = null!;

    [GlobalSetup]
    public void Setup()
    {
        const string itemJson =
            "{\"pk\":{\"S\":\"p01\"},\"sk\":{\"S\":\"s0001\"},\"n\":{\"N\":\"5\"}," +
            "\"status\":{\"S\":\"active\"},\"a\":{\"N\":\"1\"},\"b\":{\"S\":\"hello\"}," +
            "\"c\":{\"BOOL\":true},\"d\":{\"S\":\"world\"}}";
        _existing = EvaluatorBenchData.ParseValues(itemJson);
        _values = EvaluatorBenchData.ParseValues("{\":inc\":{\"N\":\"1\"},\":s\":{\"S\":\"done\"}}");
    }

    [Benchmark]
    public int Eval_ParseUpdate()
    {
        var ast = UpdateExpressionParser.Parse(UpdateExpression, null, _values);
        return ast.GetHashCode();
    }

    [Benchmark]
    public int Eval_ParseAndApplyUpdate()
    {
        var ast = UpdateExpressionParser.Parse(UpdateExpression, null, _values);
        var result = UpdateExecutor.Apply(ast, _existing);
        return result.NewItem.Count;
    }

    private static int EncodeNewItem(Dictionary<string, JsonElement> newItem)
    {
        var bw = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(bw, EvaluatorBenchData.WriterOptions))
        {
            w.WriteStartObject();
            foreach (var kv in newItem)
            {
                w.WritePropertyName(kv.Key);
                kv.Value.WriteTo(w);
            }
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(bw.WrittenMemory);
        return ItemHandlers.BuildItemDocumentBytes("p01:s0001", "p01", doc.RootElement).Length;
    }

    [Benchmark]
    public int Encode_NewItemDocument()
    {
        var ast = UpdateExpressionParser.Parse(UpdateExpression, null, _values);
        var result = UpdateExecutor.Apply(ast, _existing);
        return EncodeNewItem(result.NewItem);
    }

    /// <summary>Full non-IO update translation: parse + apply the update, then
    /// re-encode the mutated item document.</summary>
    [Benchmark(Baseline = true)]
    public int Full_Translation()
    {
        var ast = UpdateExpressionParser.Parse(UpdateExpression, null, _values);
        var result = UpdateExecutor.Apply(ast, _existing);
        return EncodeNewItem(result.NewItem);
    }
}
