# BatchWriteItem concurrency investigation

This bounded investigation addresses
[#1026](https://github.com/pedrosakuma/aws2azure/issues/1026), using the
[corrected isolated harness](batch-write-experiment.md). It does not assume
that increasing concurrency helps or that an emulator establishes a universal
production optimum. The residual original #1024 missing-arrival cause remains
unproven; no result here retrospectively attributes it to the 204 relay defect.

## Deliberate comparison

The initial candidate is **five**, compared with the current **ten** concurrent
item operations per batch. The variant changes only the existing private
constant: no public configuration knob, cross-request/global lock, new queue,
account/binding coupling, or transport policy is introduced.

| Runtime | Internal limit | Source commit |
| --- | ---: | --- |
| Main | 10 | `7d50c85b6a43ecd7e413ce142cbcac82b99e75b8` |
| Experimental variant | 5 | `2b341a89d5e210bdab4825d17cb4b0064374bfac` |

Both builds are source-pinned self-contained **JIT**, not Native AOT. Their
complete executable/dependency manifests are verified before each window.
The emulator digest is fixed to
`mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator@sha256:2db1f9e74c506bcf6fc347aa937aea1c00fa756061296a5a9efba530ce86ec02`.

Four proxy-route cells isolate low and high external concurrency, with a
mixed/delete control rather than just successful puts:

- `25:1:put:distinct:proxy`
- `25:8:put:distinct:proxy`
- `25:1:mixed:shared:proxy`
- `25:8:mixed:shared:proxy`

One dedicated runner executes **10 → 5 → 5 → 10**, reusing the exact published
file sets but giving every cell fresh processes, emulator/database and
single-use inventory. This produces two observations per runtime/cell,
sixteen finite windows total—not a full matrix or statistical proof.
All windows use four warmup batches, at most 128 measured batches, five-second
dispatch and a 30-second drain allowance. Inventory exhaustion must be labeled
short-burst; no replenishment is hidden inside measurement. Stage instrumentation,
relay boundaries and 250ms resource sampling are enabled identically.

Reproduction command, with the committed harness branch/revision recorded in
the run's artifacts:

```bash
gh workflow run batch-write-experiment.yml --ref perf/batch-concurrency-1026 \
  -f baseline=7d50c85b6a43ecd7e413ce142cbcac82b99e75b8 \
  -f candidate=2b341a89d5e210bdab4825d17cb4b0064374bfac \
  -f runtime_slots=balanced \
  -f cells='25:1:put:distinct:proxy 25:8:put:distinct:proxy 25:1:mixed:shared:proxy 25:8:mixed:shared:proxy'
```

## Decision constraints

Interpretation requires clean relay boundary counts, successful cleanup and
explicit treatment of failures, 429s, repeated attempts and unprocessed work.
A failure stops further repetition and must be compared with main; it is not
erased by retrying the campaign.

Compare acknowledged items/s, batch and caller-item p95/p99, actual semaphore
wait/downstream scopes, RU, process CPU/allocation/GC and sampled memory.
Concurrent stage sums are not additive request latency. CPU/allocation are
process-wide brackets including diagnostic work; sampled memory maxima can
miss peaks. Two repetitions reduce a simple order confound but do not establish
confidence intervals, sustainable capacity, or an irreducible cost floor.

Five must not be selected merely for improving one high-concurrency cell:
low-concurrency behavior and the mixed control matter. If tradeoffs, dispersion
or missing evidence prevent a defensible default change, **keep ten** rather
than add configuration or a shared limiter. A promising JIT result alone is
insufficient to change shipping defaults; any selected implementation needs
relevant Native AOT evidence. No threshold or qualification waiver is changed.

## Result: retain ten

Run [35634418172](https://github.com/pedrosakuma/aws2azure/actions/runs/35634418172)
completed all sixteen predeclared windows without a diagnostic rerun. The
recommendation is **keep the existing limit ten**, not that ten is optimal.
The experimental constant is restored; the final shipping-source diff is empty.
No admission mechanism, configuration, threshold or batch semantics change is
justified by this evidence.

Evidence is from
`tests/Aws2Azure.PerfTests/DynamoDb/BatchWriteExperimentTests.cs`,
`Selected_cell_records_bounded_diagnostic`, on the four cells above:

- Harness: `be0e3f6bc8324271a97ee84cee3106cdf24bd080`.
- Artifact: [`batch-write-diagnostic`, ID 10656371123](https://github.com/pedrosakuma/aws2azure/actions/runs/35634418172/artifacts/10656371123),
  digest `sha256:f5f189567ab3c4cd2b554b236725a6565f70ef2e0dda753aab9bd9d2e4c3ce40`.
- Actual emulator image ID:
  `sha256:77f8a39f5c39e0b7878c8d9cddd70ff08872695dec9b85d2055b38d5d58d5302`.
- Each slot's complete 354-file manifest is identical across its eight
  windows. The apphost SHA256 is
  `dad4d253479ee7dceb3e39546791317a60b1bd95028baa4dc9c0d437f35c6003`
  in both slots; this alone does **not** identify the managed implementation.
  `Aws2Azure.Modules.DynamoDb.dll` hashes are
  `eb6c28d377248e091d756343f3b056d938dd5565409640b41086216362811714`
  (ten) and
  `d51ed60693a25699afe3b8964cc19e15b93acbd19480de0d68860f1516f55e73`
  (five). Every report carries the full executable/dependency manifest.

Retrieve the reports without running another experiment:

```bash
gh run download 35634418172 -n batch-write-diagnostic \
  -D .tools/concurrency-evidence/run-35634418172
```

### Throughput and acknowledgement latency

`P1/P8` mean distinct-partition puts at external concurrency one/eight;
`M1/M8` mean shared-partition mixed writes at one/eight. All batches contain
25 items. Blocks 1/4 use ten; blocks 2/3 use five. Values are individual
window observations, not pooled or averaged percentiles. Throughput includes
drain (`acknowledgedItems / settledSeconds`); batches/s equals items/s divided
by 25 here. No window exhausted the 128-batch inventory.

| Block (limit) | Cell | Batches | Settled s | Items/s | Batch p95 / p99 ms | Item p95 / p99 ms |
| --- | --- | ---: | ---: | ---: | --- | --- |
| 1 (10) | P1 | 60 | 5.021 | 298.8 | 105.5 / 109.4 | 105.5 / 109.4 |
| 1 (10) | P8 | 67 | 5.421 | 309.0 | 827.0 / 974.1 | 827.0 / 974.1 |
| 1 (10) | M1 | 87 | 5.022 | 433.1 | 74.8 / 102.8 | 74.8 / 102.8 |
| 1 (10) | M8 | 96 | 5.279 | 454.7 | 504.9 / 519.4 | 504.9 / 519.4 |
| 2 (5) | P1 | 62 | 5.054 | 306.7 | 102.2 / 131.2 | 102.2 / 131.1 |
| 2 (5) | P8 | 64 | 5.207 | 307.3 | 739.9 / 760.1 | 739.9 / 760.1 |
| 2 (5) | M1 | 87 | 5.051 | 430.6 | 83.7 / 153.6 | 83.7 / 153.5 |
| 2 (5) | M8 | 99 | 5.304 | 466.6 | 529.0 / 609.6 | 528.9 / 609.5 |
| 3 (5) | P1 | 61 | 5.048 | 302.1 | 112.2 / 122.8 | 112.2 / 122.8 |
| 3 (5) | P8 | 65 | 5.154 | 315.3 | 724.7 / 731.5 | 724.7 / 731.4 |
| 3 (5) | M1 | 93 | 5.036 | 461.7 | 76.6 / 84.9 | 76.6 / 84.9 |
| 3 (5) | M8 | 96 | 5.323 | 450.9 | 511.2 / 535.4 | 511.2 / 535.4 |
| 4 (10) | P1 | 60 | 5.028 | 298.4 | 113.2 / 129.2 | 113.1 / 129.2 |
| 4 (10) | P8 | 67 | 5.290 | 316.7 | 732.0 / 735.4 | 732.0 / 735.4 |
| 4 (10) | M1 | 88 | 5.056 | 435.1 | 73.3 / 77.4 | 73.2 / 77.4 |
| 4 (10) | M8 | 92 | 5.159 | 445.8 | 565.8 / 688.3 | 565.8 / 688.3 |

Item latency is caller acknowledgement, not independent backend item
completion: these successful single-submission batches acknowledge their
25 items together. Small differences from batch latency reflect the separately
instrumented start/end boundaries, not an internal stage estimate.

### Observed stages and resources

Wait/downstream are actual per-item stage means, in milliseconds—not differences
between percentiles. CPU is process-wide seconds over its measurement bracket.
Allocation is the proxy's exported allocated-byte delta divided by acknowledged
items (KiB/item). WS is the maximum **sampled** working set (MiB), not a true
window peak. Driver CPU/WS includes the relay, SDK, diagnostics and test host;
it is not shipping sidecar usage or backend attribution.

| Block | Cell | Wait / downstream ms | Proxy / driver CPU s | Proxy KiB/item | Proxy / driver sampled WS MiB |
| --- | --- | --- | --- | ---: | --- |
| 1 | P1 | 16.50 / 27.00 | 2.65 / 3.40 | 18.67 | 93.4 / 174.7 |
| 1 | P8 | 153.83 / 199.01 | 3.36 / 3.85 | 19.35 | 108.2 / 174.3 |
| 1 | M1 | 11.20 / 18.43 | 3.14 / 3.17 | 16.23 | 95.0 / 173.7 |
| 1 | M8 | 103.35 / 144.14 | 3.64 / 3.45 | 16.67 | 109.5 / 180.4 |
| 2 | P1 | 27.33 / 14.63 | 2.99 / 3.10 | 18.80 | 94.3 / 173.5 |
| 2 | P8 | 244.18 / 125.72 | 3.73 / 3.60 | 19.10 | 107.1 / 184.9 |
| 2 | M1 | 19.77 / 10.40 | 3.21 / 3.10 | 16.29 | 97.4 / 178.7 |
| 2 | M8 | 139.21 / 71.78 | 3.34 / 3.27 | 16.44 | 106.1 / 191.4 |
| 3 | P1 | 28.32 / 14.87 | 3.05 / 3.21 | 18.81 | 94.6 / 170.4 |
| 3 | P8 | 237.87 / 121.29 | 2.81 / 3.79 | 19.05 | 105.4 / 185.8 |
| 3 | M1 | 17.96 / 9.64 | 2.88 / 3.17 | 16.27 | 94.0 / 177.1 |
| 3 | M8 | 164.16 / 84.76 | 3.71 / 3.44 | 16.47 | 106.3 / 188.4 |
| 4 | P1 | 16.40 / 26.95 | 2.92 / 3.43 | 18.73 | 94.3 / 183.2 |
| 4 | P8 | 155.64 / 204.99 | 3.09 / 3.84 | 19.39 | 105.5 / 189.2 |
| 4 | M1 | 10.72 / 18.31 | 3.32 / 3.04 | 16.23 | 94.9 / 172.9 |
| 4 | M8 | 105.06 / 136.20 | 3.63 / 3.43 | 16.73 | 108.3 / 180.4 |

The proxy's exported Gen2 delta is zero in every window. Proxy Gen0/Gen1,
backend CPU/allocation and transport-internal backoff duration are unavailable,
not zero. Full reports retain driver GC/allocation, bracket timestamps,
per-stage sums/counts, resource samples and host-pressure snapshots.

### Correctness and interpretation

Every window has identical write counts at arrival, dispatch, response headers,
body completion and response-write completion, equal to acknowledged items.
All failed/cancelled/dropped/active attempts, repeated dispatches, 429s,
non-success backend responses, client resubmissions, unprocessed occurrences,
unacknowledged requests and unresolved batches are zero. Cleanup and final
snapshots completed throughout; no resource samples were dropped or failed.
Emulator-reported RU is exactly one per acknowledged write in these windows,
with no missing RU headers. That is not real-Azure RU cost or throttling evidence.

Five consistently lowers downstream wall time while increasing semaphore
wait. That demonstrates a redistribution of observed waiting, **not** a proven
net latency or CPU reduction. P8 throughput is almost unchanged: both five
observations are slightly below their adjacent ten reference (blocks 2 versus
1, and 3 versus 4). Its apparent p99 gain against block 1 mostly disappears
against block 4. M8 tail direction changes across pairs, while M1 p95 is higher
with five in both pairs. CPU and memory do not show an across-cell, repeated
resource improvement sufficient to override these tradeoffs.

Consequently there is no demonstrated cross-request overload requiring a
backend-scoped admission queue, no supported reason to try two or sixteen in
this increment, and no repeatable improvement warranting an AOT tuning campaign
or a shipping change. This is a negative selection result, **not** proof that
cross-request overload cannot occur. A future deployment-specific hypothesis
would need its own bounded evidence and relevant Native AOT measurements.

Only maximum-size batches were compared here; smaller batch sizes were covered
by the prior measurement campaign, not a paired internal-limit comparison.
Five-second windows, two observations, JIT warmup/tiering, fixed cell ordering,
instrumentation, emulator topology and a single runner limit inference. There
is no sustained-capacity, statistical-significance, production-AOT performance,
real-Azure, universal-optimum or historical-failure-causality claim.

For #1026, this resolves the bounded **keep-current-limit** decision allowed by
the issue. The conditional admission-design and improvement criteria do not
justify inventing an implementation when no improvement was selected. Existing
focused BatchWriteItem functional tests are run against both the temporary
five variant and the restored ten; shipping semantics and canonical gap
documentation are unchanged. The reusable regression additions cover execution
block validation and rejection of incomplete/faulted observation snapshots.
Residual #1024 attribution and real-Azure questions remain open; no new
transport-core instrumentation or billed gate is needed for this decision.
