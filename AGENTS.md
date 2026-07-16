# Agent Instructions — aws2azure

> Read this first. The canonical roadmap lives in
> [Project Roadmap (#16)](https://github.com/pedrosakuma/aws2azure/issues/16)
> and per-task issues under milestones Phase 0–5.

## Project in one paragraph

`aws2azure` is a transparent HTTP proxy that accepts requests in the **AWS wire
protocol** (SigV4 + service-specific payloads) and translates them into
equivalent calls against **Azure REST APIs**. Apps using the official AWS SDK
in any language point `endpoint_url` at the proxy and run against Azure with
no code changes. Direction is AWS → Azure only.

It is meant to be **reusable across diverse deployment scenarios**, not tied to
a single fixed topology. Note it is deliberately **not** an in-process,
per-language library: doing that would mean building and maintaining bindings
for every language. Instead it translates **over the wire** (the AWS SDK
already speaks the wire protocol in every language), so a single sidecar serves
any workload with zero per-language code. Do **not** bake deployment
assumptions (e.g. proxy/backend co-location, a specific Azure region, who
operates it). Questions like "is the proxy co-located with the backend?" have
no single answer — the design response is **configurability + documentation**,
and any capability whose value depends on topology ships **opt-in** with the
tradeoff documented per scenario (see e.g. #267 region-awareness, #268
CosmosBinary).

Because it sits in the request path, the expected deployment shape is a
**sidecar alongside any workload**. That makes **resource efficiency a
first-class premise, not an afterthought**: a large footprint (memory, CPU,
startup time, image size) is a potential adoption blocker. Weigh every new
dependency, cache, buffer, or background thread against the sidecar budget —
small idle memory and fast cold start win over throughput-at-any-cost. This is
the *why* behind the Native AOT / no-Azure-SDK / no-reflection-DI / pooled-buffer
decisions below; uphold them with that intent.

## Locked decisions (do not relitigate without an issue)

| Topic | Decision |
|---|---|
| Direction | AWS → Azure only |
| Deployment model | **Wire-protocol translator, sidecar-deployed — language-agnostic by design.** Deliberately *not* an in-process per-language library (avoids per-language bindings); translates over the wire. No single assumed topology; never bake topology assumptions (co-location, region, operator). Topology-dependent behavior is opt-in + documented per scenario |
| Resource footprint | **Sidecar-first: efficiency is a premise, not an afterthought.** Small idle memory, fast cold start, small image. A large footprint is an adoption blocker — weigh every dependency / cache / buffer / background thread against the sidecar budget |
| Runtime | **.NET, Native AOT** |
| Process model | Single binary; services multiplexed by Host header / path |
| Azure integration | **Direct REST calls — no Azure SDK** dependency |
| AWS integration | Wire protocol only — **no AWS SDK** dependency |
| Credential mapping | Binding-centric config: `bindings[]` map one `aws` identity → `azure.<svc>` backends, each splitting `kind` + `target` (topology) + `auth` (secret) |
| Gap docs | YAML per operation → generated Markdown site (first-class artifact) |
| Reused code | **None** — built from scratch (s3proxy, MinIO, etc. are reference only, never imported) |
| DI | Manual composition or source-gen-friendly subset; no reflection-based DI |
| JSON | `System.Text.Json` with `JsonSerializerContext` source generation |
| XML | `XmlReader` / `XmlWriter` directly. **Never** `XmlSerializer` (reflection-heavy) |
| Logging | `LoggerMessage` source generator |
| Resilience | Manual policies; no Polly |

## Repository layout (target, populated incrementally)

```
src/
  Aws2Azure.Core/          # shared primitives (SigV4, config, REST client, IServiceModule)
  Aws2Azure.Proxy/         # Kestrel host + Program.cs + module registry
  Aws2Azure.Modules.S3/    # service module (Phase 1)
  Aws2Azure.Modules.Sqs/   # service module (Phase 2)
  ...
tests/
  Aws2Azure.UnitTests/
  Aws2Azure.IntegrationTests/
docs/
  gaps/<service>/<Operation>.yaml   # gap docs (source of truth for capability matrix)
  site/                              # generated markdown site
tools/
  Aws2Azure.GapDocs/        # YAML → Markdown generator + CapabilityMatrix codegen
aws2azure.sln
```

## AOT rules (treat as compile errors in shipping projects)

- The proxy publishes with `<PublishAot>true</PublishAot>`. Shipping projects
  inherit `<IsAotCompatible>true</IsAotCompatible>` and
  `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` from
  `Directory.Build.props`.
- Test, benchmark, and tooling projects may explicitly opt out of AOT analysis;
  those exceptions do not relax the shipping-code rules below.
- Zero AOT warnings (`IL2026`, `IL2046`, `IL3050`, etc.). Trim warnings are errors.
- No reflection, no dynamic code generation, no `Activator.CreateInstance` on
  unknown types, no `Type.GetType(string)`, no `Assembly.Load*`.
- No `XmlSerializer`, `DataContractSerializer`, `BinaryFormatter`.
- No reflection-based DI features (typed options scanning, assembly scanning).
  Register everything explicitly in `Program.cs`.
- Source generators required for: `System.Text.Json` contexts, `LoggerMessage`,
  `RegexGenerator` (when regex is unavoidable).
- Prefer `Span<T>`, `ReadOnlySpan<T>`, `Utf8Parser`, `Utf8Formatter`.
- Hot-path allocations: avoid LINQ, avoid boxing, use pooled buffers
  (`ArrayPool<byte>`, `MemoryPool<T>`).

## Service module contract

Every service implements:

```csharp
public interface IServiceModule {
    string ServiceName { get; }                 // "s3", "sqs", ...
    bool MatchesHost(string host);              // routing predicate
    ValueTask HandleAsync(HttpContext ctx);     // entry point after SigV4
    CapabilityMatrix Capabilities { get; }      // generated from docs/gaps/
}
```

- Routing pipeline: **route → SigV4 validate → module.HandleAsync**.
- Modules registered manually in `Program.cs` (no reflection scanning).
- Error responses follow the AWS service's native format (XML for S3, JSON for
  most others). Use shared helpers, not per-module duplication.

### Response writers (the no-sync-IO invariant)

**Never anchor a `Utf8JsonWriter` / `XmlWriter` directly at `context.Response.Body`.**
A `Stream`-backed writer issues *blocking* flushes once its internal buffer fills
(~16 KB), and Kestrel runs with `AllowSynchronousIO=false`, so that sync flush
throws **after** the 200 + first chunk are already committed → the client reads a
truncated body and fails to unmarshal a *successful* response. This bug class is
**size-dependent**, so small-payload functional tests miss it (see #436 / #449).

Two safe patterns:

1. **Full-buffer + single async write (default; the "error wall").** Build the
   whole response into a pooled `Aws2Azure.Core.Buffers.PooledByteBufferWriter`
   (or MemoryStream/StringBuilder), then one
   `context.Response.BodyWriter.WriteAsync(...)`. A mid-serialization throw happens
   *before* any byte is committed, so a clean error response is still possible.
2. **Stream to `BodyWriter` + incremental `FlushAsync`** (only for consistently
   multi-MB, streamable responses). Write the `Utf8JsonWriter` at
   `context.Response.BodyWriter` (a `PipeWriter`/`IBufferWriter<byte>`, async-safe)
   and flush periodically. Lower peak memory / better TTFB, but **forfeits the
   error wall** (Kestrel auto-flushes at 64 KB; a partial 200 can't be undone).

Use pattern 1 by default — sidecar peak memory is the only cost and responses are
bounded. A `SyncThrowingStream`-backed unit test guards each large-response writer
(`tests/Aws2Azure.UnitTests/Http/SyncIoResponseWriterGuardrailTests.cs`).

## Configuration

- POCOs + `JsonSerializerContext` source-gen. JSON + env-var overrides.
- Shape (see issue #3 and #508 for full schema):
  ```jsonc
  {
    "services": { "s3": { "enabled": true } },
    "bindings": [ {
      "aws":   { "accessKeyId": "AKIA...", "secretAccessKey": "..." },
      "azure": { "s3": { "kind": "blob",
                         "target": { "accountName": "..." },
                         "auth":   { "mode": "sharedKey", "key": "..." } } }
    } ]
  }
  ```
- `ICredentialResolver` exposes:
  - `TryGetAwsSecret(accessKeyId, out secret)` — for SigV4.
  - `GetAzureCredentialsFor(accessKeyId, AzureService)` — for downstream calls.
- Validate on startup. Fail loud; never start with partial/ambiguous config.

## Gap docs are mandatory

Every implemented (or stubbed) operation **must** have a YAML at
`docs/gaps/<service>/<Operation>.yaml` with `status`, `sub_features`,
`behavior_differences`, `references`. The build:

1. Validates every YAML against the schema (CI fails on violations).
2. Renders `docs/site/` Markdown coverage matrix.
3. Generates `CapabilityMatrix` constants consumed by each module.

The YAML is the **single source of truth** — code reads from it, not the
reverse.

Regenerate after editing any YAML:

```
dotnet run --project tools/Aws2Azure.GapDocs
```

CI (`.github/workflows/gap-docs.yml`) runs the tool with `--validate` and
then re-runs it without flags to ensure the committed `docs/site/` and
`src/Aws2Azure.Core/Generated/CapabilityRegistry.g.cs` are up to date.

## Testing

- Unit tests: xUnit, AOT-compatible (no Moq with `Castle.DynamicProxy`; prefer
  hand-rolled fakes or `NSubstitute` only where AOT-safe and only in test
  projects which do not need AOT publish).
- Integration tests: real boto3 + AWSSDK.NET hitting the in-process proxy
  against Azurite / Service Bus emulator / Cosmos DB emulator via
  `Testcontainers`. See issue #7.
- SigV4 validator: preserve the AWS-published `get-vanilla` vector plus the
  repository's positive and negative validator/canonicalization coverage. Add
  further official vectors when importing their fixtures.

### Emulator caveat (read this)

The integration suite runs against **Azure emulators** (Azurite, Service Bus
emulator, Cosmos DB emulator) to keep the feedback loop fast. **Emulators are
not behavior-equivalent to real Azure** — they diverge on areas like
consistency, throttling, auth edge cases, and feature surface. Therefore:

- Treat "passes against emulator" as a **necessary, not sufficient**, signal.
- Any operation whose acceptance has only been verified against an emulator
  must say so in its gap-doc YAML (`behavior_differences` / `notes`).
- Before an operation is marked `status: implemented` in its gap doc, it must
  also be exercised against **real Azure** at least once (manual or scheduled
  job) and any divergences recorded.

### CI cadence

- **`ci.yml`** (PR + push to `main`): build, unit tests, gap-doc validation,
  Tier-1 conformance, and AOT publish. Blocking.
- **`integration.yml`**: emulator-backed integration tests. Runs **nightly**
  (cron `0 4 * * *` UTC), on `workflow_dispatch`, and automatically when a PR
  changes shipping code or its integration infrastructure. The
  **`run-integration`** label remains an explicit override.

## Build / publish (canonical commands)

Use the repository validation entrypoint so local and agent runs match CI:

- `pwsh ./eng/validate.ps1 fast` — release build plus offline conformance;
  suitable for a quick cross-cutting check. Run focused unit tests for the
  changed area before it.
- `pwsh ./eng/validate.ps1 pr` — release build, all unit tests, Tier-1
  conformance, deterministic perf-harness tests (emulator scenarios self-skip),
  and gap-doc validation. It also publishes AOT on Linux.
  Non-Linux hosts skip AOT unless requested because Native AOT requires a
  platform toolchain; CI passes `linux-x64 -RequireAot` explicitly. Run before
  declaring a non-trivial change complete.
- `pwsh ./eng/validate.ps1 unit|conformance|integration|perf|footprint|aot` —
  run one harness explicitly. Integration/perf/footprint retain their existing
  Docker and environment requirements.

Do not use solution-level `dotnet test` as shorthand for unit tests: the
solution also contains integration, performance, benchmark, and footprint
projects.

## Commits / PRs

- One issue per task (Phase-X labels). Reference the issue in commits/PRs.
- AI-assisted commits include:
  `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`
- Keep PRs scoped to one phase task. Update / add gap-doc YAML in the same PR
  as the code that implements the operation.

## Agent workflow conventions

Repo-wide meta-workflows that have paid off here. Declarative on purpose —
the goal is to bias decisions, not to script every turn. Skip them when the
task is genuinely trivial (typo fix, one-line config, doc-only).

- **One session/worktree = one branch = one PR.** A session is the sole writer
  for its worktree and branch. Never let two agents edit the same checkout,
  move commits between agent-owned branches, or reuse a worktree for a second
  PR. The coordinator owns decomposition, branch dependencies, validation
  labels, shared-file assignments, merge order, and final merges; worker
  sessions implement and validate their assigned PR but do not merge it.
- **Keep the active wave small.** Prefer 3–4 active PRs at a time. Fan out only
  work that can merge independently without shared files or schema/generated
  dependencies. Stack PRs only for real code dependencies, state the parent
  branch explicitly, and retarget/rebase each surviving PR onto `main` as its
  predecessors merge.
- **Single writer for shared surfaces.** In each wave the coordinator assigns
  exactly one branch to workflows, Bicep/deployment templates, gap manifests
  and their generated outputs, qualification/conformance matrices, and
  performance/footprint baselines or histories. Other branches treat those
  files as read-only and report the needed follow-up to the coordinator.
- **Rebase, then regenerate.** The assigned gap-doc writer fetches and rebases
  onto current `origin/main`, discards branch-side edits to `docs/site/*` and
  `src/Aws2Azure.Core/Generated/CapabilityRegistry.g.cs`, runs
  `dotnet run --project tools/Aws2Azure.GapDocs`, and reviews the regenerated
  diff. Never hand-merge generated output. Dependent branches rebase after the
  generator-owning PR merges and regenerate again only if they own new source
  YAML changes.
- **Classify validation mechanically.** After fetching `main`, run
  `dotnet run --project tools/Aws2Azure.ChangeAwareValidation -- --base main --pretty`.
  Its JSON lists every gate as `required`, `optional`, or `not-applicable` and
  returns the labels required by the diff. Hot request paths require
  `run-perf`; authentication and transport require `run-integration` plus
  `run-real-azure`; startup, packaging, and build-graph changes require
  `run-footprint`. Apply every returned required label before merge.
- **Diagnose regressions against `main`.** Reproduce a failed gate on the
  merge-base/current `main` under the same runner, dependencies, configuration,
  and backend before calling it a PR regression. Never automatically raise a
  threshold, rewrite a baseline/qualification, or repeatedly rerun a failure.
  Investigate first; permit at most one evidence-backed diagnostic rerun when
  there is a concrete flake hypothesis.
- **Independent review before merging a non-trivial PR.** Review the branch
  diff against `main` with a separate reviewer context (human or
  read-only code-review agent), address every real finding, and let required
  repository checks enforce merge readiness. Do not make the policy depend on
  one vendor/model name, and do not bypass branch protection with an admin
  merge. Skip only for docs-only or one-line configuration PRs.
- **Decompose-then-parallelise.** Phases here tend to land as several
  small, independent PRs (Phase 2.7 shipped as 6 stacked PRs). When work
  decomposes into ≥2 independent trails (different modules, different test
  surfaces, no shared schema migration), prefer dispatching one background
  sub-agent per trail (`task` with `mode: "background"`) over serialising
  them in the main loop. Main loop keeps coordination + code review; the
  sub-agents own implementation.
- **Pre-scope fuzzy work with a `research` or `explore` agent.** For
  multi-day items (new service module, new wire-protocol variant, perf
  investigation across modules), dispatch a sub-agent for survey +
  feasibility before drafting the plan. Saves the main context for actual
  design + execution.
- **Don't reach for a sub-agent when a single tool call would do.** Simple
  lookups (one grep, one file read), pointed edits, and any interactive
  debugging stay in the main loop — sub-agent fidelity loss is not worth
  it.
- **Stacked-PR merge etiquette.** When a chain of dependent PRs lands
  together, retarget each PR's base to `main` BEFORE merging (or merge in
  reverse via rebase). Naively merging in author order
  can orphan commits into intermediate bases and force cherry-pick recovery
  (this has bitten this repo — see PR #94..#100 retrospective).
- **Worktree etiquette for parallel work.** Give each implementation trail an
  isolated worktree rooted at `origin/main`; use an OS-appropriate temporary
  path (`$TMPDIR`/`/tmp` on Unix, `$env:TEMP` on Windows). Push the branch
  before handing it to a remote agent. After merge, remove its worktree before
  deleting the branch because Git cannot delete a branch held by a worktree.
- **Perf claims must cite the harness.** Any throughput / latency number
  in a PR description, gap doc, or commit message must reference the
  scenario in `tests/Aws2Azure.PerfTests` that produced it and note that
  numbers are emulator-bound unless explicitly validated against real
  Azure (see emulator caveat above).
- **Perf regression gate.** `PerfResult.AssertNoRegression()` reads
  `docs/perf/baseline-reference.json` (per-scenario `minThroughputPerSec`
  / `maxP99Ms` floors and ceilings) and fails the run on degradation.
  Every perf test must call `AssertNoRegression()` right after
  `AssertHealthy()`. Every static scenario must be registered in
  `KnownPerfScenariosTests.All` and in the reference JSON; use zero thresholds
  as an explicit temporary waiver rather than silently omitting it. Bump values
  deliberately, never by accident, when a code change is *expected* to raise
  or lower expected throughput. `docs/perf/baseline-latest.md` is the live
  snapshot (rows merged in place by scenario name) and `docs/perf/history.csv`
  is the cumulative append-only trend.
- **Perf CI cadence.** `.github/workflows/perf.yml` runs the harness on
  nightly cron (05:30 UTC), `workflow_dispatch`, and automatically for PRs
  changing shipping code or emulator infrastructure. Deterministic perf-harness
  tests run in `ci.yml`; the `run-perf` label remains an explicit override for
  harness/baseline changes that need an end-to-end measurement.

User- or task-scoped preferences ("for this PR skip the review", "I
prefer option X") belong in the prompt, not here. Conventions in this
section apply to every contributor and every agent on this repo.

## Non-goals (refuse scope creep)

- Not feature-complete with AWS. Gaps are **documented**, not hidden.
- Not a control-plane / IaC tool (use Terraform/Crossplane).
- Not a fork of s3proxy, MinIO, or similar. Conceptual reference only.
- No reverse direction (Azure → AWS). No other clouds.
