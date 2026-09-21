# Isolated BatchWriteItem diagnostic

This report-only experiment is a measurement increment for
[#1024](https://github.com/pedrosakuma/aws2azure/issues/1024). It does not
change BatchWriteItem behavior, authoritative performance baselines, or
qualification verdicts. It is not a sustained-capacity benchmark.

## Run a bounded cell

Use the `batch-write-experiment` GitHub Actions workflow. It runs cells
serially on one fresh hosted runner, with a new disposable Cosmos emulator,
database and proxy per cell. It provisions **no Azure resources**.
The workflow accepts one to four space-separated cells, each run against
both a source-pinned baseline and candidate (two to eight serial windows):

```text
25:8:put:distinct:proxy 25:8:put:distinct:direct
```

The five fields are batch size (`1`, `5`, `10`, `25`), external concurrency
(`1`, `2`, `5`, `8`), operation mix (`put`, `delete`, `mixed`), partition
distribution (`shared`, `distinct`) and route (`proxy`, `direct`).
Mixed cells alternate deletes and puts, starting with a delete; a one-item
mixed cell is therefore a delete. Each item has a string partition key,
string sort key and, for puts, a 256-character ASCII payload. Distinct
distribution assigns a different partition to every item; shared
distribution places all items in one partition with distinct sort keys.

Start with the paired 25-item, concurrency-8 put cells. Then vary one axis
at a time, including concurrency 1, batch sizes 1/5/10 and delete/mixed
cells. Do not run the full 192-cell matrix automatically. Repeat selected
pairs in reversed order when investigating order effects.

To run locally, first reserve an otherwise idle host. Do not set the
acknowledgement on a shared host with unrelated workloads:

```bash
dotnet build tests/Aws2Azure.PerfTests -c Release
bash eng/publish-batch-runtime.sh HEAD artifacts/batch-runtimes/local
AWS2AZURE_BATCH_RUNTIME="$PWD/artifacts/batch-runtimes/local" \
AWS2AZURE_BATCH_EXPERIMENT=1 \
AWS2AZURE_BATCH_EXCLUSIVE_HOST=1 \
AWS2AZURE_BATCH_CELL=25:8:put:distinct:proxy \
AWS2AZURE_BATCH_OUTPUT="$PWD/artifacts/batch-write" \
dotnet test tests/Aws2Azure.PerfTests -c Release --no-build \
  --filter 'FullyQualifiedName~BatchWriteExperimentTests.Selected_cell_records_bounded_diagnostic'
```

The dedicated opt-in is independent of `AWS2AZURE_PERF`. Without it,
the test skips before starting infrastructure. The xUnit collection disables
parallel execution, but cannot prevent unrelated processes outside the
runner; host isolation remains an explicit operational prerequisite.

## Bounds and accounting

- Four successful serial warmup batches, with separate single-use inventory.
- At most 128 measured batches, five seconds of dispatch, and a 30-second
  drain allowance. Each batch gets at most five submissions including
  retries of only the unprocessed subset.
- At most 3,300 item names per cell. Delete inventory is seeded before
  measurement; puts use fresh names. No in-window replenishment.
- Backoff is 25/50/100/200 milliseconds between unsuccessful submissions.
  AWS SDK automatic retries are disabled. Shared Cosmos transport retries
  remain active and are observable as repeated backend item attempts.
- Setup has a five-minute deadline; the paired CI job has a 60-minute outer limit.
  Cleanup stops the proxy/relay and removes the disposable emulator.
- Reports are uniquely named, bounded JSON sidecars, including failed runs.
  Hard process termination can prevent reporting/cleanup; hosted runner
  disposal is the final infrastructure boundary.

`completedBatches` means all items were acknowledged; `acknowledgedItems`
counts partial successes even when the batch ultimately fails. Both rates
use the same measurement-through-drain denominator. Unprocessed item
occurrences are counted separately from terminal batch failures.
Batch latency includes SDK serialization, proxy work, retries and backoff.
`itemAcknowledgementLatencyMs` records **caller-visible acknowledgement**:
time from entering the initial submission loop to the response confirming
each item, including earlier submissions and backoff for that item. Only
newly acknowledged items are recorded, once; failed/unresolved items are
not silently counted as successes. Items acknowledged by one response share
its timestamp. This is meaningful client-observed completion, not a claim
to know when each backend item committed. For the direct reference this
also waits for its per-batch `WhenAll`, rather than giving direct items a
more favorable timestamp than AWS callers can observe.
Inventory exhaustion may end dispatch early: this is a finite-window
diagnostic, not evidence of sustained capacity.

## REST reference and observer

Both routes traverse a test-only loopback relay. It records write-attempt
latency through the complete Cosmos response body, request/response body
bytes, status counts, repeated item attempts and reported RU. Missing RU
headers are counted, not converted into a zero-cost claim.

The direct route uses the same Cosmos REST transport, key codec and document
encoder as the proxy and the current per-batch concurrency limit of 10.
It bypasses AWS HTTP/SigV4 and the batch handler. This is a transport reference,
not an independent Cosmos SDK baseline. The relay buffers responses and
adds overhead to both paths; these timings must not be represented as
uninstrumented production latency. Do not subtract their p99 values to
derive a per-stage duration.

## Internal attribution and resource scope

`AWS2AZURE_BATCH_DIAGNOSTICS=1` opts the proxy into a single histogram on the
existing `Aws2Azure.Proxy` meter, exported through the existing Prometheus
endpoint. It has nine fixed stage values, no table/key/request identifiers,
credentials or payloads. Disabled scopes do not allocate or read the clock;
no background thread, queue, per-item retained record or new dependency is
introduced. Enabled exporter storage is bounded by nine histogram series.
The harness enables this only in its child proxy, not globally.

| Stage | Actual timed region |
| --- | --- |
| `envelope_parse` | Request deserialization, including envelope byte-range capture |
| `entry_parse` | Individual action `JsonDocument.Parse` |
| `metadata_read` | Table metadata access, including any cache miss/backend work |
| `put_validation` | Item-shape validation, key validation/encoding and duplicate guard |
| `delete_validation` | Delete-key validation/encoding, duplicate guard and work-list insertion |
| `document_encode` | TTL/index-order preparation and Cosmos document serialization |
| `semaphore_wait` | Individual item `WaitAsync`, including canceled waits |
| `item_downstream` | Work after acquiring the semaphore: headers, transport/retries, response classification/disposal |
| `response_write` | Successful response serialization **and async response write**, not serialization CPU alone |

These are wall-clock scopes, not an exhaustive partition of handler time.
Envelope/table checks, work-list setup, unprocessed response assembly,
SigV4 and outer middleware are not separately timed. Validation-error paths
can include writing their error response. Item scopes overlap under fan-out,
so sums can exceed batch wall time. Response serialization is deliberately
not misrepresented as CPU time by subtracting network percentiles.

Before/after scrapes exclude setup/warmup from cumulative stage **sum/count
deltas** and yield an arithmetic mean, not fabricated percentile differences.
Scrape boundaries are process-level brackets, not an atomic per-request
trace. A baseline without instrumentation, a stage not exercised during
warmup, or an unavailable scrape produces null/missing metrics, never zero.
Direct-route stage results are not applicable; its proxy only performs setup.

Resource fields likewise have explicit scopes:

- `proxyProcess`: CPU seconds, working set and thread endpoints for the
  **actual published proxy PID**, not a `dotnet run` build/launcher process.
- `driverProcess`: CPU, approximate managed allocation and GC deltas for the
  test process, including SDK/direct transport, relay, xUnit and diagnostics.
  This is not a pure direct-client CPU measurement.
- `proxyAllocatedBytes` / `proxyGen2Collections`: existing proxy runtime
  gauges differenced across two scrapes. Scrapes themselves add overhead.
- Working sets are endpoints. `lifetimePeakWorkingSetBytes` is an OS
  process-lifetime high-water mark, **not** the measurement-window peak.
- Workflow artifacts include host CPU topology, Docker information,
  process/container snapshots and Linux CPU/memory/IO pressure endpoints.
  They describe the runner, not an assertion of backend saturation.

Backend CPU/allocation, window peak memory, sampled CPU stacks, and separate
transport retry/backoff timings remain unavailable. The relay still observes
REST attempts/RU/429s and repeated item attempts; the driver measures its own
resubmission backoff. Zero terminal failures does not imply zero retries.

## Source-pinned paired execution

`eng/publish-batch-runtime.sh` resolves a revision to a full commit, checks it
out into a detached project-relative worktree, and publishes a self-contained
Linux x64 **JIT** apphost (not Native AOT) before measurement. Its manifest
records the commit and SHA-256 of every published file: executable, managed
dependencies, native/runtime libraries and configuration. The harness verifies
the exact file inventory and hashes before launching that executable directly.
It never labels current-source `dotnet run` as a historical revision.

The workflow resolves `baseline` (default `origin/main`) and candidate HEAD
once, publishes both with the same installed SDK, and records build SDK info.
One committed harness drives both runtimes. `harnessSource` is separate from
`runtimeIdentity.Commit`; the direct route always uses that harness's
transport/encoder, even when the idle setup proxy is the baseline. Changing
the production encoder later therefore requires checking reference equivalence.
Self-contained runtime libraries are included in hashes; OS libraries/kernel
are runner dependencies, not claimed as sealed application files.

The emulator tag is resolved to a repository digest once, reused across all
cells and recorded alongside the actual container image ID. Main runs before
candidate; default cells are the paired 25:8 puts through proxy/direct. These
short serial observations remain susceptible to order/emulator variation.
For an order-effect investigation, explicitly dispatch with the two source
roles reversed rather than treating a retry-until-green as evidence.
Instrumentation is enabled where supported; comparisons with uninstrumented
old main include that overhead and cannot establish an optimized cost floor.

## Evidence and remaining work

The workflow uploads its reports and host/process snapshots, without writing
`baseline-latest` or feeding emulator qualification. Dynamic diagnostic
cells are not `PerfResult` regression scenarios and carry no threshold or
promotion claim. Existing regression scenarios remain unchanged.

Foundation [run 35608538449](https://github.com/pedrosakuma/aws2azure/actions/runs/35608538449)
provided bounded emulator observations, not statistical CPU-bottleneck proof.
Its small delete/mixed cells exhausted inventory early; they are short-burst
correctness evidence, not sustained capacity.

This increment provides paired execution and stage/process attribution,
not closure of all #1024 acceptance. Broader staged matrix evidence, separate
transport retry/backoff attribution, window resource sampling and a reviewed
interpretation of the paired campaign remain before selecting a production
optimization. It changes no batch concurrency, behavior, thresholds or
qualification authority. Do not compare unrelated historical runs as a
controlled A/B or claim #519 established an irreducible optimized floor.
Real-Azure experiments require separate, finite authorization and cleanup.
