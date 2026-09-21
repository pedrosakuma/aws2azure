# BatchWriteItem retained-body ownership

Measured on **2026-09-21** for [#1034](https://github.com/pedrosakuma/aws2azure/issues/1034),
comparing the merged scratch-only baseline
`f455352e19e19880f5b20bdb3a21f4b9508ad9be` with retaining each encoded body
through asynchronous dispatch. This is not a comparison against the older
fresh-scratch implementation. Concurrency remains ten; no protocol, threshold,
gap status, authentication or shared transport change is proposed.

**Recommendation: keep the final managed-array copy.** The source-pinned
candidate `5a4037a104912052e625409f040190daede6cb29` lowered allocation, but
concurrency-8 resident memory increased in both ordering pairs without a
consistent throughput or CPU advantage. The sidecar tradeoff does not justify
shipping retained-body ownership on this evidence. The final PR restores
shipping sources exactly to the scratch-only baseline; experimental ownership
tests remain accessible in the pinned candidate commit. No local throughput
result is used: the local machine is shared.

## Ownership and completion boundary

The candidate reuses `ItemHandlers.ItemDocumentBody`, not a new memory-pool
wrapper. A single disposable work list owns all bodies, including those already
built when a later item's validation, metadata lookup or encoding fails.
Workers borrow memory but never dispose copied owner structs. `Task.WhenAll`
settles every worker before releasing bodies; cancellation does not replace that
with an abandoned `WaitAsync`. Cleanup is idempotent and runs before the AWS
response is encoded. Validation still completes before document mutations.

The relevant boundary is the *whole logical Cosmos send*, including retries and
eligible regional failover, not just receipt of response headers:

* `CosmosClient` creates built-in `ReadOnlyMemoryContent` for each attempt and
  awaits the shared transport directly. Its outer call owns regional retries.
* Shared `AzureHttpClient` directly awaits `HttpClient.SendAsync`. Its retry clone
  obtains independent managed bytes through `ReadAsByteArrayAsync`; the original
  borrowed memory must remain valid while cloning.
* In the verified .NET **10.0.12**
  [`ReadOnlyMemoryContent`](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.Http/src/System/Net/Http/ReadOnlyMemoryContent.cs),
  serialization awaits the stream write and `AllowDuplex` is false.
  [`Http2Connection`](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/Http2Connection.cs)
  awaits the upload task before returning headers for non-duplex content.
  This is not a general guarantee for arbitrary `HttpContent`.

`AzureHttpClientRequestLifetimeTests` uses a real loopback HTTP/2 server with
bounded flow-control windows: early error headers cannot finish a blocked
256 KiB memory upload; releasing the reader preserves exact bytes, and cancelling
settles the send before its pooled owner is disposed.

The candidate's
[`BatchWriteOwnershipTests`](https://github.com/pedrosakuma/aws2azure/blob/5a4037a104912052e625409f040190daede6cb29/tests/Aws2Azure.UnitTests/DynamoDb/BatchWriteOwnershipTests.cs)
observes actual shared-pool rentals/returns, poisons
returned buffers, and holds consumers on a separate dedicated thread. It checks
independent bodies, thread migration, cancellation with pending consumers,
503 retry cloning, partial 429, hard errors, safe write-region failover
(403/substatus 3), and text/binary bodies. It also checks zero mutations and
balanced rentals after later invalid input, metadata failure and malformed
UTF-16 during encoding. A deliberately premature return confirms the poison
sensor detects changed bytes. These tests do not prove safety for arbitrary
custom duplex content or unrecoverable process failures.

## Exact allocation shape versus retained capacity

`BatchWriteBodyAccountingTests` prepares 25 distinct hash/range-key puts matching
the diagnostic fixture and warms both factory paths. This is deterministic
same-thread **encoding-only** accounting, not an end-to-end performance test.
Parsing, work records, transport, headers, diagnostics and response processing
are excluded from its allocation bracket. Both encoders produce identical bytes.

| ASCII payload | Compact body bytes/batch | Retained rented capacity/batch | Scratch-plus-copy allocation/batch | Retained-owner encoding allocation/batch |
| --- | ---: | ---: | ---: | ---: |
| 256 B | 9,075 | 25,600 | 39,000 | 29,200 |
| 8 KiB | 207,475 | 819,200 | 634,200 | 426,000 |

Removing the final copy saves **392 / 8,328 B per item** in these fixtures.
Conversely, retained body capacity is **2.82× / 3.95×** logical body bytes.
For eight simultaneously live 25-item batches, the fixture's additional body
capacity alone is approximately **129 KiB / 4.67 MiB**, before other request
state or pool caches. These are capacity calculations, not observed process
peaks. Returning an array ends the lease; it does not force the shared pool to
release its cached array to the GC or OS.

## Actual diagnostics MCP provenance

Unlike [#1027](batch-write-overhead.md), which used CLI `dotnet-trace`,
EventPipe and offline TraceEvent, this investigation actually invoked
**dotnet-diagnostics MCP** collectors against a verified task-owned proxy.
No dump, sensitive parameters, request payload events or unrelated process
attachment was used. No exclusive-host acknowledgement was set locally.

The target was the unchanged baseline, self-contained **JIT**, CoreCLR 10.0.12,
with a loopback fake backend and synthetic credentials. Its apphost SHA256 was
`dad4d253479ee7dceb3e39546791317a60b1bd95028baa4dc9c0d437f35c6003`;
the publisher retained a full 354-file executable/dependency manifest, because an apphost
hash alone does not identify the managed implementation. The qualitative
workload used one concurrent 25-item batch, alternating 256-byte/8 KiB values,
at most 80 batches and a hard 180-second lifetime. The DynamoDb assembly SHA256
was `2520f486a39cca3c26b17f7d1bd49762781111d4d72c79b0c1b82bf813795a0f`.

An initial owned target, PID 2459596, finished 80 successful batches after the
recommended 15-second counters call but before later collectors attached.
Its late empty counter handle `YSE8W4BB23GV18AV8BS0`, allocation startup failure
and process-gone CPU result are **failed samples, not zero measurements**.
Investigation: `inv-75c30d11cf9346cfbaf8c3ffedc3af1a`.

One timing correction increased the inter-batch interval from 0.5 to 1 second,
without increasing the inventory or load. The second target, PID **2459999**,
was independently verified by executable path and successful health response:

| MCP operation | Handle / bounded result |
| --- | --- |
| Investigation, before collectors | `inv-694a5a4f73fd4251a849a3f271cf868d` |
| First recommended 15-second runtime counters | `Q6CZTKZTDF77WJTB47X0` |
| Five-second allocation sampling | `X0AA523BX3WJSZJWTARG`: 46 allocation ticks; 5,813,992 sample-weighted bytes |
| Five-second CPU sampling | `K1QDCV07STYZW7QGSGH0`: 69,583 thread samples |

Allocation sampling attributed 21 ticks/2,259,544 weighted bytes to strings.
Only seven inclusive builder-rooted samples were present (six under encoding,
one under metric formatting): **not enough to isolate the final-copy fraction
or prove lifetime correctness**. Inclusive rows must not be summed.

The CPU tool classified 8,236 samples as “running” and 61,347 as “waiting”;
leading “running” leaves included a potentially blocking native file-watcher
read (4,093) and an unresolved address (4,089). These classifications are
**not verified scheduler on-CPU attribution or evidence of body-copy CPU cost**.
The counters reported an allocation-rate interval of 1,543,224 B, heap 13.887 MB
and working set 115.085 MB. These shared-host, profiler-perturbed observations
are neither capacity evidence nor a before/after comparison.

The second target was stopped after 67 successful batches and zero failed
batches; owned proxy/backend teardown completed. Portable summary export was
denied because an explicit `eventpipe` scope was not granted. That denial was
not bypassed: the handles above document successful collection/drilldowns,
not an exported trace artifact. MCP handle retention is temporary. The Native
AOT decision windows below do not use these JIT collector results as performance
proof and run without a profiler.

## Native AOT comparison

The dedicated-runner campaign is
[35654638425](https://github.com/pedrosakuma/aws2azure/actions/runs/35654638425),
using `BatchWriteExperimentTests.Selected_cell_records_bounded_diagnostic`.
Both source-pinned runtimes use explicit `native-aot` publishing. The four cells
are 25-item distinct puts, external concurrency 1/8, at 256/8,192 payload bytes,
in **baseline → candidate → candidate → baseline** order: 16 finite windows,
not a full grid. Fresh processes/state, the corrected relay, opt-in internal
stage diagnostics, 250 ms resource sampling, four warmup batches, five-second
dispatch, 128-batch inventory and 30-second drain are unchanged.

Repository image digest:
`mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator@sha256:2db1f9e74c506bcf6fc347aa937aea1c00fa756061296a5a9efba530ce86ec02`.
An additional five-second idle resource snapshot is explicitly outside timing,
CPU/allocation brackets and sampled window maxima. It does not force GC or
measure the exact inventory retained inside `ArrayPool`.

Artifact:
[`batch-write-diagnostic`, 10664900325](https://github.com/pedrosakuma/aws2azure/actions/runs/35654638425/artifacts/10664900325),
SHA256 `0bf5d0f943c892077b80b1548d6ef79e968ac0b3b369c7083fbaa92210a71181`.
Harness source is exactly `5a4037a104912052e625409f040190daede6cb29`.
All 16 reports match their slot's complete 15-file `native-aot` manifest:

| Runtime | Source | Native executable SHA256 |
| --- | --- | --- |
| Scratch plus final copy | `f455352e19e19880f5b20bdb3a21f4b9508ad9be` | `64f78a7acd92d2c11562b65e1e9efe130ca5f7d14abecc247e45f48ed029b60b` |
| Retained pooled bodies | `5a4037a104912052e625409f040190daede6cb29` | `91f9e3ef0ec55c9f1bd61cfbcb3b9fcca075f4adce9f97bab86f9fbfaaf47ee4` |

Actual emulator image ID in every report:
`sha256:77f8a39f5c39e0b7878c8d9cddd70ff08872695dec9b85d2055b38d5d58d5302`.
The artifact also includes build-toolchain information and per-block host
snapshots. No Azure resources were provisioned.

### All observations, without dropping unfavorable repetitions

`M` denotes the scratch-only baseline; `C` the retained-body candidate. Payload
is ASCII bytes; `c` is external concurrency. Latency is milliseconds. All
windows ran for the full dispatch bound without exhausting inventory
(60–80 completed batches each); every batch acknowledged 25 items.

| Payload / c | Block | Batches | Items/s | Batch p95/p99 | Item p95/p99 | CPU s | B/item (B/batch) | Sampled peak / idle MiB |
| --- | --- | ---: | ---: | --- | --- | ---: | --- | --- |
| 256/1 | 1M | 66 | 328.45 | 93.33/102.51 | 93.29/102.48 | 0.59 | 18000.96 (450024.00) | 52.89/53.40 |
| 256/1 | 2C | 66 | 328.06 | 96.03/98.88 | 96.00/98.84 | 0.60 | 17615.75 (440393.82) | 52.49/53.00 |
| 256/1 | 3C | 66 | 326.62 | 93.57/100.74 | 93.54/100.71 | 0.60 | 17620.87 (440521.70) | 51.71/52.21 |
| 256/1 | 4M | 69 | 344.38 | 86.80/91.92 | 86.78/91.89 | 0.61 | 17943.48 (448586.90) | 53.31/50.55 |
| 256/8 | 1M | 73 | 348.31 | 763.70/902.00 | 763.67/901.98 | 0.68 | 18581.79 (464544.66) | 55.46/55.46 |
| 256/8 | 2C | 75 | 359.92 | 745.30/783.21 | 745.27/783.19 | 0.71 | 18301.84 (457545.92) | 58.71/58.72 |
| 256/8 | 3C | 74 | 349.63 | 721.19/790.49 | 721.16/790.46 | 0.70 | 18291.46 (457286.49) | 58.46/58.47 |
| 256/8 | 4M | 80 | 370.24 | 628.24/732.41 | 628.19/732.38 | 0.72 | 18493.92 (462347.90) | 57.37/57.37 |
| 8192/1 | 1M | 61 | 302.31 | 98.40/102.29 | 98.23/102.20 | 0.62 | 58119.99 (1452999.74) | 52.78/52.79 |
| 8192/1 | 2C | 60 | 299.30 | 102.02/109.55 | 101.87/109.41 | 0.62 | 49876.42 (1246910.40) | 52.41/52.43 |
| 8192/1 | 3C | 63 | 313.88 | 95.04/101.75 | 94.97/101.57 | 0.63 | 49847.85 (1246196.32) | 52.49/52.51 |
| 8192/1 | 4M | 63 | 313.89 | 96.38/104.36 | 96.28/104.22 | 0.65 | 58095.38 (1452384.51) | 52.43/52.45 |
| 8192/8 | 1M | 68 | 320.11 | 821.68/843.28 | 821.53/841.15 | 0.77 | 59175.84 (1479396.12) | 62.11/61.25 |
| 8192/8 | 2C | 66 | 318.40 | 738.16/808.58 | 738.06/808.19 | 0.74 | 54344.48 (1358611.88) | 66.38/66.38 |
| 8192/8 | 3C | 70 | 325.22 | 774.17/812.81 | 774.07/812.69 | 0.75 | 54067.87 (1351696.80) | 64.72/64.72 |
| 8192/8 | 4M | 68 | 324.47 | 709.30/813.51 | 709.11/813.27 | 0.75 | 59122.96 (1478074.12) | 62.28/62.28 |

CPU and allocation are whole-proxy process deltas, including diagnostics;
`B/item = proxyAllocatedBytes / acknowledgedItems`, and `B/batch` uses completed
batches. The sampled maxima and idle RSS are not the same time boundary.
GC occurred during these windows: Gen2 deltas were **one** for every small-body
window and **eight** for every large-body window, in both implementations.
An idle heap endpoint includes GC timing effects and cannot identify live pool
objects. In particular, small-body baseline idle heaps differed sharply between
blocks despite identical binaries.

The stages below are measured means per item, not inferred percentile
differences. `Downstream` includes the logical send/status handling; relay
latency extends through the backend response body and is a different boundary.

| Payload / c | Block | Semaphore wait mean ms | Downstream mean ms | Relay p95/p99 ms |
| --- | --- | ---: | ---: | --- |
| 256/1 | 1M | 15.572 | 24.927 | 36.20/41.01 |
| 256/1 | 2C | 15.553 | 24.758 | 35.45/38.99 |
| 256/1 | 3C | 15.676 | 24.902 | 34.99/39.37 |
| 256/1 | 4M | 14.773 | 23.719 | 33.66/36.32 |
| 256/8 | 1M | 133.358 | 176.454 | 211.63/217.82 |
| 256/8 | 2C | 131.951 | 170.414 | 216.88/223.16 |
| 256/8 | 3C | 134.933 | 174.771 | 211.30/230.25 |
| 256/8 | 4M | 126.899 | 164.902 | 197.39/202.83 |
| 8192/1 | 1M | 16.887 | 26.704 | 37.39/41.36 |
| 8192/1 | 2C | 17.005 | 27.023 | 38.71/44.39 |
| 8192/1 | 3C | 16.327 | 25.742 | 36.76/39.71 |
| 8192/1 | 4M | 15.938 | 25.582 | 35.82/39.87 |
| 8192/8 | 1M | 148.607 | 190.880 | 224.00/255.93 |
| 8192/8 | 2C | 149.710 | 198.515 | 218.88/226.59 |
| 8192/8 | 3C | 141.247 | 183.741 | 225.94/240.73 |
| 8192/8 | 4M | 149.048 | 196.779 | 223.70/251.63 |

Every report has successful cleanup, complete final/idle snapshots, no dropped
or failed resource samples, and equal arrived/dispatch/headers/body/response-write
counts matching acknowledged items. There were no failed batches, unacknowledged
requests, non-success responses, 429s, repeated dispatches, resubmissions,
cancellations or active observations. The campaign passed once; no performance
rerun was requested.

### Decision and uncertainty

Pairing `2C/1M` and reversed `3C/4M`:

* Whole-proxy allocation/item fell **2.14%/1.80%** at small-body c1 and
  **1.51%/1.09%** at small-body c8. Large-body reductions were
  **14.18%/14.20%** at c1 and **8.16%/8.55%** at c8.
* At c8, sampled resident maxima increased **3.25/1.09 MiB** for small bodies
  and **4.27/2.44 MiB** for large bodies. After five idle seconds, corresponding
  RSS increases were **3.26/1.10 MiB** and **5.14/2.44 MiB**. This measures
  retained *process residency*, not an exact shared-pool array inventory;
  attributing all of it to cached body arrays would be unjustified.
* Small-body items/s changed **−0.12%/−5.16%** at c1 and
  **+3.33%/−5.57%** at c8. Large-body changes were **−0.99%/approximately 0%**
  and **−0.53%/+0.23%**. There is no consistent throughput improvement.
* Normalized CPU µs/item increased **1.69%/2.83%** and **1.63%/5.11%** for
  small bodies. Large-body changes were **+1.67%/−3.08%** at c1 and
  **−0.98%/−2.86%** at c8. These small differences, coarse process-CPU ticks
  and two observations per cell do not establish a CPU improvement.
* Tails and unchanged-baseline observations vary materially. In particular,
  favorable first-pair c8 p95 changes reverse direction in the second pair.
  No latency-noninferiority or universal memory regression is claimed.

The measured allocation saving is real within the stated bracket, but not a
sufficient sidecar benefit to accept extra resident memory and more complex
asynchronous ownership. **Keep scratch reuse plus compact owned arrays.**
This does not prove retained pooling cannot benefit other payloads, durations,
GC configurations or deployments. Exact Native AOT pool-object retention and
long-horizon trimming were not collected; that limitation does not require
shipping the candidate or another campaign.

The final change retains only useful diagnostic support: explicit AOT build
identity, bounded larger payloads, opt-in idle snapshots, allocation/capacity
accounting and real HTTP/2 lifetime tests. It does not enable the experimental
pooled batch owner or add a production tuning knob.

## Nonclaims

Finite emulator windows do not establish real-Azure or sustained capacity.
Sampled maxima can miss peaks; process-wide memory is not exact pool attribution.
Stage wall time is not CPU, overlapping sums are not request latency, and
percentiles are never subtracted to fabricate internal costs. Caller-item
acknowledgement is observed at the batch response, not individual backend
completion. Instrumentation overhead is shared but not absent.

No response suppression or concurrency change is included. The keep-ten
conclusion remains intact. #1024's original missing-arrival failures and residual
transport attribution remain unresolved; this ownership experiment does not
explain them.
