# Cosmos transport spike (issue #265)

> **Throwaway experiment.** Not part of `aws2azure.slnx`, not referenced by any
> production project, never shipped. It pulls in the official
> `Microsoft.Azure.Cosmos` SDK **only as a measuring stick** — the locked
> decision *"Azure integration: direct REST calls, no Azure SDK dependency"*
> stays intact for production code. `dotnet build` / CI never compile this
> folder because it is absent from the solution and carries a local
> `Directory.Build.props` that shadows the repo-root AOT / warnings-as-errors
> settings.

## What it measures

The proxy's DynamoDB→Cosmos module talks to Cosmos in **Gateway/REST mode only**
(HTTPS to the account endpoint). The official SDK defaults to **Direct mode**
(TCP/rntbd straight to the partition replica). This spike quantifies the gap so
we can decide — with data, not vibes — whether a Direct/rntbd implementation
would be worth the (large, ongoing) cost.

Three lanes, same account / container / dataset:

| Lane | Transport | Purpose |
|------|-----------|---------|
| **SDK Direct**  | `ConnectionMode.Direct` (TCP/rntbd) | the ceiling Direct could buy |
| **SDK Gateway** | `ConnectionMode.Gateway` (HTTPS REST) | apples-to-apples REST via same SDK |
| **Raw REST**    | `HttpClient` + master-key HMAC, using the **same `SocketsHttpHandler` config as the proxy's `AzureHttpClient.BuildDefaultHandler`** (64-conn cap, HTTP/2 multiplexing, 2-min pooled lifetime) | faithfully mirrors the proxy's production Cosmos transport |

`SDK Direct` vs `SDK Gateway` isolates **only** the transport (identical
serializer, auth, client overhead) → the honest Direct-vs-REST delta. `Raw REST`
mirrors the proxy's production Cosmos transport: it reuses the exact
`SocketsHttpHandler` tuning from `AzureHttpClient.BuildDefaultHandler`
(`MaxConnectionsPerServer=64`, `EnableMultipleHttp2Connections`,
`PooledConnectionLifetime=2min`) so its numbers reflect the proxy's
**Cosmos-side** call. It measures only the Cosmos round-trip — it does **not**
include the proxy's per-request SigV4 validation, payload parse, table-metadata
lookup, Cosmos→DynamoDB response translation, Kestrel pipeline, or the app→proxy
hop. Those are orthogonal to Direct-vs-Gateway and are measured separately by
`tests/Aws2Azure.PerfTests`.

Operations: sequential point-read latency (p50/p95/p99), single-partition query
latency (SDK lanes), and concurrent point-read throughput (ops/sec + p99 under
load). Average RU charge is reported for context.

## Run it (real Azure only)

The local emulator does **not** do Direct mode faithfully — run against a real
Cosmos account, ideally co-located in the same region as the machine to reflect
production topology.

```bash
export COSMOS_ENDPOINT="https://<account>.documents.azure.com:443/"
export COSMOS_KEY="<primary-or-secondary-master-key>"

dotnet run -c Release --project experiments/cosmos-transport-spike
```

### Optional knobs (defaults)

| Env var | Default | Meaning |
|---------|---------|---------|
| `COSMOS_DB` | `spikeDb` | database id (created if absent) |
| `COSMOS_CONTAINER` | `spikeItems` | container id, partition key `/pk` (created if absent) |
| `SPIKE_RU` | `4000` | manual RU/s on the container (high enough to avoid 429 dominating) |
| `SPIKE_ITEMS` | `500` | seed item count (each its own logical partition) |
| `SPIKE_ITERATIONS` | `1000` | sequential point reads per lane (latency run) |
| `SPIKE_CONCURRENCY` | `32` | parallel workers (throughput run) |
| `SPIKE_DURATION_SEC` | `10` | throughput window per lane |
| `SPIKE_PAYLOAD_BYTES` | `256` | per-item payload size |
| `SPIKE_LANES` | `direct,gateway,raw` | comma-separated subset of lanes to run (e.g. `gateway,raw` to skip Direct) |
| `SPIKE_PREFERRED_REGION` | account default | `ApplicationPreferredRegions[0]` for the SDK lanes |

The container is left in place between runs (seeding is idempotent upserts).
Delete `COSMOS_DB` manually when done.

## Interpreting the output

A final block prints the Direct-vs-Gateway delta per operation. Rough decision
guide:

- **Small delta (single-digit % p99, throughput within noise):** Gateway/REST is
  good enough — close #265 as won't-implement and record the Gateway-only
  decision in a Cosmos request-flow gap doc.
- **Large delta (Direct materially lower p99 and/or higher throughput, especially
  under concurrency where the gateway tier saturates):** justifies a scoped
  follow-up to evaluate an rntbd implementation — as its own large, isolated
  effort with a real risk budget.

> Numbers are environment-bound (region, RU, machine, network). Record the
> account region, RU, and machine alongside any result you cite.
