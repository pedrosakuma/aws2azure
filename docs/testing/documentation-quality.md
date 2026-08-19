# Documentation quality suite

Issue [#770](https://github.com/pedrosakuma/aws2azure/issues/770) unifies the
previously separate documentation checks (internal links, generated-artifact
freshness, schema-valid examples, undated maturity claims) into one local
command and integrates
[#776](https://github.com/pedrosakuma/aws2azure/issues/776)'s
`tools/Aws2Azure.DocsEval` retrieval-evaluation gate into CI. Gap-doc
freshness continues to run in its own path-filtered `gap-docs` workflow
rather than being duplicated in the `documentation` workflow (see the table
below).

## Running the complete suite locally

```bash
pwsh ./eng/validate-docs.ps1
```

This is the **one command** that runs every gate below in one pass — the
`documentation` and `gap-docs` CI workflows enforce the same gates, split
across two workflows for path-scoping reasons (see the gap-doc freshness row
below). It requires the .NET SDK on `PATH` (already
required by the rest of the repo) and a Python virtual environment with
[`requirements-docs.txt`](../../requirements-docs.txt) installed at `.venv`:

```bash
python3 -m venv .venv
.venv/bin/python -m pip install -r requirements-docs.txt
pwsh ./eng/validate-docs.ps1
```

Pass `-SkipPython` to skip the MkDocs build and site validation steps (for a
fast dotnet-only pass while iterating); do not treat a `-SkipPython` run as a
green result before opening a PR.

## What it checks

| Gate | Tool | Catches |
|---|---|---|
| Documentation discovery drift | `tools/Aws2Azure.Documentation -- --check` | `llms.txt` / `documentation-manifest.json` out of date relative to the documentation tree. |
| Configuration examples and copy-paste commands | `tools/Aws2Azure.DocsQuality` | A committed `docs/configuration/examples/*.json`, or a fenced `json`/`jsonc` snippet that looks like a full config document, that no longer validates against `config.schema.json`; a `dotnet run --project`/`dotnet test`/`dotnet publish`/`eng/*`/`.github/scripts/*` command referencing a path that no longer exists. |
| Retrieval-evaluation dataset and maturity claims | `tools/Aws2Azure.DocsEval` | A dataset case whose expected answer now disagrees with live workload certification, gap-doc status, or `config.schema.json`; any hand-authored doc under `docs/` or `README.md` stating an unhedged, uncited "GA"/"production-ready"/"generally available" claim. |
| Gap-doc schema and generated-artifact freshness | `tools/Aws2Azure.GapDocs -- --validate` plus a regenerate-and-diff check | Invalid gap-doc YAML; `docs/site/**` or `src/Aws2Azure.Core/Generated/CapabilityRegistry.g.cs` out of date relative to their YAML/schema sources. This is already enforced in CI by the path-filtered `gap-docs` workflow (which triggers on any change under `docs/gaps/**`, `docs/workloads/**`, `docs/site/**`, `tools/Aws2Azure.GapDocs/**`, or `src/Aws2Azure.Core/Generated/**`); the local script runs it too so a single command covers everything, but it is intentionally **not** duplicated in the unconditional `documentation` CI workflow. |
| MkDocs strict build | `python -m mkdocs build --strict` | Missing navigation entries, unresolved internal links, unresolved anchors, and other structural issues across the entire hand-authored plus generated documentation tree. |
| Built-site validation | `.github/scripts/validate_docs_site.py` | Broken internal links/assets/fragments in the *built* HTML, missing discovery artifacts, and representative search-index coverage. |

Configuration-schema and configuration-reference staleness
(`config.schema.json`, `docs/configuration-reference.md`) is already covered
by the `Generated_schema_matches_committed_artifact` and
`Generated_configuration_reference_matches_committed_artifact` unit tests in
`tests/Aws2Azure.UnitTests/Configuration/ConfigSchemaTests.cs`, which run with
the rest of the unit suite (`pwsh ./eng/validate.ps1 pr` or `dotnet test
tests/Aws2Azure.UnitTests --filter FullyQualifiedName~ConfigSchema`) — it is
not duplicated here.

## CI wiring

The `documentation` workflow (`.github/workflows/documentation.yml`) runs this
same sequence — `tools/Aws2Azure.Documentation --check`,
`tools/Aws2Azure.DocsQuality`, `tools/Aws2Azure.DocsEval`, the gap-doc
regenerate-and-diff check, the MkDocs strict build, and
`validate_docs_site.py` — on every pull request and every push to `main`, so a
broken link, a stale generated artifact, an invalid configuration example, or
a prohibited undated maturity claim fails CI before merge. Every gate here is
deterministic and bounded: no network access, no language model calls, and a
fixed job timeout.

See [Retrieval evaluation gate](retrieval-eval.md) for `tools/Aws2Azure.DocsEval`
specifics and [Documentation portal](../contributing/documentation.md) for the
MkDocs build/link-validation details. See
[Documentation ownership and freshness policy](../contributing/documentation-ownership.md)
and the
[documentation review checklist](../contributing/documentation-review-checklist.md)
for what is reviewed by humans rather than mechanically enforced.
