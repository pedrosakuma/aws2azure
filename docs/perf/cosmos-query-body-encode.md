# Single-pass Cosmos query-body encode (#344)

## Problem

The Cosmos SQL query body (`{"query": ..., "parameters":[...]}`) sent for
`Query`, `Scan`, `ScannedCount`, and `BatchGetItem` was built by
`CosmosQueryBody.Build` returning a **`string`**, which the handlers then
re-encoded via `StringContent` on every page. That is a
`bytes → string → bytes` round-trip on a per-page hot path, plus each
KeyCondition / `id` parameter (always a plain string) was wrapped in a
`JsonElement` (`StringValue(string)` parsed a one-element `JsonDocument`)
just to be written back out.

## Change

- `CosmosQueryBody.Build` now writes straight into a **pooled UTF-8 buffer**
  (`PooledByteBufferWriter`, `ArrayPool`-backed) via `Utf8JsonWriter` and
  returns it; the caller `using`-disposes it after the body is sent. All five
  call sites (`QueryHandler`, `ScanHandler`, `ScannedCountQuery`,
  `BatchGetItemHandler`) send it **zero-copy** through the existing
  `CosmosClient.SendAsync(..., ReadOnlyMemory<byte>, "application/query+json")`
  overload. The buffer's `WrittenMemory` is read once per attempt and stays
  valid across pagination/retries within the enclosing `using` scope.
- `CosmosSqlParameter` gained a `string` constructor + `WriteValueTo` that
  emits a plain string straight to the writer (`WriteStringValue(string)`),
  dropping the `JsonElement` round-trip for KeyCondition / `id` values. The
  `JsonElement` constructor is retained for filter-pushdown parameters whose
  JSON kind (number / bool / null) must be preserved. A diagnostic-only
  `Value` property is kept for tests; **production must use `WriteValueTo`**.

Byte-identity is guaranteed: `Utf8JsonWriter.WriteStringValue(string)` and the
legacy `string → JsonElement → WriteTo` escape through the same encoder
(`UnsafeRelaxedJsonEscaping`), so the raw-string and element-string paths
produce identical bytes. A 7-case byte-identity unit suite
(`CosmosQueryBodyTests`) covers element-backed, string-backed, and escaped
corpora.

## Benchmark

`CosmosQueryEncodeBenchmarks` (in `tests/Aws2Azure.Benchmarks`) compares
`StringRoundTrip` (the pre-#344 `Utf8JsonWriter → MemoryStream → GetString →
GetBytes` round-trip) against `SinglePass` (`CosmosQueryBody.Build` into the
pooled buffer). Its `[GlobalSetup]` asserts the two encoders are
byte-identical, so the delta describes a *correct* encoder. This is an
in-process CPU/alloc micro-benchmark — **Azure-independent** (no Cosmos round
trip), emulator-bound caveat does not apply because no emulator is involved.

Representative `--job short` pass (AMD EPYC 7763, .NET 10):

| Query       | StringRoundTrip | SinglePass | Time ratio | Alloc (base → new) | Alloc ratio |
|-------------|----------------:|-----------:|-----------:|-------------------:|------------:|
| key_only    |     889 ns      |  311 ns    |   0.35     |  1400 B → 200 B    |    0.14     |
| key_range   |   1 137 ns      |  388 ns    |   0.34     |  1624 B → 200 B    |    0.12     |
| filtered_8  |   4 597 ns      | 1 398 ns   |   0.30     |  7736 B → 200 B    |    0.03     |

So the single-pass query body is a **65–70 % time reduction** and a
**86–97 % allocation reduction** (allocation is now a flat ~200 B regardless
of parameter count — the pooled buffer rental, returned on dispose), removing
the per-page `string` round-trip and the per-parameter `JsonElement`
materialization from the read hot path.

## How to run

```bash
dotnet run -c Release --project tests/Aws2Azure.Benchmarks -- \
  --filter '*CosmosQueryEncodeBenchmarks*' --job short
```
