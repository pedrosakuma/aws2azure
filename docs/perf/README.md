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

## Roadmap

- Workload matrix per module (small / medium / large payload, 1 / 16 / 64
  concurrency) — currently MVP is a single point per module.
- Receive-side scenarios (SQS `ReceiveMessage`, Kinesis `GetRecords`).
- Real-Azure pass behind a manual workflow (see issue #119 for the
  real-Azure smoke pattern).
- Allocation / CPU profile via `dotnet-counters` collect during the run.

