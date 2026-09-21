# BatchWriteItem retained-body ownership

Measured on **2026-09-21** for [#1034](https://github.com/pedrosakuma/aws2azure/issues/1034),
comparing the merged scratch-only baseline
`f455352e19e19880f5b20bdb3a21f4b9508ad9be` with retaining each encoded body
through asynchronous dispatch. This is not a comparison against the older
fresh-scratch implementation. Concurrency remains ten; no protocol, threshold,
gap status, authentication or shared transport change is proposed.

The source-pinned candidate is
`5a4037a104912052e625409f040190daede6cb29`. Selection remains contingent on the
Native AOT resource/performance comparison below. No local throughput result is
used: the local machine is shared.

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

`BatchWriteOwnershipTests` observes actual shared-pool rentals/returns, poisons
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

Results and the implementation decision will be recorded after inspecting the
complete campaign artifacts. No Azure resources were provisioned.

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
