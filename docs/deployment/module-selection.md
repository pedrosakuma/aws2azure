# Build-time module selection

`aws2azure` ships as a single binary that multiplexes every supported service
module (S3, SQS, DynamoDB, Kinesis, SNS, Secrets Manager) by `Host`/path. That
all-in-one build is the right default for a general-purpose proxy, but it is not
the leanest sidecar: a workload that only talks to S3 still carries the code,
JSON contexts, and Azure REST clients for five other services.

Because the proxy is **sidecar-first** — small idle memory, fast cold start, and
a small image are first-class adoption concerns — you can select **at build
time** exactly which service modules are compiled in. Unselected module code is
never referenced, so the Native-AOT trimmer drops it (and its transitive Azure
REST clients, JSON source-gen contexts, and shared infrastructure) from the
binary entirely.

This is a **compile-time** decision, not a runtime flag: the cost of an unused
module is removed from the artifact, not merely disabled at startup. (The
existing `services.<name>.enabled` config still gates modules at runtime within
whatever build you ship.)

---

## Selecting modules

Pass the `Modules` MSBuild property — a semicolon- or comma-separated list of
module ids — at publish time:

```bash
# All modules (the default; empty or "all" are equivalent)
dotnet publish src/Aws2Azure.Proxy -c Release -r linux-x64

# A single-service sidecar
dotnet publish src/Aws2Azure.Proxy -c Release -r linux-x64 -p:Modules=s3

# A tailored subset
dotnet publish src/Aws2Azure.Proxy -c Release -r linux-x64 -p:Modules=sqs+sns+kinesis
```

Valid module ids: `s3`, `sqs`, `dynamodb`, `kinesis`, `sns`, `secretsmanager`.

> **Separator:** on the command line use `+` (or a space) between module ids.
> The `dotnet`/MSBuild property parser treats both `,` and `;` as
> property-assignment separators, so `-p:Modules=sqs;sns` fails with `MSB1006`.
> A typo or otherwise-empty selection (e.g. `-p:Modules=s33`) fails the build
> rather than silently shipping a module-less proxy.

Shared infrastructure (the AMQP transport, the Entra ID token provider, the
Event Hubs / Service Bus topics / Event Grid clients) is pulled in
automatically based on which modules you select, so you never list it
explicitly.

### Container images

The `Dockerfile` exposes the same selection as a `MODULES` build-arg, so you can
bake a tailored sidecar image:

```bash
# Default: all modules
docker build -t aws2azure:all .

# S3-only sidecar image
docker build --build-arg MODULES=s3 -t aws2azure:s3 .
```

### Verifying what shipped

The running proxy reports its compiled-in modules:

```bash
curl -s http://localhost:8080/_aws2azure/modules
# ["s3"]
```

---

## Measured footprint delta

From the footprint harness (`tests/Aws2Azure.FootprintTests`, #271) on
`linux-x64`, Native-AOT. Numbers are runner-bound — treat the **delta** as the
durable signal, not the absolute values (your CI runner will differ; binary size
is deterministic, idle RSS and cold start vary with the host).

| Build      | Binary (MB) | Idle RSS (MB) | Cold start (ms) |
|------------|-------------|---------------|-----------------|
| all modules | 22.2       | 33.9          | 71              |
| s3 only     | 16.3       | 26.6          | 56              |
| **delta**   | **−27%**   | **−22%**      | (within noise)  |

Takeaways:

- **Binary size** shrinks ~21–27% for a single-module build — the largest, most
  deterministic win, and the one that matters most for image pull time.
- **Idle RSS** drops ~20% because fewer JSON source-gen contexts and Azure REST
  clients are initialised.
- **Cold start** is essentially module-invariant (it is dominated by the AOT
  runtime and Kestrel bring-up, not module count); the small difference above is
  within run-to-run noise.

The per-tier numbers are re-measured and budget-gated on the nightly footprint
job (`AWS2AZURE_FOOTPRINT_TIERS=1`); see
[`docs/perf/README.md`](../perf/README.md) for the gate mechanics and
[`docs/perf/footprint-latest.md`](../perf/footprint-latest.md) for the latest
snapshot.

---

## How it works

`src/Aws2Azure.Proxy/Aws2Azure.Proxy.csproj` turns the `Modules` list into:

1. `MOD_<NAME>` compilation constants (e.g. `MOD_S3`) and derived
   shared-infrastructure constants (`USE_AMQP`, `USE_ENTRAID`, `USE_EVENTHUBS`,
   `USE_SBTOPICS`, `USE_EVENTGRID`).
2. **Conditional `ProjectReference`s** so an unselected module's project is not
   even compiled or referenced — the AOT trimmer never sees its code.

`src/Aws2Azure.Proxy/Program.cs` wraps each module's `using`, DI registration,
registry entry, and any startup probe in the matching `#if` guard. The default
(no `Modules` override) defines every constant, so the all-in-one build is
unchanged.

This keeps composition explicit (no reflection, no assembly scanning — see the
AOT rules in `.github/copilot-instructions.md`) while letting operators trade
generality for footprint when they know their workload.
