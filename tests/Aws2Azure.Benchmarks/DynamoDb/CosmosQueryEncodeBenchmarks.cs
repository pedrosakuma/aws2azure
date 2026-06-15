using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Operations;
using BenchmarkDotNet.Attributes;

namespace Aws2Azure.Benchmarks.DynamoDb;

/// <summary>
/// #344 — the Cosmos SQL query-body encode for Query/Scan/BatchGetItem.
/// <para><b>StringRoundTrip</b> reproduces the pre-#344 path: encode with a
/// <see cref="Utf8JsonWriter"/> into a <see cref="MemoryStream"/>, then
/// <c>Encoding.UTF8.GetString(ms.ToArray())</c> and re-encode via
/// <see cref="StringContent"/> (the bytes → string → bytes round-trip that
/// hit the wire once per page).</para>
/// <para><b>SinglePass</b> is the shipping path:
/// <see cref="CosmosQueryBody.Build"/> straight into a pooled UTF-8 buffer,
/// sent zero-copy.</para>
/// <para>A <c>[GlobalSetup]</c> gate asserts the two encoders are byte-identical
/// so the delta describes a <i>correct</i> encoder. Emulator-independent CPU
/// micro-benchmark (no Azure round-trip).</para>
/// </summary>
[MemoryDiagnoser]
public class CosmosQueryEncodeBenchmarks
{
    public sealed record QueryCase(string Name, int KeyParams, int FilterParams)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<QueryCase> Queries =>
    [
        new("key_only", KeyParams: 1, FilterParams: 0),
        new("key_range", KeyParams: 2, FilterParams: 0),
        new("filtered_8", KeyParams: 1, FilterParams: 8),
    ];

    [ParamsSource(nameof(Queries))]
    public QueryCase Query { get; set; } = null!;

    private static readonly JsonWriterOptions WriterOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private string _sql = null!;
    private CosmosSqlParameter[] _params = null!;

    [GlobalSetup]
    public void Setup()
    {
        var sb = new StringBuilder("SELECT * FROM c WHERE c._a2a = 'item'");
        var list = new List<CosmosSqlParameter>();

        for (int i = 0; i < Query.KeyParams; i++)
        {
            sb.Append(" AND c.id >= @k").Append(i);
            list.Add(new CosmosSqlParameter("@k" + i, "2025-partition-" + i));
        }

        for (int i = 0; i < Query.FilterParams; i++)
        {
            sb.Append(" AND c[\"attr").Append(i).Append("\"] = @f").Append(i);
            using var doc = JsonDocument.Parse(i % 2 == 0 ? "\"value-" + i + "\"" : (i * 7).ToString());
            list.Add(new CosmosSqlParameter("@f" + i, doc.RootElement.Clone()));
        }

        _sql = sb.ToString();
        _params = list.ToArray();

        // Byte-identity gate: the shipping single-pass output must equal the
        // legacy string-round-trip bytes.
        byte[] legacy = LegacyBuildBytes(_sql, _params);
        using var pooled = CosmosQueryBody.Build(_sql, _params);
        ReadOnlySpan<byte> shipping = pooled.WrittenMemory.Span;
        if (!shipping.SequenceEqual(legacy))
        {
            throw new InvalidOperationException(
                "CosmosQueryEncodeBenchmarks: single-pass output diverged from the legacy string round-trip.");
        }
    }

    private static byte[] LegacyBuildBytes(string sql, IReadOnlyList<CosmosSqlParameter> parameters)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("query", sql);
            writer.WritePropertyName("parameters");
            writer.WriteStartArray();
            foreach (var p in parameters)
            {
                writer.WriteStartObject();
                writer.WriteString("name", p.Name);
                writer.WritePropertyName("value");
                p.WriteValueTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        // The pre-#344 path returned a string the caller re-encoded via
        // StringContent — model both halves of the round-trip.
        string s = Encoding.UTF8.GetString(ms.ToArray());
        return Encoding.UTF8.GetBytes(s);
    }

    [Benchmark(Baseline = true)]
    public int StringRoundTrip()
    {
        byte[] bytes = LegacyBuildBytes(_sql, _params);
        return bytes.Length;
    }

    [Benchmark]
    public int SinglePass()
    {
        using var body = CosmosQueryBody.Build(_sql, _params);
        return body.WrittenMemory.Length;
    }
}
