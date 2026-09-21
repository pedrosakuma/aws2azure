# BatchWriteItem response and allocation overhead

Investigation for [#1027](https://github.com/pedrosakuma/aws2azure/issues/1027),
retrieved/measured on **2026-09-21**. The internal concurrency limit remains ten,
as decided in [#1026](batch-write-concurrency.md). Residual #1024 transport
attribution and historical missing-arrival causality remain open.

**Recommendation:** retain the measured text-encoder scratch reuse, with no
protocol change. Its payoff is lower allocation, not a demonstrated throughput,
CPU or tail-latency improvement. Temporary collection/publishing hooks are removed.

## Response suppression: not enabled

The shipping `CosmosClient.ApiVersion` is **`2018-12-31`**, applied to
`x-ms-version`. Batch puts use `POST /docs` with
`x-ms-documentdb-is-upsert: true`. Successful responses are disposed without
deserializing their documents; non-success response bodies remain part of error
mapping. The transport uses `ResponseHeadersRead`, so this is not an opportunity
to remove a successful-response JSON deserialization that already does not occur.

The authoritative sources checked were:

| Source | Evidence and limit |
| --- | --- |
| [REST version reference](https://learn.microsoft.com/en-us/rest/api/cosmos-db/) | Lists `2018-12-31` and the version header. It does not establish a response-suppression contract for that version. |
| [Create a Document](https://learn.microsoft.com/en-us/rest/api/cosmos-db/create-a-document) | Documents the upsert header and a returned document body. No suppression preference is specified. The example itself uses an older version, so it is not an exact-version suppression verification. |
| [Common REST request headers](https://learn.microsoft.com/en-us/rest/api/cosmos-db/common-cosmosdb-rest-request-headers) | Does not document a suppression header/preference. |
| [SDK `EnableContentResponseOnWrite`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.azure.cosmos.itemrequestoptions.enablecontentresponseonwrite) | Official SDK feature for create/upsert/patch/replace; not a standalone REST contract for this proxy's pinned API version. |
| [Pinned SDK request handler](https://github.com/Azure/azure-cosmos-dotnet-v3/blob/5e6232d25f84074d5008e97670d3e0dc07891f0e/Microsoft.Azure.Cosmos/src/Handler/RequestInvokerHandler.cs#L61-L69) | Maps eligible SDK requests to its `Prefer` / `PreferReturnMinimal` constants. This is a hint only, not proof of public REST compatibility or controlled Azure behavior. No SDK code was imported. |

Stable documentation revisions: version/create pages at
[`7df9a542`](https://github.com/MicrosoftDocs/azure-docs-rest-apis/tree/7df9a542558a5d7b30c30bb25f0ae4fd34d72530/docs-ref-conceptual/cosmos-db),
common headers at
[`3b606f0f`](https://github.com/MicrosoftDocs/azure-docs-rest-apis/blob/3b606f0f84187c304778557f73223ff65415f7b3/docs-ref-conceptual/cosmos-db/common-cosmosdb-rest-request-headers.md).

**Decision:** documented support at the exact version was not established.
Do not implement a guessed header, upgrade the API version, add a toggle, or
claim the service is incapable of suppression. No suppression request or billed
experiment was made. Enabling it would first require authoritative exact-version
confirmation and separately authorized, finite real-Azure verification of
create/update upserts, body bytes, errors, session/routing headers, cancellation,
disposal and connection reuse. Existing relay byte counters alone do not prove
connection reuse. There is no before/after suppression result to report.

## Focused proxy allocation evidence

The source-pinned runtime was
`bad054ef2377717dc2ad89fcd299b8498047e585` (unchanged limit ten), self-contained
**JIT**. Temporary collector harness:
`65b6b460fbb5674e4aac4793ee9eedd6dc80cc9b`.
Run [35641589432](https://github.com/pedrosakuma/aws2azure/actions/runs/35641589432),
artifact [`batch-write-diagnostic`, 10658622192](https://github.com/pedrosakuma/aws2azure/actions/runs/35641589432/artifacts/10658622192),
SHA256 `0edae21187b0d551d6127a49d335a2402bf4014324fbad88535b2d0b583ed5be`.

Two fresh dedicated-runner windows used the existing
`BatchWriteExperimentTests.Selected_cell_records_bounded_diagnostic` scenario:
`25:8:put:distinct:proxy` and `25:8:mixed:shared:proxy`. They retained the
five-second dispatch, 128-batch inventory, bounded drain, corrected relay,
opt-in stage metrics and resource samples. Emulator repository digest:
`sha256:2db1f9e74c506bcf6fc347aa937aea1c00fa756061296a5a9efba530ce86ec02`;
actual image ID:
`sha256:77f8a39f5c39e0b7878c8d9cddd70ff08872695dec9b85d2055b38d5d58d5302`.

`dotnet-trace` **10.0.745401** attached only to the verified proxy PID after
warmup, with a **ten-second / 32 MiB** EventPipe session:

```text
Microsoft-Windows-DotNETRuntime:0x1:5
Microsoft-DotNETCore-SampleProfiler:0x0:4
```

No dump, parameter values, HTTP payload events, driver or emulator process was
profiled. Offline analysis used TraceEvent **3.2.6**, filtering the target PID
and inclusive UTC interval from the report's
`proxyProcess.before/after.CapturedAtUtc`. Setup, warmup, post-window idle and
rundown were excluded. Both traces cover the complete brackets and report
zero lost events.

| Cell | Report/trace stem | PID | UTC bracket | Allocation ticks / weighted bytes | Thread samples |
| --- | --- | ---: | --- | --- | ---: |
| Put | `3fea18744a9c4f89844d2cc80669095b` | 3935 | 18:59:02.7141025–18:59:08.0329719 | 323 / 34,381,840 | 55,982 |
| Mixed | `e7d2ec818a834f5c8c7c8326c4e3d93d` | 4597 | 18:59:37.1589277–18:59:42.3706107 | 391 / 41,669,168 | 45,866 |

Trace SHA256 respectively:
`fe190b750f7364914ac4719e7fe1973c677febc4e1f45d38381a32f465699d65`,
`cb12e89d1ef5daff39476797e074dac2a0bfc7fbb0023bfb290e99c046e6a657`.
Both reports contain the same complete 354-file runtime manifest; apphost hash
`dad4d253479ee7dceb3e39546791317a60b1bd95028baa4dc9c0d437f35c6003`,
DynamoDb assembly hash
`109f2a1f0b72224e778261151b0f9b9c7c1cb1484097c8d69315b9aef2b29022`.
The apphost hash alone is not a managed implementation identity.

### What the profiles establish

For allocation ticks, sum `AllocationAmount64` by `TypeName`, or by presence of
a named frame in the resolved stack. These are **sample-weighted estimates**,
not exact per-type bytes or object counts. Inclusive frame rows overlap and
must not be summed; asynchronous ancestry can be incomplete.

| Type / inclusive stack | Put weighted share | Mixed weighted share |
| --- | ---: | ---: |
| `System.String` | 35.02% | 32.70% |
| `System.Byte[]` | 10.79% | 11.52% |
| `System.Char[]` | 8.01% | 7.17% |
| `System.Net.Http.Headers.HeaderEntry[]` | 6.82% | 9.71% |
| `ItemHandlers.BuildItemDocumentBytes` stack | 13.60% (44 ticks) | 17.43% (68 ticks) |
| `ArrayBufferWriter<byte>` constructor under that builder | 3.39% (11 ticks) | 6.42% (25 ticks) |
| `PrometheusExporter.BuildKey` stack | 26.60% | 21.47% |
| `BatchWriteDiagnostics` stack | 18.24% | 10.73% |

The temporary text-document scratch allocation is a concrete candidate:
`BuildItemDocumentBytes` allocates `ArrayBufferWriter<byte>(1024)`, encodes,
then copies into the independently owned array required across asynchronous
batch execution. Profiles also show string transcoding, HTTP header structures,
async state and diagnostic formatting. Broadly pooling those families or
rewriting shared transport/metrics is not justified by these two short traces.
The diagnostic rows explicitly prevent treating all observed allocation as
uninstrumented shipping overhead.

Thread samples prominently contain `Monitor.Wait`, `LowLevelLifoSemaphore`,
`WaitHandle` and native reads. They include blocked/external time and are
**not scheduler on-CPU samples**. In addition, 4,012/3,941 leaf names are
unresolved (although stacks are present). No CPU percentage, allocator-specific
CPU saving, or saturation conclusion is inferred from these rows or from the
stage wall-time sums. Actual process CPU is reported separately below.

### Profiled-window context, not an optimization comparison

| Cell | Completed batches/items | Items/s | Batch p95/p99 ms | Item p95/p99 ms | Proxy CPU s | Allocated B/item (B/batch) | Sampled proxy WS MiB | Response body bytes |
| --- | --- | ---: | --- | --- | ---: | --- | ---: | ---: |
| Put | 71 / 1,775 | 334.65 | 789.43 / 864.10 | 789.40 / 864.07 | 3.65 | 19,663.88 (491,596.96) | 109.62 | 1,080,975 |
| Mixed | 99 / 2,475 | 476.30 | 491.11 / 606.95 | 491.09 / 606.93 | 3.86 | 17,017.61 (425,440.32) | 107.78 | 730,620 |

These are emulator-bound, profiler-perturbed finite windows, not sustainable
capacity or a JIT/AOT comparison. CPU/allocation brackets are process-wide;
working-set maxima are sampled lower bounds, not true peaks. Proxy Gen2 deltas
are zero; proxy Gen0/Gen1 remain unavailable. The all-put response body is
609 bytes/write for this fixture, not a universal document size.

All five relay write boundaries equal acknowledged items; no fault,
cancellation, dropped/active attempt, repeated dispatch, 429, non-success
response, unprocessed item or client resubmission was observed. Snapshots and
cleanup completed; inventory did not exhaust.

The earlier collector run
[35640791216](https://github.com/pedrosakuma/aws2azure/actions/runs/35640791216)
is preserved as failed evidence. Its harness `46c96129` waited for an interactive
CLI message omitted from redirected output, and both cells failed **before
measurement**. The verified correction recognizes `Trace Duration` after
session creation; it is the only corrected collection. Those first traces
are not batch-load evidence or a proxy regression, and do not explain any
historical #1024 failure.

## Narrow candidate and deterministic evidence

The candidate changes only the text builder's temporary scratch to the existing
`PooledByteBufferWriter`, disposed synchronously on success or exception. The
returned body remains an independent GC-managed array; no pooled lease escapes
into asynchronous sends, retries, UnprocessedItems or early validation returns.
Binary encoding, key codec, TTL/order-key forwarding, prevalidation, concurrency,
headers, response handling and transport semantics are unchanged.

`BatchWriteDocumentScratchTests` compares the actual factory with the previous
fresh-scratch shape using the existing `TranslationAllocGate` measurement
helper. On the shared local host only exact allocation shape was evaluated,
not meaningful local latency/CPU/throughput:

| Payload fixture | Previous B/item | Candidate B/item | Saved B/item |
| --- | ---: | ---: | ---: |
| 256 ASCII bytes | 2,232 | 1,184 | 1,048 |
| 8 KiB ASCII bytes | 51,672 | 24,992 | 26,680 |

Both allocation assertions failed against the original implementation (zero
saving) and passed after the change. Tests also retain 25 bodies across input
disposal and repeated scratch reuse, exercise text buffer growth and binary
output, compare exact bytes including TTL, and exercise encoding failure followed
by successful reuse. Existing batch prevalidation/partial-error tests are retained.

Existing `BatchWriteParseAllocTests` still report 2,408 B per 25-entry pooled-range
parse versus 9,608/17,304/50,896 B for the legacy retained-DOM fixture shapes.
This is an already-shipped improvement, not new savings. The existing pooled
wire-encoder microguards report 168 B/text encode and 56 B/binary encode; they
do **not** include this factory's detached output array or the whole batch.
No microprobe number is promoted to an end-to-end gain.

## Native AOT decision evidence

The candidate was checked with paired Native AOT runtimes before selection. Baseline is
`bad054ef2377717dc2ad89fcd299b8498047e585`; candidate is
`5ad8a4712f33bd4166b545b1f45007cdc34bed62`. The one-off publisher at
`ee235a74c9d337ebfb3823fa66a6feb0828b4386` explicitly uses `PublishAot=true`
and records `BuildMode: native-aot` in the original runtime manifests. This is
not the ordinary diagnostic publisher's self-contained JIT mode.

Run [35642912661](https://github.com/pedrosakuma/aws2azure/actions/runs/35642912661)
uses **baseline → candidate → candidate → baseline**, three cells per block:
`25:1:put:distinct:proxy`, `25:8:put:distinct:proxy`,
`25:8:mixed:shared:proxy`. No profiler is attached. Both slots have identical
diagnostic opt-ins, fixed image, fresh processes/state, single-use inventory
and finite window/drain bounds. Neither the temporary collector nor publisher
mode is intended as a permanent configuration knob.

All twelve windows completed without another diagnostic repetition:
artifact [`batch-write-diagnostic`, 10659740543](https://github.com/pedrosakuma/aws2azure/actions/runs/35642912661/artifacts/10659740543),
SHA256 `9ba440b59e57ec6686412fab178b1d0cd8d6bf89e2dd671d349ddf33d095dd7c`.
Each slot reused its exact **15-file** manifest across all six windows.
Native executable hashes:

- Baseline: `5a1448219ddb2d4c8f5525e27bd954ff1e41bf7e8ec23981cd262b0f13a11d39`.
- Candidate: `d0cef219e34b4563fe2da6ea4b9bfca54e75e965790ad5e94a2c04c27edb506b`.

The publisher manifests and build logs establish Native AOT; the report's
`host.runtime` describes the JIT test driver, **not** the external proxy.

`P1/P8` are the put cells at external concurrency one/eight; `M8` is mixed.
Blocks 1/4 are baseline, 2/3 candidate. Each row is a separate finite observation.
Throughput includes drain, and no window exhausted inventory.

| Block | Cell | Batches | Settled s | Items/s | Batch p95/p99 ms | Item p95/p99 ms |
| --- | --- | ---: | ---: | ---: | --- | --- |
| 1 baseline | P1 | 57 | 5.024 | 283.62 | 277.59 / 413.33 | 277.56 / 413.31 |
| 1 baseline | P8 | 45 | 5.459 | 206.07 | 1455.51 / 1600.60 | 1455.48 / 1600.58 |
| 1 baseline | M8 | 50 | 5.494 | 227.52 | 1251.80 / 1262.61 | 1251.79 / 1262.60 |
| 2 candidate | P1 | 81 | 5.020 | 403.39 | 80.74 / 144.79 | 80.71 / 144.77 |
| 2 candidate | P8 | 80 | 5.594 | 357.56 | 690.73 / 726.58 | 690.71 / 726.56 |
| 2 candidate | M8 | 80 | 5.233 | 382.22 | 670.20 / 809.60 | 670.18 / 809.53 |
| 3 candidate | P1 | 55 | 5.038 | 272.93 | 302.53 / 454.49 | 302.51 / 454.43 |
| 3 candidate | P8 | 72 | 5.198 | 346.27 | 797.22 / 833.55 | 797.19 / 833.52 |
| 3 candidate | M8 | 94 | 5.188 | 452.94 | 839.37 / 1017.65 | 839.35 / 1017.63 |
| 4 baseline | P1 | 76 | 5.011 | 379.20 | 100.71 / 156.27 | 100.68 / 156.24 |
| 4 baseline | P8 | 72 | 5.170 | 348.19 | 815.78 / 878.37 | 815.77 / 878.36 |
| 4 baseline | M8 | 109 | 5.219 | 522.09 | 723.02 / 849.89 | 722.99 / 849.88 |

The following resource brackets are proxy-process scoped, including diagnostics.
Allocation/batch is rounded to the nearest byte; CPU/item normalizes different
work counts but is not an allocator-specific CPU measurement.

| Block | Cell | Allocated B/item | Allocated B/batch | CPU s (ms/item) | Sampled WS MiB | Response body bytes |
| --- | --- | ---: | ---: | --- | ---: | ---: |
| 1 | P1 | 19,102.65 | 477,566 | 0.41 (0.288) | 50.51 | 867,825 |
| 1 | P8 | 20,185.27 | 504,632 | 0.36 (0.320) | 56.49 | 685,125 |
| 1 | M8 | 17,712.04 | 442,801 | 0.38 (0.304) | 56.29 | 369,000 |
| 2 | P1 | 17,874.16 | 446,854 | 0.58 (0.286) | 54.47 | 1,233,225 |
| 2 | P8 | 18,498.99 | 462,475 | 0.62 (0.310) | 56.82 | 1,218,000 |
| 2 | M8 | 16,679.71 | 416,993 | 0.57 (0.285) | 56.00 | 590,400 |
| 3 | P1 | 18,063.56 | 451,589 | 0.38 (0.276) | 48.45 | 837,375 |
| 3 | P8 | 18,581.93 | 464,548 | 0.56 (0.311) | 56.54 | 1,096,200 |
| 3 | M8 | 16,549.44 | 413,736 | 0.66 (0.281) | 57.11 | 693,720 |
| 4 | P1 | 18,954.30 | 473,857 | 0.52 (0.274) | 53.68 | 1,157,100 |
| 4 | P8 | 19,630.71 | 490,768 | 0.53 (0.294) | 55.50 | 1,096,200 |
| 4 | M8 | 16,937.88 | 423,447 | 0.75 (0.275) | 63.57 | 804,420 |

Response bytes remain **609 per item** for puts and **295.2 per item** for the
mixed fixture, in both arms; different totals reflect different completed work.
All windows have one observed proxy Gen2 collection, not evidence of fewer GCs.
Sampled WS is not true peak memory and does not establish a memory reduction.
All relay boundaries equal acknowledged items, with zero observed failed,
cancelled, dropped, active or repeated attempts, 429/non-success responses,
unprocessed work or client resubmissions. All snapshots and cleanup completed;
no resource samples were dropped or failed. Emulator RU remains one per write,
not a real-Azure RU result.

### Selection and nonclaims

Compare block 2 against 1 and block 3 against 4:

| Cell | First-pair allocation/item reduction | Reversed-pair reduction |
| --- | ---: | ---: |
| P1 | 6.43% | 4.70% |
| P8 | 8.35% | 5.34% |
| M8 | 5.83% | 2.29% |

The repeatable native allocation direction supports the deterministic mechanism
test. Whole-process reductions are **not** exact factory savings: fixed window
costs, differing work counts, metrics and sampling remain in their denominator.
The mixed result is expected to include unchanged delete work.

Select the two-line scratch-storage change **for allocation reduction only**.
No new cache, thread, global lock, configuration or scratch lease across async
work is introduced. The existing shared pool can retain bucket capacity after
return, so there is no claim of a universal sidecar peak-memory reduction.

Do **not** attribute the large throughput/tail swings to scratch reuse. Unchanged
baseline P8 throughput itself moves from 206.07 to 348.19 items/s across blocks;
candidate P1's second p99 is worse than the final baseline. Two short observations
do not establish latency noninferiority, statistical significance, CPU savings,
sustainable capacity or second-scale latency improvement. This uncertainty is
retained, not hidden by averaging percentiles, raising thresholds or rerunning
the campaign. Functional equivalence and allocation payoff are the supported
results; deployment-specific performance remains scenario-dependent.

## Reproduction and acceptance boundaries

Download the original artifacts:

```bash
gh run download 35641589432 -n batch-write-diagnostic \
  -D .tools/overhead-evidence/profile
gh run download 35642912661 -n batch-write-diagnostic \
  -D .tools/overhead-evidence/native
```

The profile collector is source-pinned at `65b6b460`; the native decision harness
at `ee235a74`. Their temporary changes are not present in the final implementation.
Full runtime manifests, report timestamps, thread/resource samples and host
pressure receipts remain in the artifacts; trace hashes and compact measurements
above preserve the decision evidence beyond artifact retention.

Focused coverage includes `BatchWriteDocumentScratchTests`,
`ItemDocumentBodyTests`, existing `BatchWriteItemHandlerTests` and allocation
guards. Canonical PR validation includes all unit/conformance/deterministic-perf
tests, gap validation and Linux Native AOT publish. No gap status, response
contract, concurrency limit or performance threshold changes.

The chosen allocation change requires `run-integration` and `run-perf`, not a
billed gate. Response suppression remains unverified at the pinned REST version
and disabled. No real-Azure resources were provisioned. The remaining #1024
transport/historical questions are not closed by this allocation result.
