# Copilot Instructions — aws2azure

> Read this first. The canonical roadmap lives in
> [Project Roadmap (#16)](https://github.com/pedrosakuma/aws2azure/issues/16)
> and per-task issues under milestones Phase 0–5.

## Project in one paragraph

`aws2azure` is a transparent HTTP proxy that accepts requests in the **AWS wire
protocol** (SigV4 + service-specific payloads) and translates them into
equivalent calls against **Azure REST APIs**. Apps using the official AWS SDK
in any language point `endpoint_url` at the proxy and run against Azure with
no code changes. Direction is AWS → Azure only.

## Locked decisions (do not relitigate without an issue)

| Topic | Decision |
|---|---|
| Direction | AWS → Azure only |
| Runtime | **.NET, Native AOT** |
| Process model | Single binary; services multiplexed by Host header / path |
| Azure integration | **Direct REST calls — no Azure SDK** dependency |
| AWS integration | Wire protocol only — **no AWS SDK** dependency |
| Credential mapping | Static config: `AWS access_key_id → Azure credentials per service` |
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
  Aws2Azure.S3/            # service module (Phase 1)
  Aws2Azure.Sqs/           # service module (Phase 2)
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

## AOT rules (treat as compile errors)

- `<PublishAot>true</PublishAot>`, `<IsAotCompatible>true</IsAotCompatible>`,
  `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` everywhere.
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

## Configuration

- POCOs + `JsonSerializerContext` source-gen. JSON + env-var overrides.
- Shape (see issue #3 for full schema):
  ```jsonc
  {
    "services":   { "s3": { "enabled": true } },
    "credentials":[ { "awsAccessKeyId": "AKIA...", "awsSecretAccessKey": "...",
                      "azure": { "blob": { "accountName": "...", "accountKey": "..." } } } ]
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
- SigV4 validator: must pass the official AWS SigV4 test suite (positive and
  negative vectors).

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

- **`ci.yml`** (PR + push to `main`): build, unit tests, AOT publish. Fast,
  blocking.
- **`integration.yml`**: emulator-backed integration tests. Runs **nightly**
  (cron `0 4 * * *` UTC), on `workflow_dispatch`, and on PRs that carry the
  **`run-integration`** label. Default PR runs do **not** trigger it — apply
  the label when changing SigV4, the Azure REST client, authenticators, or
  any module's emulator-covered code paths to force a run before merge.

## Build / publish (canonical commands)

> Add commands here once the solution exists. Until then, the contract is:

- `dotnet build` — clean, zero warnings (warnings are errors).
- `dotnet test` — unit tests.
- `dotnet publish -c Release -r linux-x64` — single self-contained AOT binary.

## Commits / PRs

- One issue per task (Phase-X labels). Reference the issue in commits/PRs.
- AI-assisted commits include:
  `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`
- Keep PRs scoped to one phase task. Update / add gap-doc YAML in the same PR
  as the code that implements the operation.

## Non-goals (refuse scope creep)

- Not feature-complete with AWS. Gaps are **documented**, not hidden.
- Not a control-plane / IaC tool (use Terraform/Crossplane).
- Not a fork of s3proxy, MinIO, or similar. Conceptual reference only.
- No reverse direction (Azure → AWS). No other clouds.
