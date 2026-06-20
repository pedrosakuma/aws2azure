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

Local results are written under `TestResults/perf/` by default so an
ad-hoc run does not dirty tracked baseline files. Set
`AWS2AZURE_PERF_UPDATE_DOCS=1` to opt into updating
`docs/perf/baseline-latest.md`, `baseline-latest.json`, and `history.csv`
(CI sets this), or set `AWS2AZURE_PERF_DIR` to an explicit output
directory.

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




### 4. Tiered strategy & the falsification criterion (#420)

A lower CPU/allocation number is **necessary but not sufficient** to call
something an optimization. It only counts if it survives the real
end-to-end flow — if CPU/alloc drop but response time and throughput do
not improve, it is not an optimization, just a micro-benchmark artifact.
And a CPU/alloc win only *cashes out* into throughput/latency when **CPU
is the binding constraint** (a CPU-bound / densely-packed sidecar under a
constrained CPU quota — the project's stated deployment premise). In a
network/IO-bound regime the same win is invisible at the response layer.

Perf coverage is therefore organized in three tiers, each answering a
different question:

| Tier | Where | Cadence | Answers |
|------|-------|---------|---------|
| **0 — mechanism micro-guard** | `tests/Aws2Azure.UnitTests` (e.g. `CosmosFusedEnvelopeAllocTests`) | every PR (`ci.yml`) | *Does the optimized code path still allocate/compute less than the path it replaced?* Deterministic `GC.GetAllocatedBytesForCurrentThread` deltas — fast, exact, non-flaky. |
| **1 — emulator throughput** | `tests/Aws2Azure.PerfTests` + emulators | nightly (`perf.yml`) | *Does proxy overhead regress vs the SDK baseline?* Relative + resource gating (above). Emulator-bound — **never emits CosmosBinary**, so it only ever exercises the text path. |
| **2 — real-Azure A/B (falsification arbiter)** | `tests/Aws2Azure.PerfTests` against live Azure | weekly (`perf-real-azure.yml`) | *Does the mechanism win (Tier 0) translate to a real throughput/latency improvement?* Runs the same scenarios twice — text vs binary — against real Cosmos and compares. |

**The falsification criterion (Tier 2).** For an optimization like
CosmosBinary (#268/#321/#336), the chain is:

1. Tier 0 proves the mechanism allocates/computes less (the *necessary*
   condition). If this fails the win never existed.
2. Tier 2 runs the optimization on/off against real Azure under load and
   reads both sides: the throughput/latency percentiles **and** the
   proxy's own CPU/alloc gauges (`aws2azure_*`). The optimization is
   *confirmed* only if the lower CPU/alloc co-occurs with **equal-or-better
   throughput/latency** in a regime where CPU is the binding constraint
   (saturation + constrained CPU). If real throughput/latency do not move,
   the optimization is **falsified as an end-to-end win** — it stays a CPU
   micro-saving, documented as such, not promoted.

Because real Azure throttles and its absolute latency is network-bound,
Tier 2 **never gates on absolute throughput/p99** (those flap). The
`perf-real-azure.yml` job sets `AWS2AZURE_PERF_RESOURCE_ONLY=1`, which
makes `AssertNoRegression()` suppress the throughput-floor / p99-ceiling
dimensions while still enforcing the backend-independent resource ceilings
(alloc/op, peak working set). The throughput/latency numbers are captured
as run artifacts (per-mode `AWS2AZURE_PERF_DIR`, kept out of the committed
emulator baseline) for the A/B comparison, not asserted as absolutes.

> **Why a CPU/alloc win can fail to cash out.** The proxy's design goal is
> to drive its own CPU cost toward near-zero so the request is essentially
> pure IO. When IO/network dominates the response, halving an already-tiny
> CPU slice leaves the wall-clock response unchanged — the win only becomes
> observable once enough sidecars share a CPU that the saved cycles convert
> to headroom (more concurrent requests per core). Tier 2 must run in that
> regime to observe it; outside it, a confirmed Tier 0 win can read as
> "no end-to-end benefit" — which is the correct, honest verdict for that
> deployment shape.

### Saturation sweep (Tier 2 knee)

A single fixed-concurrency measurement obeys **Little's law** —
`throughput = concurrency / latency` — so at one worker count it cannot
distinguish whether the *proxy* or the *harness* is the binding constraint.
A CPU/alloc optimization can therefore look like "no change" simply because
the run was network-bound, not because the optimization is worthless. The
**saturation sweep** (`PerfSweep`, issue #420) removes that ambiguity by
driving the same workload up a concurrency ladder until throughput stops
climbing, then reading the optimization at the **knee** — the smallest
concurrency that already reaches (within 5 %) the maximum sustained
throughput. Beyond the knee, extra workers only inflate latency.

- **Opt-in.** The sweep scenario (`GetItem_saturation_sweep`) is gated on
  `AWS2AZURE_PERF_SWEEP=1`, set only in `perf-real-azure.yml`. The emulator
  nightly leaves it off, so the committed fixed-concurrency baseline is
  unchanged. The ladder comes from `AWS2AZURE_PERF_SWEEP_LEVELS` (csv,
  default `8,16,32,64,128`) and per-level duration from
  `AWS2AZURE_PERF_SWEEP_SECONDS` (default `8`).
- **Knee semantics.** `PerfSweep.DetectKnee` is a pure function over the
  per-level `(concurrency, throughput, p99)` points (unit-tested with
  synthetic curves, no backend). `ReachedSaturation` is true **only** when
  the ladder extended *beyond* the knee — at least one rung above the knee was
  tested, so the near-flat region past it was actually sampled. When the knee
  is itself the top rung (95%-of-max is only reached at the very top),
  throughput was still climbing to the end and the run is reported as
  **NOT SATURATED** (widen the ladder) rather than guessing a knee. Saturation
  keys off the knee position, not the numeric peak — the max routinely lands on
  the top rung from sub-percent noise even on a saturated curve.
- **A/B verdict.** Each arm (text, then binary) records one row per ladder
  rung plus a single `dynamodb.GetItem (sweep knee)` row, all labelled with
  the backend. The optimization is confirmed only if the binary arm's
  **throughput-at-knee** is equal-or-better and **p99-at-knee** no worse,
  under the CPU-constrained proxy (`AWS2AZURE_PERF_PROXY_CPUS=1`). These rows
  use distinct scenario names, so they never pollute the gated
  fixed-concurrency baselines and carry no absolute floor/ceiling (the knee
  is regime-dependent); the verdict comes from diffing the two passes'
  artifacts, not from `AssertNoRegression`.

## Roadmap

- Workload matrix per module (small / medium / large payload, 1 / 16 / 64
  concurrency) — currently MVP is a single point per module.
- Real-Azure A/B is live for DynamoDB via
  [`perf-real-azure.yml`](../../.github/workflows/perf-real-azure.yml)
  (Tier 2 above); extend the same backend-pluggable fixture pattern to the
  other modules, and add proxy-vs-SDK relative gating against the real
  backend.
- Allocation / CPU profile via `dotnet-counters` collect during the run.


---

## Footprint budget (#271)

Throughput/latency is only half of a sidecar's cost. As a sidecar, aws2azure
is also judged on **idle memory, cold start, and image/binary size** — the
numbers a prospective adopter checks first. These are measured by
`tests/Aws2Azure.FootprintTests` and gated exactly like the perf baseline.

### What it measures

| Metric          | How                                                            |
|-----------------|---------------------------------------------------------------|
| AOT binary size | published `Aws2Azure.Proxy` file length                       |
| Idle RSS        | boot the binary, settle, sample steady-state `VmRSS`          |
| Cold start      | process start → first `/_aws2azure/health` 200 (median of 7)  |
| Image size      | `docker image inspect` (opt-in, see below)                    |

The harness publishes the proxy as a **real Native-AOT binary** and drives it
out-of-process, so the numbers reflect the shipped artifact — not a `dotnet
test` host or a `dotnet run` JIT build.

### Baseline (runner-bound)

Latest snapshot lives in [`footprint-latest.md`](./footprint-latest.md); the
cumulative trend is [`footprint-history.csv`](./footprint-history.csv). On a
GitHub-hosted `linux-x64` runner the all-modules build measures roughly:

| Binary | Idle RSS | Cold start |
|--------|----------|------------|
| ~22 MB | ~34 MB   | ~70 ms     |

> Footprint numbers are **runner-bound**. Binary size is deterministic; idle
> RSS is fairly stable; cold start scales with runner core count, so its
> ceiling is deliberately loose and only catches gross regressions. Every row
> records the RID.

### The gate

`FootprintResult.AssertWithinBudget()` reads
[`footprint-reference.json`](./footprint-reference.json) — per-metric ceilings
keyed by scenario. The convention mirrors the perf baseline:

- a ceiling of **0 opts out** of that dimension;
- a scenario **absent** from the JSON is **not gated** (newly added scenarios
  pass silently until an operator records a ceiling);
- `KnownFootprintScenariosTests` is a drift guard — every scenario must appear
  in the reference JSON and vice versa, so a missing ceiling fails a fast unit
  test instead of silently skipping the budget gate.

### How to run

```bash
# Multi-minute (it AOT-publishes the proxy):
AWS2AZURE_FOOTPRINT=1 dotnet test tests/Aws2Azure.FootprintTests -c Release

# Reuse a previous publish (cache by module selection):
AWS2AZURE_FOOTPRINT=1 \
AWS2AZURE_FOOTPRINT_PUBLISH_DIR=/tmp/aws2azure-footprint \
  dotnet test tests/Aws2Azure.FootprintTests -c Release

# Also capture container image size (requires a built image):
docker build -t aws2azure:footprint .
AWS2AZURE_FOOTPRINT=1 AWS2AZURE_FOOTPRINT_IMAGE=aws2azure:footprint \
  dotnet test tests/Aws2Azure.FootprintTests -c Release

# Also measure per-tier (build-time module selection, #273) footprint. Each
# tier is a separate AOT publish, so this is opt-in and runs only on the
# nightly footprint job by default:
AWS2AZURE_FOOTPRINT=1 AWS2AZURE_FOOTPRINT_TIERS=1 \
  dotnet test tests/Aws2Azure.FootprintTests -c Release
```

The per-tier scenarios quantify and gate the
[build-time module selection](../deployment/module-selection.md) delta (e.g.
`aws2azure (s3 only)`).

CI runs this on the perf cadence — nightly, `workflow_dispatch`, and PRs that
carry the **`run-footprint`** (or **`run-perf`**) label — via
[`.github/workflows/footprint.yml`](../../.github/workflows/footprint.yml).
The image-size ceiling stays at 0 (silent passthrough) until the first CI run
records the number; tighten it deliberately afterwards, same as the perf
floors.
