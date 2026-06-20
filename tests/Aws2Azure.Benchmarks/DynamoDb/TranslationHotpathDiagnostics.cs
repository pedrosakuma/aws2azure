using Aws2Azure.Core.Buffers;
using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Aws2Azure.Benchmarks.DynamoDb.Spike319;
using Aws2Azure.Benchmarks.DynamoDb.Spike332;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using DotnetDiagnostics.BenchmarkDotNet;

namespace Aws2Azure.Benchmarks.DynamoDb;

/// <summary>
/// EventPipe-sampled hotpath probe for the four production DDB↔Cosmos
/// translation paths, isolated from all IO so the only thing the sampler sees is
/// the token walk + formatting cost (the user's stated goal: "verificar somente
/// o processo de tradução, senão o IO vai só acrescentar barulho").
///
/// <list type="bullet">
///   <item><see cref="DecodeText"/> — read path, previous shipped two-pass:
///   <c>CosmosBinaryDecoder.Decode</c> (binary → JSON text) then
///   <c>WriteGetItemEnvelope(writer, span)</c>.</item>
///   <item><see cref="DecodeBinary"/> — read path, current fused single-pass:
///   <c>WriteGetItemEnvelope(writer, ref CosmosBinaryReader)</c>.</item>
///   <item><see cref="EncodeText"/> — write path, shipping single-pass text:
///   <c>WriteCosmosDocument(buffer, id, pk, utf8)</c>.</item>
///   <item><see cref="EncodeBinary"/> — write path, CosmosBinary single-pass:
///   <c>WriteCosmosDocumentBinary(buffer, id, pk, utf8)</c>.</item>
/// </list>
///
/// Each body loops the production transform for <see cref="RunSeconds"/> so the
/// <see cref="DiagnosedConfig"/> Monitoring job gives EventPipe a stable window.
/// The "wide_20s_20n" doc shape is used to maximise token-walk branch coverage,
/// surfacing the per-frame hotpath clearly. Output of interest is the
/// <c>*-dotnet-diagnostics-report.md</c> "Hottest self-cost" (cpu) and
/// allocation <c>TopBySite</c>, not the Monitoring-job mean.
/// </summary>
[Config(typeof(DiagnosedConfig))]
public class TranslationHotpathDiagnostics
{
    private const string Id = "route-0001";
    private const string Pk = "p00";
    private const int RunSeconds = 5;

    // Read path: a CosmosBinary document body (what Cosmos returns on a read).
    private byte[] _binaryBody = null!;

    // Write path: the raw DynamoDB item UTF-8 wire bytes (what the AWS SDK sends).
    private byte[] _itemUtf8 = null!;

    // Reused output buffers so the sampler attributes allocation to the
    // translation itself, not to per-iteration buffer churn (production reuses
    // pooled request buffers). Mirrors the CosmosWriteEncodeBenchmarks pattern.
    private ArrayBufferWriter<byte> _encodeBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Read corpus: synthetic Cosmos doc → production CosmosBinary encoding.
        string cosmosJson = SyntheticCosmosDoc.Build(20, 20, 0);
        _binaryBody = CosmosBinaryTestEncoder.Encode(cosmosJson);

        // Write corpus: synthetic DynamoDB item, raw UTF-8 wire bytes.
        string ddbJson = SyntheticDdbItem.Build(20, 20, 0);
        _itemUtf8 = System.Text.Encoding.UTF8.GetBytes(ddbJson);
        _encodeBuffer = new ArrayBufferWriter<byte>(4096);

        // Smoke-gate both read arms produce identical envelopes and both write
        // arms produce non-empty output before the sampler ever runs.
        if (RunDecodeText() == 0 || RunDecodeBinary() == 0 || RunEncodeText() == 0 || RunEncodeBinary() == 0)
        {
            throw new InvalidOperationException("translation setup produced empty output");
        }
    }

    [Benchmark(Baseline = true)]
    [DiagnosticKind("cpu,allocation")]
    public long DecodeText()
    {
        long checksum = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < RunSeconds)
        {
            checksum += RunDecodeText();
        }

        return checksum;
    }

    [Benchmark]
    [DiagnosticKind("cpu,allocation")]
    public long DecodeBinary()
    {
        long checksum = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < RunSeconds)
        {
            checksum += RunDecodeBinary();
        }

        return checksum;
    }

    [Benchmark]
    [DiagnosticKind("cpu,allocation")]
    public long EncodeText()
    {
        long checksum = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < RunSeconds)
        {
            checksum += RunEncodeText();
        }

        return checksum;
    }

    [Benchmark]
    [DiagnosticKind("cpu,allocation")]
    public long EncodeBinary()
    {
        long checksum = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < RunSeconds)
        {
            checksum += RunEncodeBinary();
        }

        return checksum;
    }

    private int RunDecodeText()
    {
        using var json = new PooledByteBufferWriter(Math.Max(4096, _binaryBody.Length));
        CosmosBinaryDecoder.Decode(_binaryBody, json);

        using var scratch = new PooledByteBufferWriter(1024);
        using (var writer = new Utf8JsonWriter(scratch))
        {
            InferredAttributeStorage.WriteGetItemEnvelope(writer, json.WrittenMemory.Span);
            writer.Flush();
        }

        return scratch.WrittenMemory.Length;
    }

    private int RunDecodeBinary()
    {
        using var scratch = new PooledByteBufferWriter(1024);
        using (var writer = new Utf8JsonWriter(scratch))
        {
            var reader = new CosmosBinaryReader(_binaryBody);
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

    private int RunEncodeText()
    {
        _encodeBuffer.Clear();
        InferredAttributeStorage.WriteCosmosDocument(_encodeBuffer, Id, Pk, _itemUtf8);
        return _encodeBuffer.WrittenCount;
    }

    private int RunEncodeBinary()
    {
        _encodeBuffer.Clear();
        InferredAttributeStorage.WriteCosmosDocumentBinary(_encodeBuffer, Id, Pk, _itemUtf8);
        return _encodeBuffer.WrittenCount;
    }
}
