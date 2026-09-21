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
