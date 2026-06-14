# DynamoDB→Cosmos response decode: text JSON vs CosmosBinary

On-demand [BenchmarkDotNet](https://benchmarkdotnet.org/) micro-benchmark
(`tests/Aws2Azure.Benchmarks`) quantifying the CPU + allocation cost of the two
DynamoDB→Cosmos response decode models introduced in #268. Issue: #317.

This is a **diagnostic**, not a CI gate — the closed-loop `Aws2Azure.PerfTests`
suite plus `AssertNoRegression()` remain the regression guard. Run it on demand
when changing the decoder or weighing the CosmosBinary tradeoff.

## The two models

- **Text model** (`TextParse`, baseline) — Cosmos returns text JSON; the proxy
  parses it (`JsonDocument.Parse`) for the response transform.
- **CosmosBinary model** (`BinaryDecodeThenParse`) — Cosmos returns a `0x80`
  binary body; the proxy decodes it to JSON (`CosmosBinaryDecoder.Decode`) and
  then parses it. This is the full work on the binary path.
- `BinaryDecodeOnly` isolates just the extra decode step.

CosmosBinary trades extra decode CPU for a smaller wire payload from Cosmos.

## How to run

```bash
# CPU + allocation benchmark (all shapes)
dotnet run -c Release --project tests/Aws2Azure.Benchmarks -- --filter '*CosmosBinaryDecode*'

# wire-size comparison only (fast, no benchmarking)
dotnet run -c Release --project tests/Aws2Azure.Benchmarks -- --sizes
```

BenchmarkDotNet writes its reports under `BenchmarkDotNet.Artifacts/`
(git-ignored). Pass `--artifacts <dir>` to redirect.

> BenchmarkDotNet is reflection-based and **AOT-incompatible by design**. The
> benchmark project opts out of the repo-wide AOT/trim analysis and is never
> referenced by shipped assemblies — it is a dev-only tool.

## Captured results

Environment: `BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04, AMD EPYC 7763,
.NET 10.0.5, X64 RyuJIT AVX2, DefaultJob`. Numbers are machine- and
input-bound; re-run locally before drawing conclusions for another host.

### CPU + allocation (per decoded query page)

| Method                | Page            |       Mean | Ratio | Allocated |
|-----------------------|-----------------|-----------:|------:|----------:|
| TextParse             | small_10x128    |   3.181 µs |  1.00 |      72 B |
| BinaryDecodeOnly      | small_10x128    |   3.726 µs |  1.17 |     168 B |
| BinaryDecodeThenParse | small_10x128    |   6.871 µs |  2.16 |     240 B |
| TextParse             | medium_50x512   |  14.655 µs |  1.00 |      72 B |
| BinaryDecodeOnly      | medium_50x512   |  19.285 µs |  1.32 |     168 B |
| BinaryDecodeThenParse | medium_50x512   |  33.831 µs |  2.31 |     240 B |
| TextParse             | large_200x1024  |  58.990 µs |  1.00 |      72 B |
| BinaryDecodeOnly      | large_200x1024  |  81.868 µs |  1.39 |     168 B |
| BinaryDecodeThenParse | large_200x1024  | 142.290 µs |  2.41 |     240 B |
| TextParse             | wide_50x24attrs |  64.272 µs |  1.00 |      72 B |
| BinaryDecodeOnly      | wide_50x24attrs |  67.565 µs |  1.05 |     168 B |
| BinaryDecodeThenParse | wide_50x24attrs | 132.031 µs |  2.05 |     240 B |

### Wire size (Cosmos → proxy payload)

| Page            |   Text B |  Binary B | Binary/Text | Saving |
|-----------------|---------:|----------:|------------:|-------:|
| small_10x128    |     2086 |      1928 |       92.4% |   7.6% |
| medium_50x512   |    29486 |     28748 |       97.5% |   2.5% |
| large_200x1024  |   220337 |    217256 |       98.6% |   1.4% |
| wide_50x24attrs |    18671 |     16498 |       88.4% |  11.6% |

## Takeaways

- **The binary path roughly doubles per-response decode CPU** (`BinaryDecodeThenParse`
  ≈ 2.0–2.4× `TextParse`): the proxy still parses the JSON after decoding, so the
  decode is pure additive CPU. The decode step alone (`BinaryDecodeOnly`) costs
  about as much as a full text parse (1.05–1.39×).
- **Both paths are allocation-light** (72–240 B/op, no Gen0+ collections): work is
  done over pooled `ArrayPool` buffers, so the cost is CPU, not GC pressure. This
  matters for the sidecar budget — CosmosBinary does not add heap churn.
- **Wire savings on these shapes are modest** (1.4–11.6%) and are largest on
  structure-heavy items (`wide_50x24attrs`), smallest on large-string-heavy items
  where the string bytes are identical in both encodings.
- **Net:** for a CPU-sensitive sidecar, CosmosBinary is a poor trade unless the
  deployment is network-bound (e.g. cross-region Cosmos, large pages) where the
  wire saving outweighs ~2× decode CPU. This supports keeping it **opt-in /
  default-off** (#268).

## Caveats

- The binary input is produced by a **simplified test encoder**
  (`CosmosBinaryTestEncoder`), not real Cosmos serialization. Real Cosmos may
  pack values more tightly (user-string dictionary, system-string tokens), so the
  **wire-size savings here are a lower bound** and not authoritative.
- The decode CPU is measured against the **real production decoder**
  (`CosmosBinaryDecoder`), but over synthetic input — representative of shape, not
  of any specific real workload.
- Emulator-unverified, same as #268: the Cosmos DB Linux emulator used by CI does
  not emit CosmosBinary bodies, so this path has no live-emulator coverage.
