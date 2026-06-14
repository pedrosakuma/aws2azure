# Spike #319 — fusing CosmosBinary decode with the DynamoDB transform

Feasibility spike for issue **#319**: can the CosmosBinary GetItem path skip the
intermediate JSON-text materialization it pays today, and is it worth doing?

This extends the #317 decode diagnostic (`cosmos-binary-decode.md`). It is a
**spike / measurement prototype** living in `tests/Aws2Azure.Benchmarks`, not
shipped code. No production code changed.

## The redundancy

The shipped CosmosBinary GetItem path is **two passes**:

1. `CosmosBinaryDecoder.Decode(binary, …)` — binary → **JSON text** (via `Utf8JsonWriter`).
2. `InferredAttributeStorage.WriteGetItemEnvelope(writer, jsonTextSpan)` — re-reads
   that JSON text (via `Utf8JsonReader`) → the `{"Item":{…}}` DynamoDB envelope.

The JSON-text intermediate is pure overhead: bytes are formatted only to be
re-parsed immediately.

## The design under test: abstract the *reader*, not fuse the *logic*

Rather than hand-fuse decode+transform (which would duplicate the whole
transform), abstract the reader behind a small interface and write the transform
**once**, generic over it:

```csharp
interface ITokenReader              // the Utf8JsonReader surface the transform uses
ref struct Utf8JsonTokenReader : ITokenReader   // thin adapter over Utf8JsonReader (text)
ref struct CosmosBinaryReader  : ITokenReader   // walks the binary token stream directly

static void WriteGetItemEnvelope<TReader>(Utf8JsonWriter w, scoped ref TReader r)
    where TReader : ITokenReader, allows ref struct;   // C# 13 / .NET 9+
```

`allows ref struct` lets the JIT **monomorphize** the transform per concrete
reader and **devirtualize** the interface calls — no boxing, no reflection,
AOT-clean. The exact production transform was ported verbatim, changing only
`ref Utf8JsonReader` → `scoped ref TReader`.

Spike files: `tests/Aws2Azure.Benchmarks/DynamoDb/Spike319/`.

## Correctness gate

`[GlobalSetup]` asserts all three arms emit **byte-identical** envelopes over the
corpus (a single Cosmos GetItem doc: routing `id` / `_a2a_pk` / `_a2a`, shadow
`_a2a$id`→`id`, Cosmos system fields `_rid/_self/_etag/_ts/_attachments`, plus
S / N(int) / BOOL / NULL / nested M / L attributes). The benchmark refuses to run
if any arm diverges. Numbers below therefore describe a *correct* transform.

## Results

```
BenchmarkDotNet v0.15.2, AMD EPYC 7763, .NET SDK 10.0.201 / .NET 10.0.5, X64 RyuJIT AVX2
DefaultJob
```

| Method                     | Doc          | Mean     | Ratio | Gen0   | Allocated | Alloc Ratio |
|--------------------------- |------------- |---------:|------:|-------:|----------:|------------:|
| Production_TwoPass         | lean         | 2.708 μs |  1.00 | 0.0076 |     336 B |        1.00 |
| Generic_TextReader_TwoPass | lean         | 2.703 μs |  1.00 | 0.0076 |     336 B |        1.00 |
| Generic_BinaryReader_Fused | lean         | 1.147 μs |  0.42 | 0.0038 |     168 B |        0.50 |
| Production_TwoPass         | payload_512  | 3.071 μs |  1.00 | 0.0076 |     336 B |        1.00 |
| Generic_TextReader_TwoPass | payload_512  | 2.995 μs |  0.98 | 0.0076 |     336 B |        1.00 |
| Generic_BinaryReader_Fused | payload_512  | 1.283 μs |  0.42 | 0.0038 |     168 B |        0.50 |
| Production_TwoPass         | wide_20s_20n | 8.773 μs |  1.00 |      - |     336 B |        1.00 |
| Generic_TextReader_TwoPass | wide_20s_20n | 8.680 μs |  0.99 |      - |     336 B |        1.00 |
| Generic_BinaryReader_Fused | wide_20s_20n | 4.467 μs |  0.51 |      - |     168 B |        0.50 |

The three arms:

- **`Production_TwoPass`** (baseline) — the shipped two-pass path.
- **`Generic_TextReader_TwoPass`** — decode to JSON text (as today), then the
  **generic** transform over the `Utf8JsonReader` adapter. Isolates the cost of
  the abstraction itself on identical input.
- **`Generic_BinaryReader_Fused`** — the **proposed** path: the same generic
  transform driven straight off `CosmosBinaryReader`, no JSON text.

## Findings

1. **The abstraction is free.** `Generic_TextReader_TwoPass` is within noise of
   `Production_TwoPass` (ratio 0.98–1.00, identical allocations). The
   `allows ref struct` monomorphization devirtualizes the interface cleanly —
   making the transform generic costs nothing.
2. **The fused binary path is ~2× faster and allocates half.** 0.42–0.51× CPU
   (≈2.0–2.4× faster, ~50–58% less wall time) and 168 B vs 336 B per op (the
   JSON-text intermediate buffer is gone), with half the Gen0.
3. **No transform duplication.** The single generic transform serves both text
   and binary, so the binary path adds a reader, not a second copy of the
   envelope/attribute mapping. It also generalizes to every reader-based
   transform (Query, Scan), not just GetItem.

## Recommendation: **GO** (with a scoped production port)

The reader-abstraction is the right shape: it removes the redundant pass, ~halves
binary-path CPU and allocation, costs nothing in the abstraction, keeps a single
source of truth, and is AOT-friendly. Recommended follow-up work (separate
issue/PR, behind the existing CosmosBinary opt-in):

- Refactor the production `InferredAttributeStorage` reader-based transforms to
  generic over `ITokenReader`; pin byte-identity with the existing golden corpus.
- Production `CosmosBinaryReader` must cover the **full** marker surface the
  shipped `CosmosBinaryDecoder` handles (this spike covers only the corpus
  markers: inline + 2-byte strings, int32/double scalars, bool, null,
  LC1/LC2/LC4 containers).
- **Double canonicalization:** the spike corpus is integer-only. Doubles must be
  formatted with the same shortest-round-trip algorithm `Utf8JsonWriter` uses to
  stay byte-identical; verify against the golden corpus.
- Bonus: binary `Skip()` is O(1) via the container length prefix (vs the re-scan
  `Utf8JsonReader.Skip` performs) — extra upside on Skip-heavy reserved-field
  filtering not captured by these micro-numbers.

## How to run

```bash
dotnet run -c Release --project tests/Aws2Azure.Benchmarks -- --filter '*FusedDecodeSpike*'
```

Numbers are micro-benchmark, in-process, emulator-independent CPU/allocation
figures — not throughput against real Azure.
