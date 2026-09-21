# Isolated BatchWriteItem diagnostic

This report-only experiment is the first measurement increment for
[#1024](https://github.com/pedrosakuma/aws2azure/issues/1024). It does not
change BatchWriteItem behavior, authoritative performance baselines, or
qualification verdicts. It is not a sustained-capacity benchmark.

## Run a bounded cell

Use the `batch-write-experiment` GitHub Actions workflow. It runs cells
serially on one fresh hosted runner, with a new disposable Cosmos emulator,
database and proxy per cell. It provisions **no Azure resources**.
The workflow accepts one to four space-separated cells:

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
- Setup has a five-minute deadline; the CI job has a 30-minute outer limit.
  Cleanup stops the proxy/relay and removes the disposable emulator.
- Reports are uniquely named, bounded JSON sidecars, including failed runs.
  Hard process termination can prevent reporting/cleanup; hosted runner
  disposal is the final infrastructure boundary.

`completedBatches` means all items were acknowledged; `acknowledgedItems`
counts partial successes even when the batch ultimately fails. Both rates
use the same measurement-through-drain denominator. Unprocessed item
occurrences are counted separately from terminal batch failures.
Batch latency includes SDK serialization, proxy work, retries and backoff.
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

Reports identify the source commit, a hash over built runtime DLL/JSON
files, resolved emulator image ID, host/runtime, and explicit unavailability
of internal parsing/serialization time, proxy semaphore wait, end-to-end
per-item latency and CPU attribution. Backend REST-attempt latency is
**not** per-item end-to-end latency. Memory snapshots are proxy process
endpoints, not peaks; direct-route snapshots describe an idle proxy, not
the direct client process.

## Evidence and remaining work

The workflow uploads its reports and host/process snapshots, without writing
`baseline-latest` or feeding emulator qualification. Dynamic diagnostic
cells are not `PerfResult` regression scenarios and carry no threshold or
promotion claim. Existing regression scenarios remain unchanged.

Compare candidate and main using the same committed harness, cell, backend
image, dependencies and runner resources. Record both runtime hashes and
source identities; do not compare unrelated historical runs as a controlled
A/B. This first increment does not yet orchestrate that source-pinned pair.
Internal timing/CPU attribution and a controlled main/candidate campaign
remain necessary before closing #1024 or selecting a production optimization.
Real-Azure experiments require separate, finite authorization and cleanup.
