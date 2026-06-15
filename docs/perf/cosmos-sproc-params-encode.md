# Single-pass Cosmos sproc write-body encode (#345)

## Problem

The conditional / atomic write paths go through Cosmos **stored procedures**,
and their request bodies were assembled with repeated **`bytes → string →
bytes`** round-trips before being sent. Sproc parameters are inherently text
JSON — the Cosmos sproc API takes the parameter list as JSON and CosmosBinary
(`0x80`) does **not** apply to stored-procedure input — so this is purely about
removing the materialization waste, not a format change.

- **Conditional PutItem / UpdateItem (single-write sproc):** the Cosmos
  document (PutItem) / key attributes (UpdateItem) were built as a **`string`**,
  embedded into the parameter array by `SprocManager.BuildParamsJson` (another
  `string`), then re-encoded by `new StringContent(...)`. Three encodes for one
  body.
- **TransactWriteItems:** `BuildOperationsJson` wrote the operations array via
  `Utf8JsonWriter` → `MemoryStream.ToArray()` → `Encoding.UTF8.GetString(...)`,
  the result was string-concatenated `"[" + ops + "]"`, then re-encoded by
  `StringContent`.

## Change

- `SprocManager.ExecuteAsync` now assembles the parameter list
  `[op, docId, payload, conditionAst, updateAst]` straight into a **pooled
  UTF-8 buffer** (`PooledByteBufferWriter`) via `WriteSingleWriteParams` and
  sends it **zero-copy** through the existing
  `CosmosClient.SendAsync(..., ReadOnlyMemory<byte>, "application/json")`
  overload. The document `payload` and the already-serialized condition / update
  ASTs are spliced as **raw bytes** — no `string` round-trip, no `StringContent`
  re-encode.
- `SprocManager.ExecuteTransactAsync` now takes the fully-assembled parameter
  body as `ReadOnlyMemory<byte>`. `TransactWriteItemsHandler.BuildTransactParamsBody`
  writes `[ [ …ops… ] ]` in a **single `Utf8JsonWriter` pass** over a pooled
  buffer (one nested array instead of `"[" + s + "]"`), splicing each Put's
  pre-encoded document and each condition AST via `WriteRawValue`.
- The conditional PutItem path builds its document via
  `ItemDocumentBody.CreateText`, reusing the #342 single-pass wire encoder
  (straight from the request's UTF-8 item bytes when recoverable). UpdateItem's
  fixed-shape key attributes are encoded once into a pooled buffer. Sproc bodies
  are always text (the #336 sproc/Transact text restriction stands), so the
  binary encoder is never engaged here, and the write-body-format metric (which
  tracks only standalone-document writes) is intentionally not emitted for sproc
  params.

Byte-identity is guaranteed and verified: `SprocParamsEncodingTests` asserts the
single-pass single-write params equal `UTF8(BuildParamsJson(...))` and the
transact params equal `UTF8("[" + BuildOperationsJson(...) + "]")` across PUT /
UPDATE / DELETE, escaped-`docId`, and empty-ops corpora. `WriteSingleWriteParams`
escapes `docId` with the same minimal (`\`, `"`) rule the legacy
`EscapeJsonString` used, at the UTF-8 byte level.

## Benchmark

`SprocParamsEncodeBenchmarks` (in `tests/Aws2Azure.Benchmarks`) compares each
legacy round-trip arm against its single-pass replacement. Its `[GlobalSetup]`
asserts both single-pass arms are byte-identical to their legacy counterparts,
so the deltas describe a *correct* encoder. This is an in-process CPU/alloc
micro-benchmark — **Azure-independent** (no Cosmos round trip), so the
emulator-bound caveat does not apply.

Representative `--job short` pass (AMD EPYC 7763, .NET 10; `--job short` timings
are noisy, the **allocation** deltas are the robust signal):

| Doc          | Arm                   | Mean      | Alloc (base → new) | Alloc ratio |
|--------------|-----------------------|----------:|-------------------:|------------:|
| lean         | SingleWriteLegacy     |  5.8 us   |   4408 B           |   1.00      |
| lean         | SingleWriteSinglePass |  4.3 us   |   1752 B           |   0.40      |
| lean         | TransactLegacy        |  6.3 us   |   8920 B           |   2.02      |
| lean         | TransactSinglePass    |  1.8 us   |    168 B           |   0.04      |
| payload_512  | SingleWriteLegacy     | 12.7 us   |  15768 B           |   1.00      |
| payload_512  | SingleWriteSinglePass |  5.7 us   |   2944 B           |   0.19      |
| payload_512  | TransactLegacy        | 10.9 us   |  16096 B           |   1.02      |
| payload_512  | TransactSinglePass    |  1.9 us   |    168 B           |   0.01      |
| wide_20s_20n | SingleWriteLegacy     | 25.4 us   |  19752 B           |   1.00      |
| wide_20s_20n | SingleWriteSinglePass | 28.4 us   |  12432 B           |   0.63      |
| wide_20s_20n | TransactLegacy        | 15.0 us   |  15560 B           |   0.79      |
| wide_20s_20n | TransactSinglePass    |  6.9 us   |    168 B           |   0.01      |

(Ratios are relative to the `SingleWriteLegacy` baseline per doc group.)

The single-write path drops **60–81 %** of its allocation; the transact path
collapses to a **flat ~168 B** pooled rental regardless of payload size
(98–99 % reduction) since the operations array, the outer wrapper, and the
StringContent transcode are all gone. Time tracks the allocation win on the two
larger arms; the `wide` single-write time is within the `--job short` error
bars but its allocation still falls ~37 %.

## How to run

```bash
dotnet run -c Release --project tests/Aws2Azure.Benchmarks -- \
  --filter '*SprocParamsEncodeBenchmarks*' --job short
```
