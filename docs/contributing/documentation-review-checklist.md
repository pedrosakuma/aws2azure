# Documentation review checklist

Use this checklist when reviewing (or preparing) a pull request that touches
`docs/`, `README.md`, `mkdocs.yml`, `config.schema.json`, `docs/gaps/**`, or any
`tools/Aws2Azure.*` documentation-generation project. It complements — it does
not replace — the automated gates in
[Documentation quality suite](../testing/documentation-quality.md) and the
[ownership/freshness policy](documentation-ownership.md).

- [ ] **Internal links and anchors resolve.** Ran
      `python -m mkdocs build --strict` and
      `python .github/scripts/validate_docs_site.py site /aws2azure/` (or the
      full local command below) with no errors.
- [ ] **New Markdown pages are in the MkDocs nav.** `mkdocs build --strict`
      fails on an omitted nav entry; adding a page without a nav entry is the
      single most common way this suite breaks (see #776's first CI run).
- [ ] **Configuration examples validate.** Any new/changed
      `docs/configuration/examples/*.json`, or fenced `json`/`jsonc` config
      snippet in a guide, validates against `config.schema.json`
      (`tools/Aws2Azure.DocsQuality`).
- [ ] **Copy-paste commands reference real paths.** Any new `dotnet run
      --project`, `dotnet test`, `dotnet publish`, `eng/*.ps1`/`*.sh`, or
      `.github/scripts/*.py` invocation in a fenced shell block points at a
      path that exists (`tools/Aws2Azure.DocsQuality`).
- [ ] **No undated/uncited maturity claims.** Any "GA", "production-ready", or
      "generally available" claim either cites
      `docs/site/workload-ga.json`/live workload certification, or is clearly
      hedged (`candidate`/`conditional`/`blocked`, a "requires"/"until"
      qualifier, or an explicit "historical" framing)
      (`tools/Aws2Azure.DocsEval`).
- [ ] **Gap docs regenerated if source YAML changed.** If `docs/gaps/**`,
      `docs/workloads/**`, or `docs/testing/real-azure-conformance.yaml`
      changed, `dotnet run --project tools/Aws2Azure.GapDocs` was run and the
      diff to `docs/site/**` / `src/Aws2Azure.Core/Generated/` is committed —
      never hand-edited.
- [ ] **Discovery metadata regenerated if the doc tree changed.** If a stable
      document was added, removed, or moved, `dotnet run --project
      tools/Aws2Azure.Documentation` was run and the diff to `llms.txt` /
      `documentation-manifest.json` is committed.
- [ ] **`docs-eval` dataset updated for new adversarial scenarios.** If the
      change introduces a new kind of doc-drift risk (a new workload profile,
      a new authority-precedence edge case, a new fabricated-field pattern),
      add a case to
      `tools/Aws2Azure.DocsEval/Dataset/retrieval-eval-dataset.json` covering
      it.
- [ ] **Ownership table is still accurate.** If this PR adds a new
      documentation area (a new `docs/<area>/` directory, a new generated
      artifact), it is reflected in the
      [ownership/freshness table](documentation-ownership.md).
- [ ] **Prose still matches behavior.** Read the diff as a new reader would:
      the automated gates catch broken links, stale artifacts, and invalid
      structure — they do not catch a paragraph that is well-formed Markdown
      pointing at a real file but describing the *wrong* behavior.

## Running the full suite locally

One command runs every mechanically-enforced gate above:

```bash
pwsh ./eng/validate-docs.ps1
```

See [Documentation quality suite](../testing/documentation-quality.md) for
what it runs and how to interpret a failure.
