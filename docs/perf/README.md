# aws2azure — Performance Baseline

This folder holds the perf indicators captured by `tests/Aws2Azure.PerfTests`.

## What it measures

End-to-end throughput and latency percentiles for one representative
operation per service module, with the AWS SDK as the **client** and the
proxy + a local emulator as the **server**:

| Module    | Scenario           | Backend emulator                       |
|-----------|--------------------|----------------------------------------|
| S3        | `PutObject` 4 KiB  | Azurite (Blob REST)                    |
| SQS       | `SendMessage` 256 B| Service Bus emulator (AMQP)            |
| SNS       | `Publish` 256 B    | Service Bus emulator (AMQP topics)     |
| DynamoDB  | `PutItem`          | Cosmos DB emulator (REST)              |
| Kinesis   | `PutRecord` 256 B  | Event Hubs emulator (AMQP)             |

Each scenario runs at concurrency 16 for 20 s after a 3 s warmup, in a
closed-loop driver. The harness records per-call latency in microseconds
and emits a single Markdown table with throughput + p50/p95/p99/max.

## Important caveat

**The numbers below are emulator-bound and tell you about proxy overhead,
not real-Azure throughput.** Emulators diverge from real Azure on
throttling, consistency, and feature surface (per CONTRIBUTING). Use these
indicators to:

1. catch **regressions** of the proxy itself (compare two runs of the same
   suite on the same host); and
2. spot **per-module overhead deltas** (e.g. AMQP send path versus the REST
   passthrough on S3).

Do **not** use them as a model of real-Azure throughput.

## How to run

```bash
cd /path/to/aws2azure
AWS2AZURE_PERF=1 dotnet test tests/Aws2Azure.PerfTests \
    -v minimal --logger "console;verbosity=normal"
```

Without `AWS2AZURE_PERF=1` every scenario self-skips, so a plain
`dotnet test` at the solution level is unaffected.

Each scenario brings up its own emulator(s) + a fresh out-of-process
`Aws2Azure.Proxy`. Total bring-up + run time for the full module sweep is
~8–12 min on a developer laptop (Cosmos emulator dominates with a
~1.5 GB image pull on first run).

To run a single module:

```bash
AWS2AZURE_PERF=1 dotnet test tests/Aws2Azure.PerfTests \
    --filter "FullyQualifiedName~SqsPerfTests"
```

Results are appended to `docs/perf/baseline-latest.md` (overwritten on
each invocation that writes its first row). Commit that file to record
the baseline for the current commit, or rename it (e.g.
`baseline-2026-05-23.md`) to keep history.

## Routing & DNS dependency

AWS SDK requests carry the proxy URL's host in the `Host` header, and the
proxy dispatches per-module via host prefix (`s3.`, `sqs.`, `sns.`,
`dynamodb.`, `kinesis.`). To avoid editing `/etc/hosts` the fixtures
target `<module>.127.0.0.1.nip.io:<port>`, relying on nip.io's wildcard
DNS to resolve every subdomain to `127.0.0.1`.

If you run the suite in a network-restricted environment that blocks
nip.io, either:

- pre-add the host names to `/etc/hosts` pointing at `127.0.0.1`, or
- swap `PerfProxyProcess.ServiceUrlForHost` to return the raw loopback URL
  and install a `DelegatingHandler` on each AWS SDK client that rewrites
  the `Host` header to the expected prefix.

## Reading throughput vs latency

For most modules the per-call latency (p50/p95/p99) and the per-second
throughput tell a consistent story: at concurrency N, `throughput ≈ N /
p50`. Always read the two columns together.

> **Note on Kinesis (issue #129):** an earlier baseline reported a ~1.7
> ops/s ceiling for `PutRecord`. That number was a transient
> WSL2/Docker/cold-cache artifact, not an emulator cap — pristine `main`
> sustains ~95 ops/s, slightly above the Azure SDK direct baseline
> (~82 ops/s) on the same emulator. If you see a similar drop in the
> future, set `AWS2AZURE_AMQP_TIMING=1` to get per-send breadcrumbs and
> compare with the `AzureEventHubsSdkBaselinePerfTests` row.

## Regression gates

Two independent gates guard against perf regressions. They run as **two
separate `dotnet test` invocations** in `.github/workflows/perf.yml` (the
relative gate reads a file the scenario step writes, and xUnit gives no
cross-collection ordering — so a dedicated second process is the robust
hand-off point):

### 1. Absolute health + floors — `AssertHealthy` / `AssertNoRegression`

Runs inside each scenario. `AssertHealthy()` fails on no completions or a
>10% failure rate — a genuinely broken proxy path. `AssertNoRegression()`
compares against per-scenario floors/ceilings in
[`baseline-reference.json`](baseline-reference.json) (`minThroughputPerSec`
/ `maxP99Ms`). **On emulator-bound AMQP paths these absolutes are set to
`0` (disabled)** because the emulator's multi-second cold-connect tail
stalls move p99 by 20× run-to-run — an absolute ceiling there only produces
chronic false reds. Those paths are gated relatively instead (below). REST
paths (S3/DynamoDB) keep meaningful absolute floors.

### 2. Relative proxy-vs-SDK gate — `RelativeRegressionGate`

Each proxy scenario is paired in `baseline-reference.json`'s `pairings`
section with an `azure-sdk.*` baseline scenario that measures the **same
operation against the same emulator with no proxy in the path**. The gate
fails only when the proxy's throughput drops below — or its latency climbs
above — a configured *multiple* of that baseline:

```jsonc
"pairings": {
  // REST / receive: gate on p99-ratio (+ throughput)
  "dynamodb.GetItem (small)": { "baseline": "azure-sdk.Cosmos.ReadItem (small)", "minThroughputRatio": 0.45, "maxP99Ratio": 3.0 },
  // AMQP send: gate on p50-ratio only (p99 is cold-attach noise)
  "sns.Publish (256 B)":      { "baseline": "azure-sdk.ServiceBusTopics.SendMessage (256 B)", "minThroughputRatio": 0.0, "maxP50Ratio": 5.0 }
}
```

`azure-sdk.*` baselines never fail the build — they are reference rulers,
never the proxy side of a pairing.

Because the emulator's tail-latency jitter hits **both** sides equally, the
ratio cancels the noise — a failure here is genuine proxy overhead, not
flakiness. A `0` ratio opts out of that dimension.

**Metric by path shape** — the gate picks the statistic that is *stable* for
each path, and only pairs against a baseline that is itself a stable ruler:

- **REST + AMQP receive** pairs gate on **p99-ratio** (and usually
  throughput-ratio): their latency distribution is unimodal and their SDK
  baseline is stable, so p99 is a reliable signal.
- **EventHubs send** (`kinesis.PutRecord`) gates on **p50-ratio (median)**. A
  send's distribution is bimodal — a steady mode plus rare multi-second cold
  link-attach spikes — and which side those spikes land in p99 (vs max) is
  essentially random per run, so the p99-ratio swings wildly (observed
  **0.06×–11×** between structurally identical send pairs in one run). The
  median ignores the cold-attach tail; the EH baseline is stable (~3–5 ms,
  thousands of clean samples), so the p50-ratio is meaningful.
- **Service Bus sends** (`sqs.SendMessage`, `sns.Publish`) are **NOT paired**
  and gate on a **throughput floor only**. The SB emulator's *own* SDK send
  baseline is too unstable to be a relative ruler (its p50 swings 3–5×
  run-to-run — queue 8→23.5 ms, topic 43→8.8 ms — because the baseline
  link-attaches per send while the proxy pools connections). And an *absolute*
  p99 ceiling is no better: the proxy's own send p99 is bimodal, with a rare
  cold AMQP link-attach dropping a multi-second spike into the top 1%
  unpredictably (observed **234 ms** one run, **3108 ms** the next). So these
  paths rely on `AssertHealthy` (no completions / >10% failures) plus a
  throughput floor (`minThroughputPerSec`) as the catastrophe detector; the
  latency tail carries no stable signal at any threshold.
- **DynamoDB BatchGetItem** is also **NOT paired** — it gates on an **absolute
  p99 ceiling** instead. Here the *proxy* is the stable side (Cosmos REST p99
  ~240–340 ms across runs, unimodal), while the `Cosmos.ReadManyItems` baseline
  is the jittery, structurally-unfair ruler: it is a specialized batched SDK
  API the proxy can only approximate as N point-reads, and its own p99 collapsed
  from ~106 ms to ~45 ms in one run, spiking the *ratio* to 5.3× with no proxy
  change. Gating the stable proxy p99 against a fixed ceiling is the honest
  signal; a real proxy regression still trips it.

A **freshness window** (2 h) makes the gate skip any pair whose proxy and
baseline rows were not captured in the same run, so a fresh proxy row is
never judged against a stale committed baseline.

The machine-readable hand-off file `baseline-latest.json` is written by the
scenario step (merged in place by scenario name, each row stamped
`capturedAtUtc`) and is **gitignored** — it is fresh per run. The
human-readable `baseline-latest.md` snapshot stays tracked.

To adjust a gate, edit `baseline-reference.json` deliberately — bump a ratio
only when a code change is an understood, accepted trade-off. The guard
tests in `KnownPerfScenariosTests` fail the build if a pairing references an
unknown scenario.

### 3. Under-load memory ceiling (#274) — `AssertNoRegression`

The harness also characterizes the proxy's **memory under sustained load**,
the operationally-relevant figure for a sidecar replicated across every app
pod (idle RSS / static footprint is tracked separately under #271/#273). The
proxy self-reports its runtime memory via gauges on `/_aws2azure/metrics`:

| Gauge | Meaning |
|---|---|
| `aws2azure_process_working_set_bytes` | proxy process resident set |
| `aws2azure_dotnet_gc_heap_size_bytes` | managed heap size |
| `aws2azure_dotnet_gc_allocated_bytes_total` | cumulative managed allocations (monotonic) |
| `aws2azure_dotnet_gc_gen2_collections_total` | cumulative gen2 collections (monotonic) |

`ProxyMemoryProbe` scrapes these from inside the (out-of-process) proxy —
the test process's own working set would measure the wrong thing. During the
measure window `PerfRunner` samples the working set every 200 ms (recording
the **peak**) and diffs the cumulative allocated-bytes / gen2 counters across
the window. The result surfaces in the `RSS MB`, `GCheap MB`, `B/op`, and
`g2` columns of `baseline-latest.md` and `history.csv`, and as
`peakWorkingSetMb` / `allocBytesPerOp` / `gen2Collections` in
`baseline-latest.json`.

Two optional ceilings in `baseline-reference.json` gate it (both `0` =
opt-out, and both no-op when the run did not actually measure memory, e.g.
the probe was unreachable):

```jsonc
"dynamodb.Scan (pushable filter)": {
  "minThroughputPerSec": 40.0, "maxP99Ms": 700.0,
  "maxPeakWorkingSetMb": 0.0,   // peak proxy RSS over the window
  "maxAllocBytesPerOp":  0.0    // mean managed bytes allocated per completed op
}
```

> **Choosing the metric.** `maxAllocBytesPerOp` is the
> **scenario-attributable** churn signal — it is measured as a delta over the
> window, so it isolates *this* scenario's allocation behavior and is the
> better leak/regression detector. `maxPeakWorkingSetMb` is a **cumulative
> high-water mark** across every scenario sharing one proxy process (working
> set rarely shrinks), so treat it as a wide catastrophe ceiling, not a tight
> ruler.

The high-risk scenarios (`DynamoDb Scan/Query/Batch`, `SQS`/`SNS` publish,
`S3` put/get) carry **generous catastrophe ceilings** seeded from the first
CI perf run: `maxAllocBytesPerOp` ≈ 2.5–3× the observed per-op churn, plus a
wide `maxPeakWorkingSetMb` of 400 (vs ~100–154 MB observed). They are sized
to trip a genuine regression — a per-op allocation doubling, a lost buffer
pool, a working-set leak — without flapping on run-to-run jitter. **Operator
follow-up:** tighten these deliberately as more runs accumulate (same workflow
as the throughput floors above); the numbers are emulator-bound JIT-build
figures (the proxy runs via `dotnet run`, not AOT), so treat them as relative
regression rulers, not absolute prod memory.




- Workload matrix per module (small / medium / large payload, 1 / 16 / 64
  concurrency) — currently MVP is a single point per module.
- Real-Azure pass behind a manual workflow (see issue #119 for the
  real-Azure smoke pattern).
- Allocation / CPU profile via `dotnet-counters` collect during the run.

