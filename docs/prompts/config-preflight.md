# AI config pre-flight prompt

Use this before staging or production to have an AI coding agent review an
`aws2azure` binding configuration against the repository's **live**
compatibility docs.

This prompt is intentionally **not** version-locked or freshness-locked. It
always tells the agent to re-read the live published docs for the current run
instead of trusting a frozen snapshot, cached answer, or compiled-in verdict.

```text
You are performing an aws2azure pre-flight compatibility review before a
migration or production rollout.

Inputs:
- Local config file: <path-to-config.json>
- Live docs entrypoint: https://raw.githubusercontent.com/pedrosakuma/aws2azure/main/llms.txt
- Live documentation index: https://pedrosakuma.github.io/aws2azure/documentation-manifest.json

Instructions:
1. Read the local aws2azure config file first.
2. From that config, identify:
   - every enabled service under `services.*.enabled`
   - every configured binding in `bindings[]`
   - the Azure backend `kind`, `target`, and `auth.mode` used for each enabled service
   - service-specific settings that can affect compatibility (for example transport or backend selection)
3. Start with the live `llms.txt` entrypoint and follow its published reading order.
   Use the live published docs for this run; do not rely on cached knowledge,
   prior runs, release notes, embedded snapshots, or memory.
4. Re-fetch and read the live versions of:
   - `docs/project-maturity.md`
   - `docs/site/coverage.md`
   - `docs/site/workload-ga.md`
   - `docs/site/workload-compatibility.md`
   - `docs/site/readiness-checklists/<service>.md` for every enabled service
5. Use `documentation-manifest.json` to resolve stable document paths, IDs,
   revisions, and published URLs when needed.
6. For every enabled service, also read the specific operation pages and
   `docs/site/design-gaps/<service>/*.md` pages referenced by the readiness
   checklist, workload profile, or coverage/workload pages that you cite.
7. Cross-reference the config against the live docs and produce a report that:
   - lists any `partial`, `stub`, or `unsupported` operations in play for the enabled services
   - states the current live workload verdict for each relevant profile using the exact labels `ga`, `candidate`, `conditional`, or `blocked`
   - links the matching readiness checklist and every relevant design-gap or operation page
   - calls out where live docs show missing or stale real-Azure evidence that still requires adopter validation
8. Use aws2azure's normative terms correctly. Do not invent substitute maturity
   wording. Reserve these terms for their documented meanings:
   - `module available`
   - `operation implemented`
   - `real-Azure sealed`
   - `workload conditional`
   - `workload GA`
   When reporting live workload certification, use the exact verdict labels from
   the live workload-GA docs: `ga`, `candidate`, `conditional`, `blocked`.
9. If the config alone does not reveal which AWS operations the application
   actually calls, say so explicitly. Do not claim the workload is safe for
   production. Instead, identify the unknown operations and assumptions the
   adopter still needs to verify against the live docs.
10. Include these sections in the output:
    - Config summary
    - Live documentation inputs used
    - Service-by-service findings
    - Current workload verdicts
    - Readiness checklist and design-gap links
    - Open risks before production
    - Recommended next actions
11. Final rule: always re-fetch and re-read the live docs on every invocation
    because operation coverage and workload-GA verdicts change over time.
```

> [!NOTE]
> Replace `<path-to-config.json>` with the adopter's actual config path. The
> local config file is the only required local input; the compatibility verdicts
> must come from the live published docs discovered through `llms.txt` and
> `documentation-manifest.json`.

## Why a prompt instead of a CLI tool?

This is deliberate, not an omission.

1. **Access-method uncertainty.** Adopters reach `aws2azure` through different
   shapes — Docker, Helm, direct binary use, CI/CD, or sidecar packaging — so a
   single assumed pre-production CLI entrypoint would miss many real workflows.
2. **Workload-GA freshness.** The live workload certification is time-bound,
   including `evaluated_as_of_utc` metadata and 72-hour evidence freshness
   rules. A compiled or embedded snapshot could become stale shortly after a
   build and misstate the current verdict.

Use this prompt as a guided front-end over the generated coverage, workload-GA,
workload-compatibility, readiness-checklist, and design-gap docs; it does not
replace them as the source of truth.
