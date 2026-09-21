# BatchWriteItem representative campaign

This is report-only evidence for [#1024](https://github.com/pedrosakuma/aws2azure/issues/1024),
following the [bounded harness and attribution guide](batch-write-experiment.md).
It does not alter production batch concurrency, retries, qualification or
performance thresholds. It is not a Native AOT or real-Azure capacity study.

## Design and reproducibility

The campaign uses `BatchWriteExperimentTests.Selected_cell_records_bounded_diagnostic`
on a dedicated GitHub-hosted runner. Setup, four successful warmups, measured
dispatch/drain and cleanup are separate per cell. Each cell starts a fresh
proxy process, relay, disposable emulator/database and single-use inventory.
Bounds remain 128 measured batches, five seconds of dispatch, a 30-second
drain allowance and five submissions per batch. Early inventory exhaustion
means a **short-burst observation**, not a five-second capacity measurement.

Two planned stages cover eight workload combinations, each through both
proxy and equivalent direct REST. The same two source-pinned runtime slots
repeat the entire sequence serially (32 bounded windows, not 192 grid cells):

| Stage | Batch size | External concurrency | Kind | Partition distribution |
| --- | ---: | ---: | --- | --- |
| Primary concurrency | 25 | 1, 2, 5, 8 | put | distinct |
| Remaining axes | 1 | 1 | delete | shared |
| Remaining axes | 5 | 2 | mixed | distinct |
| Remaining axes | 10 | 5 | put | shared |
| Remaining axes | 25 | 8 | mixed | shared |

Puts carry 256 ASCII payload characters plus string hash/range keys and
translation metadata. Deletes are acknowledged seeded existing items.
Mixed cells alternate delete/put, starting with delete. Keys use the shipping
hex codec, not raw Cosmos routing values. The direct route uses the harness's
shipping encoder/transport and the existing per-batch limit of ten, bypassing
AWS/SigV4/handler work. Both routes include relay observation overhead.

The planned runtime is fixed to merged attribution source
`bd89968772af815ad847845159075d2d1521f703` in **both slots**. This is deliberate
A/A repetition, not a claimed candidate optimization. The run stopped before
the second slot, so no new repeated-slot comparison is claimed. The earlier controlled
main/candidate instrumentation comparison remains available in
[run 35613207145](https://github.com/pedrosakuma/aws2azure/actions/runs/35613207145)
and [its evidence](https://github.com/pedrosakuma/aws2azure/pull/1029#issuecomment-5762366159).
Both new slots enable `AWS2AZURE_BATCH_DIAGNOSTICS=1`. Self-contained JIT
executables and every published dependency/configuration file are SHA-256
verified; runtime identity remains distinct from harness source identity.

The emulator repository digest is
`mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator@sha256:2db1f9e74c506bcf6fc347aa937aea1c00fa756061296a5a9efba530ce86ec02`.
No local shared-host performance run or billed Azure provisioning is allowed.

Reproduce the bounded campaign using the reviewed harness revision:

```bash
gh workflow run batch-write-experiment.yml --ref perf/batch-campaign-1024 \
  -f baseline=bd89968772af815ad847845159075d2d1521f703 \
  -f candidate=bd89968772af815ad847845159075d2d1521f703 \
  -f cells='25:1:put:distinct:proxy 25:1:put:distinct:direct 25:2:put:distinct:proxy 25:2:put:distinct:direct 25:5:put:distinct:proxy 25:5:put:distinct:direct 25:8:put:distinct:proxy 25:8:put:distinct:direct 1:1:delete:shared:proxy 1:1:delete:shared:direct 5:2:mixed:distinct:proxy 5:2:mixed:distinct:direct 10:5:put:shared:proxy 10:5:put:shared:direct 25:8:mixed:shared:proxy 25:8:mixed:shared:direct'
```

## Scope of attribution

Stage sums/counts measure actual parse, key validation, encoding, semaphore
wait and downstream regions. Concurrent item sums overlap and must not be
added into request latency or derived by subtracting percentiles. Item latency
is caller-visible acknowledgement, including earlier submissions and backoff,
not unknowable individual backend commit time.

CPU/allocation/GC are process-scoped. The driver includes relay, SDK/direct
transport, xUnit and sampling. New 250ms process sampling supplies window
observations with monotonic timestamps and bounded storage; observed memory
maxima can miss spikes. Host process/container snapshots and pressure endpoints
provide context, not proof that an emulator/host resource is the bottleneck.

Transport internals still do not export retry/backoff duration. The relay
records write attempts **only after reading a complete backend response**,
including repeated identities and 429 responses;
the driver records actual resubmission backoff. Those are separate quantities.
`AzureHttpClient` passes 429 through without its own retry; its internal retry
loop handles selected server/transport failures. Instrumenting that loop would
touch `src/Aws2Azure.Core/Azure/AzureHttpClient.cs`, mechanically requiring
`run-integration`, `run-perf` and **`run-real-azure`**. This campaign does not
bypass that requirement or authorize billed resources. Internal backoff timing
remains explicitly unavailable, even if all observed cells have zero retries.

## Recorded result and acceptance reconciliation

[Run 35618887025](https://github.com/pedrosakuma/aws2azure/actions/runs/35618887025),
artifact **`batch-write-diagnostic`**, used harness commit
`615fd3609ec830249cc5afc6c15ed11abc481020` on a four-vCPU AMD EPYC 7763 runner.
The workflow **failed**, preserving 14 successful windows and one failed
window; the remaining 17 planned windows were not executed. It was not rerun.
All 15 reports attest successful cleanup and the same measured runtime
354-file manifest and image ID
`sha256:77f8a39f5c39e0b7878c8d9cddd70ff08872695dec9b85d2055b38d5d58d5302`.
The measured DynamoDB DLL SHA-256 was
`f2b84430e7b312f7678f8b33979afa9ab68d8ee287e038e282759b21fb676f31`.
The separately published, unused candidate-slot manifest differs despite the
same source commit; source equality is not asserted to mean binary equality.

### Primary 25-item distinct-partition puts

These eight windows dispatched for five seconds and settled in
5.0562–5.3742 seconds. Rates include drain. In each paired rate below,
the first value is **batches/s**, the second **acknowledged items/s**.
These are individual finite emulator observations, not confidence intervals.

| External concurrency | Proxy rates | Direct rates | Proxy batch p99 ms | Direct batch p99 ms | Proxy mean item semaphore wait ms | Proxy mean item downstream ms |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 11.849 / 296.21 | 14.438 / 360.94 | 144.715 | 92.370 | 16.770 | 27.106 |
| 2 | 12.898 / 322.44 | 14.912 / 372.80 | 214.024 | 162.369 | 31.854 | 49.781 |
| 5 | 13.133 / 328.33 | 14.568 / 364.21 | 487.600 | 403.653 | 87.190 | 123.785 |
| 8 | 12.955 / 323.89 | 14.886 / 372.15 | 902.932 | 584.218 | 148.501 | 190.882 |

Proxy completed-batch counts were 60/66/69/68, direct 73/76/75/80.
The matching proxy caller-item acknowledgement p99 values were
144.695/213.995/487.574/902.898ms; direct values were
92.353/162.350/403.617/584.192ms. Same-response items share a timestamp,
so these are not independent item commit observations.

Proxy CPU brackets were 3.13/2.95/3.01/3.04 CPU-seconds. Approximate proxy
allocation divided by acknowledged items was 19,192/19,199/19,443/19,758
bytes/item, including instrumentation and process-wide background work.
Sampled proxy working-set maxima were 94.8/94.5/101.9/102.9MiB.
Samples numbered 22–23 per window, with no dropped/failed captures.
Proxy gen2 deltas were zero. Put backend response bodies were **609 bytes/item**
in all four cells; this measures bytes, not proof that response suppression
is supported or worthwhile.

### Remaining balanced axes: short bursts only

All six windows below exhausted their 128-batch inventory before five seconds.
Do not rank their rates as sustained capacities.

| Cell (size:concurrency:kind:partitions) | Proxy/direct settled seconds | Proxy batches/s; items/s | Direct batches/s; items/s | Proxy/direct batch p99 ms |
| --- | ---: | ---: | ---: | ---: |
| `1:1:delete:shared` | 0.4026 / 0.2871 | 317.942; 317.94 | 445.899; 445.90 | 8.608 / 4.626 |
| `5:2:mixed:distinct` | 1.5826 / 1.1571 | 80.882; 404.41 | 110.619; 553.10 | 177.200 / 118.859 |
| `10:5:put:shared` | 3.9373 / 3.5501 | 32.510; 325.10 | 36.055; 360.55 | 219.834 / 187.203 |

Only 3–17 resource samples were available per short window. All 14 successful
windows reported zero failed batches, caller resubmissions, recorded 429s,
repeated completed-response identities and missing RU headers. This does
**not** prove the absence of unobserved transport failures.

### Failure that stops closure

`25:8:mixed:shared:proxy` on the **pinned merged main runtime** returned
`InternalServerErrorException: Service Unavailable`: 97 initial requests,
96 completed batches, one failed batch, 2,400 acknowledged items, no caller
resubmission and one request without acknowledgement. The report is
`batch-write/5dc93f4fcbf14f309b2cc5712f841a9a.json`.
The relay recorded 2,424 completed backend write responses for 2,425 submitted
items, with zero recorded non-success responses/repeated identities/429s.
Its completed-response-only accounting cannot localize the missing write:
failure before the relay, transport/body-read failure and internal behavior
are not distinguished. The evidence does **not** justify calling this a flake,
declaring a candidate regression, or assuming all 25 writes in the failed
batch were unapplied. No throughput promotion is based on this cell.
Cleanup succeeded; 22 resource samples survived, with no dropped/failed
captures. End-of-window CPU/allocation/stage deltas were unavailable on the
exception path; periodic samples must not be presented as those missing deltas.

| #1024 acceptance | Evidence / precise remaining limitation |
| --- | --- |
| Isolated bounded phases | Dedicated runner, fresh processes/state per cell, capped inventory/time/storage, cleanup receipts; no unrelated benchmarks launched |
| Staged requested axes | All sizes 1/5/10/25, external concurrency 1/2/5/8, puts/deletes/mixed and both distributions covered by seven successful balanced proxy/direct pairs |
| Counts and latency | Separate batch/item rates, initial/resubmission requests, unacknowledged requests, caller-item/batch distributions and preserved failure; cannot infer backend commit outcomes for failed batches |
| Attribution/resources | Actual stage scopes, observed completed REST responses/RU/429, caller backoff, process CPU/allocation/GC and bounded window samples; internal transport retry/backoff and incomplete attempts remain unavailable |
| Matching references/identity | All measured cells use one exact manifest/image and one harness; previous #1029 main/candidate comparison remains separate; new second-slot repetition and mixed-25 direct reference did not run |
| Deterministic guardrails | Accounting, missing metrics, sample ordering/caps, tamper rejection and existing qualification/threshold preservation tested; dynamic report-only cells do not enter PerfReport |
| Published limitations / Azure plan | Run/artifact/commit IDs above, explicit failed evidence and approval-only finite plan below; no Azure production/capacity claim |

### Directions supported for subsequent investigation

For **#1026**, higher external concurrency here increases semaphore and
downstream wall time substantially while acknowledged throughput changes
little above concurrency two; direct-reference throughput also stays in a
narrow band. That supports a small controlled **internal-limit** sweep,
including the current ten, not an immediate increase to 25 or an assumption
that a global limiter is correct. It does not distinguish emulator limits,
shared host resources, transport effects or proxy bottlenecks. No fairness,
backend/binding scope, or production-optimal limit has been demonstrated.

For **#1027**, response bytes and process allocation provide measurable targets,
but no allocation stack establishes which object family is material. In the
concurrency-eight put cell, mean per-item parse/validation/document encoding
were approximately 0.0055/0.0110/0.0315ms, versus observed semaphore/downstream
means of 148.5/190.9ms. This is **not** a percentile subtraction or a CPU
attribution: it discourages claiming that small serialization changes alone
eliminate the observed tail. Response suppression still requires authoritative
REST support and separately authorized real-Azure verification.

**#1024 remains open.** Resolve/localize the mixed-cell failure and incomplete
attempt observation, decide on explicit authorization for exact transport
retry/backoff instrumentation/gates, and obtain the missing repetition/reference
if needed for the chosen hypothesis. Do not turn this failed finite campaign
into an optimized-floor or statistically repeatable improvement claim.

## Separately approved real-Azure experiment plan

No production claims follow from emulator measurements. Before any billed
experiment, obtain explicit approval of subscription, region/topology, a
resource cost cap, RU cap and wall-clock deadline. Use one disposable database
and container under a dedicated resource group; no customer data. Preserve
the same pinned executable/dependency manifests and record the selected
consistency mode and topology rather than assuming co-location.

Start with paired 25-item distinct-partition puts at concurrency 1 and 8,
then one paired 25-item shared-partition mixed cell: six finite windows,
128 batches maximum each, five-second dispatch and 30-second drain each.
Use a ten-minute setup deadline and a 20-minute whole-job deadline. Stop on
resource/cost-limit violations, terminal failures or unexpected backend
behavior; no automatic reruns or threshold changes. Record RU, 429s,
resubmissions, actual instrumentation availability, process resource samples
and cleanup receipts. Delete the resource group promptly in an always-run
cleanup step, verify deletion, and provide an owner/deadline for reaping if
cleanup fails. Transport telemetry changes additionally require their normal
classifier-mandated live-Azure gate; this plan is **not approval** for either.
