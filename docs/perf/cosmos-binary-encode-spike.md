# Spike #332 — CosmosBinary **encode** direction (DDB→Cosmos request bodies)

Feasibility spike for issue **#332**: can we send DynamoDB→Cosmos **request
bodies** as CosmosBinary (the `0x80` format), symmetric to the shipped decode
rollout (#321, #327–#331)? Decide **go/no-go before any implementation** — same
posture as the rntbd transport study (#265), because both the payoff and the
server-side support were unverified.

This is a **spike**. No production code changed. A throwaway binary *encoder*
was built only as a benchmark fixture (`tests/Aws2Azure.Benchmarks`, never
shipped). The deliverable is this write-up, including a server-side support probe
**executed against real Azure** (§4) and an encode micro-benchmark (§2).

## Decision: **GO**

Both open questions are now **resolved with data**, and both came back in
binary's favour:

- The gateway **does accept binary request bodies** — confirmed against real
  Azure: a raw `0x80` upsert returned `201` and an indexed query proved the
  server *parsed* it (§4).
- Binary formatting **is materially cheaper** than text — **~2.4× faster** on the
  encode step, at equal (zero) allocation (measured, §2; no string escaping, no
  number→ASCII).

**Decision (owner): GO.** A consistent **~2.4× CPU reduction on a write-path hot
loop** is squarely the kind of win the sidecar-first premise exists to capture —
CPU footprint is a first-class budget for this project (see the locked
"Resource footprint" decision), and the format step runs on every PutItem /
UpdateItem / BatchWrite / TransactWrite. The cost is borne by the proxy on every
write; halving it is worth pursuing.

The spike's earlier caveats are **not blockers — they become implementation
guardrails**:

1. **Server contract is undocumented / off-by-default in the SDK (§3).** Ship the
   binary request body **opt-in**, mirroring the decode rollout's
   `DynamoDb.CosmosBinaryResponses` flag (a new `DynamoDb.CosmosBinaryRequests`,
   default `false`), with transparent text fallback. Topology/feature-dependent
   behavior ships opt-in + documented per scenario — the project's standing rule.
2. **Correctness surface is large.** A `CosmosBinaryWriter` must cover the entire
   marker set + double shortest-round-trip canonicalization, gated by a
   **byte-identity golden corpus** and a `decode(encode(x)) == text(x)` round-trip
   test (the §2 benchmark already gates on this). Build it behind a shared
   **`ITokenWriter`** abstraction with **two implementations over the same token
   walk** — a `Utf8JsonWriter`-backed text writer and the `CosmosBinaryWriter` —
   so text and binary stay symmetric (no `JsonElement.WriteTo` path; that arm is
   benchmark-reference only, and is slower — §2).
3. **Validate against real Azure before flipping `status: implemented`.** Emulators
   neither emit nor reliably accept CosmosBinary; acceptance must be re-confirmed
   on real Azure in CI/nightly (the §4 probe pattern), and any divergence recorded
   in the gap-doc `behavior_differences`.
4. **Do the text zero-copy tail *first* (§5), as a prerequisite.** A binary body
   still flows through the `GetString` → `StringContent` round-trip (the only real
   allocation on the path, 608–1656 B/write) unless that tail is fixed. Fixing it
   is text-only, zero server-contract risk, and is required for the binary path to
   realize its CPU win without re-introducing an allocation. Land it as the first
   step of the GO.

Sequencing: **(1)** text zero-copy tail (§5), keeping the `Utf8JsonWriter`
token-walk → **(2)** shared `ITokenWriter` + `CosmosBinaryWriter` + golden corpus
(text writer = `Utf8JsonWriter`-backed, symmetric) → **(3)** opt-in
`CosmosBinaryRequests` flag + text fallback → **(4)** real-Azure acceptance test
+ gap-doc update.

---

## 1. The asymmetry with the decode win

The decode direction paid off because the shipped read path was **two passes**
with a redundant JSON-text intermediate (see
[`cosmos-binary-fused-decode-spike.md`](cosmos-binary-fused-decode-spike.md)):

```
Cosmos binary (0x80)  ──decode──▶  JSON text  ──re-parse──▶  DDB envelope
                                   ▲ pure overhead: formatted only to be reparsed
```

Fusing removed the middle pass → **~2× CPU, ~50% allocation** on multi-item
response pages.

The **encode** path has **no analogous redundancy**. It is already a single
transform pass: the DDB `AttributeValue` map is written straight to the Cosmos
document as UTF-8 JSON by one `Utf8JsonWriter`:

```
DDB AttributeValue map  ──Utf8JsonWriter (single pass)──▶  Cosmos JSON text body
```

Source of truth: `InferredAttributeStorage.BuildCosmosDocument`
(`src/Aws2Azure.Modules.DynamoDb/Persistence/InferredAttributeStorage.cs:197-238`)
— one `ArrayBufferWriter<byte>` + `Utf8JsonWriter`, no intermediate
materialization of the transform. Callers wrap the bytes in `StringContent`
(`ItemHandlers.cs:161,285`, `BatchWriteItemHandler.cs:269`,
`UpdateItemHandler.cs:321,337`, `TransactWriteItemsHandler.cs:269`); queries use
`application/query+json` text (`QueryHandler.cs:237-238`, `ScanHandler.cs:218`,
`BatchGetItemHandler.cs:443`).

**Going binary would not remove a pass — it would only swap the formatter** the
single pass uses (text → binary). So the entire payoff reduces to "is producing
a binary token stream cheaper than producing JSON text?" — measured in §2.

## 2. Measured: binary formatting is ~2.4× cheaper than text (per-write CPU)

To answer the formatter question with data rather than assertion (the same way
the decode side was settled in #319), a micro-benchmark encodes the same Cosmos
document **three corpus shapes, four ways**. The document is pre-materialized
**once in `[GlobalSetup]` into a UTF-8 token tree** (`Node`: property names and
string values decoded to `byte[]` up front), so both encoders walk it **zero
allocation** and the numbers reflect *pure formatter cost* — escaping, number
formatting, structural framing — not buffer churn or string materialization. A
correctness gate round-trips the binary output back through the **production**
`CosmosBinaryDecoder` and asserts it equals the text output, so the numbers
describe a *correct* encoder.

> This is the corrected second cut of the benchmark. The first cut drove both
> encoders off a `JsonElement`, forcing a `GetString()` materialization per
> property in **both** arms — a shared tax that dominated timing, diluted the
> text-vs-binary delta to a misleading "~10–15%", and made `JsonElement.WriteTo`
> (which copies raw UTF-8) look ~2× faster. Pre-materializing the token tree
> removes that confound.

Spike fixtures: `tests/Aws2Azure.Benchmarks/DynamoDb/Spike332/`
(`EncodeTokenTree.cs` — the `Node` tree plus `TextTokenEncoder` and a realistic
single-buffer backpatching `BinaryTokenEncoder`; `CosmosEncodeBenchmarks.cs`).

```
BenchmarkDotNet v0.15.2, AMD EPYC 7763, .NET 10.0.5, X64 RyuJIT AVX2, DefaultJob
```

| Method                     | Doc          |     Mean | Ratio |  Allocated |
|--------------------------- |------------- |---------:|------:|-----------:|
| Text_TokenWalk (baseline)  | lean         |   747 ns |  1.00 |        0 B |
| **Binary_TokenWalk**       | lean         |   312 ns |  **0.42** |    0 B |
| Text_WriteTo               | lean         |   980 ns |  1.31 |        0 B |
| Text_TokenWalk_ToString    | lean         | 1,141 ns |  1.53 |      608 B |
| Text_TokenWalk (baseline)  | payload_512  |   813 ns |  1.00 |        0 B |
| **Binary_TokenWalk**       | payload_512  |   330 ns |  **0.41** |    0 B |
| Text_WriteTo               | payload_512  |   991 ns |  1.22 |        0 B |
| Text_TokenWalk_ToString    | payload_512  | 1,844 ns |  2.27 |    1,656 B |
| Text_TokenWalk (baseline)  | wide_20s_20n | 2,108 ns |  1.00 |        0 B |
| **Binary_TokenWalk**       | wide_20s_20n |   871 ns |  **0.41** |    0 B |
| Text_WriteTo               | wide_20s_20n | 2,558 ns |  1.21 |        0 B |
| Text_TokenWalk_ToString    | wide_20s_20n | 2,944 ns |  1.40 |    1,576 B |

Reading the arms:

- **`Binary_TokenWalk` vs `Text_TokenWalk`** (apples-to-apples — identical
  zero-alloc token-tree walk, only the emit format differs): binary is **~2.4×
  faster** (ratio **0.41–0.42**), consistent across all three shapes, at **equal
  (zero) allocation**. Binary token formatting genuinely *is* cheaper — it skips
  string escaping and number→ASCII formatting (the expensive parts of text),
  paying only a fixed length-prefix backpatch per container. So "binary is more
  expensive to produce" was **wrong**, *and so was the first cut's "~10–15%"* —
  that figure was diluted by a per-property `GetString()` tax shared by both arms;
  removing it reveals the real ~2.4×, symmetric to the decode read win.
- **`Text_WriteTo` is not a shortcut — it is ~1.2–1.3× *slower*** than the
  zero-materialization token walk, so it is **a benchmark reference only, not the
  implementation route**. (This also corrects the first cut, where `WriteTo`
  looked ~2× faster: that was only relative to a *materializing* manual walk.
  Against an optimal text writer, `JsonElement.WriteTo`'s element-indirection
  overhead makes it lose.) The implementation keeps `Utf8JsonWriter` for the text
  path — the `Text_TokenWalk` arm — which is both faster *and* gives **structural
  symmetry** with the binary path: both walk the *same* token tree through a
  shared `ITokenWriter`, differing only in the writer implementation
  (`Utf8JsonWriter`-backed text vs `CosmosBinaryWriter`). See §5 / the GO
  sequencing.
- **`Text_TokenWalk_ToString`** isolates the production tail: the
  `Encoding.UTF8.GetString` that `BuildCosmosDocument` pays before `StringContent`
  adds +400 ns…+1.1 µs **and the only real allocation on the path** (608–1656 B).
  That round-trip — *not* text-vs-binary — is the write path's actual
  inefficiency (§5).

So binary buys a **real ~2.4×** on the format step. In wall-clock per request
that is ~0.4–1.2 µs against a millisecond-scale round-trip, so it does **not**
move end-to-end latency; its value is **proxy CPU** — the format loop runs on
every write the sidecar handles, and CPU footprint is a first-class sidecar
budget. Both arms are now zero-alloc, so the win is *pure CPU*, with no
allocation, network, or RU component (see also below). The GO weighs that
per-write CPU reduction as worthwhile; the absolute-latency framing is why it is
a CPU/efficiency play, not a latency one.

**No network or RU upside** (so the win is CPU-only, as above). Binary is not
smaller than text for the small docs we write — the system-string dictionary +
absolute string-reference offsets add fixed overhead (the round-tripped 4-field
probe doc was 134 bytes binary). And Cosmos RU charge is a function of document
size and indexing, not request wire-encoding, so binary requests do not reduce
cost. The benefit is confined to CPU on both ends (proxy formatter + Cosmos
parser); the proxy half is the part worth capturing for a sidecar.

Capturing the ~2.4× requires building `ITokenWriter` + a full
`CosmosBinaryWriter` (the writer analog of `ITokenReader`/`CosmosBinaryReader`,
covering the *entire* marker surface + double shortest-round-trip
canonicalization + a byte-identity golden corpus) and wiring it onto the
(undocumented, opt-in) server feature (§3). That is real work, but the decision
owner accepted it for the per-write CPU win — see the GO decision and sequencing
at the top of this document; the golden-corpus + round-trip gate this benchmark
already enforces is the safety net.

## 3. Server-side support: CONFIRMED (against real Azure), but undocumented

The open question — *does the Cosmos REST/gateway accept a `0x80` request
body?* — is **answered yes**, both by the open-source SDK and by a live probe
(§4).

**Live confirmation (real Azure, §4):** an authenticated gateway probe against a
real Cosmos SQL-API serverless account upserted a raw `0x80` binary body and got
**`201 Created`**; a subsequent text read and an indexed query
(`WHERE c.marker=…`) both returned the document, proving the gateway **parsed
and indexed** the binary body rather than storing it opaquely. It worked with
**no Content-Type and no negotiation header** (the server auto-detects the
`0x80` marker), and equally with `application/json`, `application/cosmos-binary`,
and PUT/replace.

**SDK corroboration** (`Azure/azure-cosmos-dotnet-v3`):

- The SDK converts the request body to `JsonSerializationFormat.Binary` in
  `ContainerCore.ProcessItemStreamAsync` with **no Gateway-vs-Direct branch**,
  and sends it as the raw HTTP body via `GatewayStoreClient`. A source comment
  *excludes* binary only when triggers are present ("the backend will pass the
  stream to the javascript engine, which does not support binary encoded
  content") — i.e. the main gateway path **does** accept binary bodies.
- **But it is internal and off by default.** Gated behind the environment
  variable `AZURE_COSMOS_BINARY_ENCODING_ENABLED` (default `false`), with a
  source comment that it "will eventually be removed once binary encoding is
  enabled by default." A **staged internal rollout**, not a public feature.
- **Not in the public contract.** The official REST reference documents
  `Content-Type: application/json` for write bodies and never mentions a binary
  request format or MIME type. Nothing about the `0x80`-body behavior is
  contractually guaranteed; it can change without notice.
- **The negotiation header is response-only.** Our existing
  `x-ms-cosmos-supported-serialization-formats: CosmosBinary`
  (`CosmosClient.cs:38-39,313-318`) controls the **response** format only — it
  is documented and coded that way (`CosmosSerializationFormatOptions`). It does
  *not* declare a binary request body (and the live probe confirms none is
  needed).

So acceptance is no longer a blocker. The remaining consideration — for a
**sidecar** whose value proposition is correctness and stability across "diverse
deployment scenarios" — is that the server behavior is **undocumented,
off-by-default-in-the-SDK, and may change without notice**. The GO decision
therefore ships it **opt-in** (`DynamoDb.CosmosBinaryRequests`, default `false`)
with transparent text fallback and a real-Azure acceptance test, exactly as the
decode rollout treated its own negotiation flag. That makes the undocumented
contract a configurable, monitored opt-in rather than a default dependency — the
project's standard way to ship topology/feature-dependent behavior.

## 4. Server-side probe — executed against real Azure

The emulator cannot answer this (it neither emits nor reliably accepts
CosmosBinary). The probe confirms gateway acceptance **without building an
encoder**, by round-tripping a *known-valid* binary body:

1. Create a container + a document (text JSON), as in
   `tests/Aws2Azure.IntegrationTests/DynamoDb/DynamoDbRealAzureCosmosBinaryTests.cs`.
2. **Read it back with** `x-ms-cosmos-supported-serialization-formats:
   CosmosBinary` → capture the exact `0x80…` response bytes (already exercised
   by `RealAzure_point_read_emits_CosmosBinary_body`).
3. **Delete the doc, then POST those exact `0x80` bytes back** as an upsert
   (`x-ms-documentdb-is-upsert: true`, same partition key), as the raw HTTP body.
4. **Verify the server parsed it**, not just stored it: a text read-back returns
   correct JSON, and a query filtering on a field value only present inside the
   binary body returns the document.

**Result (executed against real Azure — `cosmos-poc-conntest`, Brazil South,
Cosmos DB SQL API, serverless gateway, master-key auth):**

```
source binary first byte: 0x80
binary upsert status: 201          # server CREATED a doc from the 0x80 body (extracted id)
text read status:     200  marker=BINARY_PARSED_OK  n=777  id=d1
query  status:        200  {"Documents":[{"id":"d1","marker":"BINARY_PARSED_OK","n":777}],"_count":1}
```

Additional variants, all **HTTP 200**: upsert with no `Content-Type`, with
`application/json`, with `application/cosmos-binary`, and PUT/replace. The
gateway **auto-detects the `0x80` marker** — no negotiation header or special
`Content-Type` is required. This was a throwaway database (`spike332*`), deleted
after the run; no production data or resources were touched, and no encoder was
built (the binary body was obtained by reading a real doc back in binary).

**Conclusion:** server acceptance is **confirmed**, removing it as a risk. With
the §2 payoff also confirmed (~2.4× per write), the decision is **GO**, shipped
opt-in per the sequencing at the top of this document.

## 5. Prerequisite step of the GO: text zero-copy tail

The §2 benchmark also exposes the write path's *actual* allocation cost — the
tail, not the format — and fixing it is **step 1 of the GO** (it must land before
the binary writer, or the binary body would re-introduce the same allocation).

1. **Drop the `bytes → string → bytes` round-trip (the only real allocation).**
   After the `Utf8JsonWriter` pass, `BuildCosmosDocument` does
   `Encoding.UTF8.GetString(bw.WrittenSpan)` (`InferredAttributeStorage.cs:237`)
   and the caller wraps that `string` in `StringContent`, which **re-encodes
   string → UTF-8 bytes** — the `Text_TokenWalk_ToString` arm: +608 B…+1.6 KB per
   write, the *only* allocation on the path. Writing the `Utf8JsonWriter` output
   straight into the HTTP body (e.g. `ReadOnlyMemoryContent` / pooled content over
   the `ArrayBufferWriter` span, buffer returned after send) removes it.
2. **(Secondary) avoid per-property string materialization in the transform.**
   Where `BuildCosmosDocument` reads attribute values via `GetString()`, copying
   the name/value UTF-8 spans directly (`Utf8JsonReader`, no `string`) keeps the
   walk zero-materialization, matching the `Text_TokenWalk` profile measured in
   §2.

Both are **text-only, zero new format, no server-contract risk**, and apply to
every write handler — and crucially both **keep `Utf8JsonWriter`** (the
`Text_TokenWalk` path), not `JsonElement.WriteTo`. That preserves symmetry with
the binary writer that follows: step 2 lifts this `Utf8JsonWriter` walk behind a
shared `ITokenWriter`, so the text and binary encoders are the *same token walk*
with two writer implementations. The tail fix in (1) is a **prerequisite** for
the binary writer: a binary request body still flows through the same
`StringContent` / `GetString` tail unless that tail is fixed first, so without it
the binary path would re-introduce the 608–1656 B/write allocation it is meant to
avoid. Land this first, then the binary writer captures the ~2.4× format-step CPU
win on top of a zero-allocation body.

## Step 2 — production writer landed (#335): end-to-end validation

Step 2 ships the production `ITokenWriter` abstraction with two implementations
over **one** token walk (`InferredAttributeStorage.WriteCosmosDocumentCore`):
`Utf8JsonTokenWriter` (text, byte-identical to the prior path) and
`CosmosBinaryWriter` (the `0x80` encoder). Correctness is gated by a byte-identity
golden corpus plus a `decode(encode(x)) == text(x)` round-trip through the
production `CosmosBinaryDecoder` (`CosmosBinaryWriterTests`).

A second benchmark, `CosmosWriteEncodeBenchmarks`, measures the **shipping entry
points** end to end (`WriteCosmosDocument` vs `WriteCosmosDocumentBinary` over a
parsed DynamoDB item) rather than the isolated formatter of §2:

| Doc          | Text    | Binary  | Ratio | Alloc ratio |
|--------------|--------:|--------:|------:|------------:|
| lean         | 2.62 µs | 2.57 µs | 0.98  | 0.95        |
| payload_512  | 3.51 µs | 3.43 µs | 0.98  | 0.97        |
| wide_20s_20n | 18.6 µs | 17.9 µs | 0.96  | 0.99        |

The isolated **format step is ~2.4× cheaper** (§2), but end to end the two
encoders are within a few percent because the per-attribute `JsonElement`
traversal + `ParsedAttributeValue` parse + `GetString` materialization — shared
identically by both arms — dominates, and the formatter is only a small slice of
total encode cost. `CosmosBinaryWriter` additionally assembles into a pooled
scratch buffer (backpatching the `LC4` length/count prefixes needs random access)
and copies to the body writer on flush. **None of this changes the GO**: the
format-step CPU win is real and the infrastructure is correct and gated. It does
mean **step 3 should integrate the binary writer single-pass** — write directly
into the send buffer and avoid the redundant string materialization — to surface
more of the formatter advantage at the request boundary.

## Step 3 — single-pass wire encode landed (#342): JsonElement + GetString eliminated

Step 2 noted the end-to-end win was capped by the per-attribute `JsonElement`
traversal + `ParsedAttributeValue` parse + `GetString` materialization shared by
both arms. #342 removes exactly that tax: a new `Utf8JsonReader`-driven
`WriteCosmosDocumentCore` overload encodes the Cosmos body **straight from the
raw UTF-8 wire bytes** of the DynamoDB attribute-map, forwarding string values as
UTF-8 spans (verbatim when unescaped, unescaped once via `CopyString` otherwise)
through new `ITokenWriter` span overloads — no intermediate DOM, no per-attribute
string. Both back-ends share the new walk, so text and binary both benefit.
Numbers materialize their short ASCII text once (identical output). The
JsonElement overloads are retained for callers whose item is synthesized
(UpdateItem's read-modify-write) or embedded as a JSON string (sproc / Transact).
`PutItem` (fast + fallback) recovers the item byte-slice from the request body via
a no-alloc `Utf8JsonReader` scan (`ItemHandlers.TryLocateItemBytes`).

`CosmosWriteEncodeBenchmarks` gained `TextWire` / `BinaryWire` arms (production
entry points over the raw item bytes); its `[GlobalSetup]` now also asserts each
wire arm is byte-identical to its JsonElement counterpart. Representative
`--job short` pass (in-process CPU/alloc micro-benchmark, Azure-independent):

| Doc          | Text (base) | TextWire | Ratio | Alloc ratio | BinaryWire | Ratio | Alloc ratio |
|--------------|------------:|---------:|------:|------------:|-----------:|------:|------------:|
| lean         |   2.70 µs   | 2.03 µs  | 0.75  |    0.36     |  1.80 µs   | 0.67  |    0.31     |
| payload_512  |   3.50 µs   | 2.19 µs  | 0.63  |    0.21     |  1.90 µs   | 0.54  |    0.18     |
| wide_20s_20n |  22.9 µs    | 14.0 µs  | 0.64  |    0.47     |  12.2 µs   | 0.55  |    0.47     |

So the single-pass wire encode is a **25–46 % time reduction and 53–79 %
allocation reduction** end to end — an order of magnitude beyond the ~2–4 %
the binary format alone bought over text (Step 2), confirming the Step 2
diagnosis that the materialization tax, not the formatter, was the dominant
cost. The win applies to **both** the text and binary arms.

## How to run the encode benchmarks

```bash
dotnet run -c Release --project tests/Aws2Azure.Benchmarks -- \
  --filter '*CosmosEncodeBenchmarks*'        # §2 isolated formatter (~2.4×)
dotnet run -c Release --project tests/Aws2Azure.Benchmarks -- \
  --filter '*CosmosWriteEncodeBenchmarks*'   # production entry points, end to end
                                             # (Text/Binary JsonElement + TextWire/BinaryWire single-pass #342)
# add --job short for a fast pass
```

Numbers are in-process micro-benchmark CPU/allocation figures (emulator- and
Azure-independent); the `[GlobalSetup]` gate fails the run if the binary encoder
output does not round-trip back to the text output through the production
`CosmosBinaryDecoder`.

## References

- #319 / [`cosmos-binary-fused-decode-spike.md`](cosmos-binary-fused-decode-spike.md)
  — the decode-side spike whose two-pass redundancy justified fusion.
- #321, #327–#331 — shipped reader-side (decode) production port. Closed.
- #268 — CosmosBinary opt-in (response negotiation).
- #265 — precedent: feasibility-spike-before-commit for an uncertain-payoff
  binary/transport optimization (rntbd), also a no-go.
- SDK evidence: `Azure/azure-cosmos-dotnet-v3` — `ContainerCore.Items.cs`
  (`ProcessItemStreamAsync`), `RequestInvokerHandler.cs`,
  `GatewayStoreClient.cs`, `ConfigurationManager.cs`
  (`AZURE_COSMOS_BINARY_ENCODING_ENABLED`), `CosmosSerializationFormatOptions.cs`.
