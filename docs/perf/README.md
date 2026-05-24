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

## Roadmap

- Workload matrix per module (small / medium / large payload, 1 / 16 / 64
  concurrency) — currently MVP is a single point per module.
- Real-Azure pass behind a manual workflow (see issue #119 for the
  real-Azure smoke pattern).
- Allocation / CPU profile via `dotnet-counters` collect during the run.

