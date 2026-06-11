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
| **Raw REST**    | `HttpClient` + master-key HMAC, using the **same `SocketsHttpHandler` config as the proxy's `AzureHttpClient.BuildDefaultHandler`** (2-min pooled lifetime, HTTP/2 multiplexing) with `MaxConnectionsPerServer` pinned to the shared `SPIKE_CONN_LIMIT` | faithfully mirrors the proxy's production Cosmos transport |

`SDK Direct` vs `SDK Gateway` isolates **only** the transport (identical
serializer, auth, client overhead) → the honest Direct-vs-REST delta. `Raw REST`
mirrors the proxy's production Cosmos transport. It measures only the Cosmos
round-trip — it does **not** include the proxy's per-request SigV4 validation,
payload parse, table-metadata lookup, Cosmos→DynamoDB response translation,
Kestrel pipeline, or the app→proxy hop. Those are orthogonal to
Direct-vs-Gateway and are measured separately by `tests/Aws2Azure.PerfTests`.

### Methodology (measure the transport, not incidental client defaults)

- **Connection parametrization is equalized.** The SDK gateway lane's
  `GatewayModeMaxConnectionLimit` (**default 50** — the knob that otherwise makes
  "gateway" look slow and conflates client config with the transport method) and
  the raw lane's `MaxConnectionsPerServer` are **both** pinned to
  `SPIKE_CONN_LIMIT` (default `max(64, concurrency)`), so neither HTTP lane is
  artificially capped below the offered concurrency. Set `SPIKE_CONN_LIMIT`
  *lower* to deliberately study the connection-cap effect.
- **Latency is interleaved.** Every iteration hits all enabled lanes against the
  *same* document id in randomized lane order, so all lanes see the same network
  window sample-by-sample — this removes the cross-lane jitter that made the old
  run-one-lane-then-the-next sequential metric noisy.
- **Repeated with a noise floor.** Each latency/throughput measurement runs
  `SPIKE_REPS` times; the table's `stddev` column is the stddev of the per-rep
  p50 (latency rows) or per-rep ops/s (throughput rows), so a delta smaller than
  the stddev is noise, not signal.

Operations: interleaved point-read latency (p50/p95/p99), single-partition query
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
| `SPIKE_ITERATIONS` | `1000` | interleaved point reads per lane per rep (latency run) |
| `SPIKE_CONCURRENCY` | `32` | parallel workers (throughput run) |
| `SPIKE_DURATION_SEC` | `10` | throughput window per lane per rep |
| `SPIKE_WARMUP` | `100` | warm-up reads per lane before each measured rep (open conns / JIT) |
| `SPIKE_REPS` | `3` | repetitions of the latency run (drives the `stddev` noise floor) |
| `SPIKE_TPUT_REPS` | `1` | repetitions of the throughput run (each is `SPIKE_DURATION_SEC` long) |
| `SPIKE_CONN_LIMIT` | `max(64, concurrency)` | shared connection cap: gateway `GatewayModeMaxConnectionLimit` + raw `MaxConnectionsPerServer`. Lower it to study the cap effect |
| `SPIKE_RAW_HTTP_VERSION` | `1.1` | HTTP version the raw lane requests (production default is `1.1`; try `2.0`) |
| `SPIKE_PAYLOAD_BYTES` | `256` | per-item payload size |
| `SPIKE_LANES` | `direct,gateway,raw` | comma-separated subset of lanes to run (e.g. `gateway,raw` to skip Direct) |
| `SPIKE_PREFERRED_REGION` | account default | `ApplicationPreferredRegions[0]` for the SDK lanes |

The container is left in place between runs (seeding is idempotent upserts).
Delete `COSMOS_DB` manually when done.

## Interpreting the output

A final block prints, per operation, each lane's delta **vs the SDK Gateway
baseline** (point-read latency p50/p99 and throughput). Compare any delta
against that row's `stddev` column first: **a delta smaller than the stddev is
noise.** Rough decision guide:

- **Small delta (single-digit % p99, throughput within the stddev noise floor):**
  Gateway/REST is good enough — close #265 as won't-implement and record the
  Gateway-only decision in a Cosmos request-flow gap doc.
- **Large delta (Direct materially lower p99 and/or higher throughput beyond the
  noise floor, especially under concurrency where the transport saturates):**
  justifies a scoped follow-up to evaluate an rntbd implementation — as its own
  large, isolated effort with a real risk budget.

> **Equalize before you compare.** With `SPIKE_CONN_LIMIT` at its default both
> HTTP lanes get the same connection budget, so the gateway lane is *not* hobbled
> by the SDK's 50-connection default. If you see "gateway" lagging badly, check
> `SPIKE_CONN_LIMIT` ≥ `SPIKE_CONCURRENCY` — otherwise you are measuring the cap,
> not the transport.

> Numbers are environment-bound (region, RU, machine, network). Record the
> account region, RU, machine, and the knob values alongside any result you cite.
