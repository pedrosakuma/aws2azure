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

Two explicit stages cover eight workload combinations, each through both
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

The runtime is fixed to merged attribution source
`bd89968772af815ad847845159075d2d1521f703` in **both slots**. This is deliberate
A/A repetition, not a claimed candidate optimization. The earlier controlled
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
records actual REST attempts, repeated item attempts and 429 responses;
the driver records actual resubmission backoff. Those are separate quantities.
`AzureHttpClient` passes 429 through without its own retry; its internal retry
loop handles selected server/transport failures. Instrumenting that loop would
touch `src/Aws2Azure.Core/Azure/AzureHttpClient.cs`, mechanically requiring
`run-integration`, `run-perf` and **`run-real-azure`**. This campaign does not
bypass that requirement or authorize billed resources. Internal backoff timing
remains explicitly unavailable, even if all observed cells have zero retries.

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
