# Retrieval evaluation gate

`tools/Aws2Azure.DocsEval` is a deterministic, offline evaluator that checks a
curated question/expected-answer dataset against the current repository
state. It exists to catch the retrieval failure mode described in
[#776](https://github.com/pedrosakuma/aws2azure/issues/776): superficial
retrieval confusing a historical GA claim with live certification, inventing
configuration fields that do not exist, or mistaking emulator-only evidence
for production qualification.

This is **not** a call to a language model. Every check is a mechanical
comparison against a canonical, already-committed source:

- [`docs/site/workload-ga.json`](../site/workload-ga.json) — live workload
  certification verdicts and findings.
- [`docs/gaps/<service>/<Operation>.yaml`](../gaps/README.md) — per-operation
  capability status, loaded with the same `tools/Aws2Azure.GapDocs` loader the
  generator uses.
- [`config.schema.json`](../../config.schema.json) — the canonical operator
  configuration JSON Schema, walked (including `$ref`/`oneOf`/`anyOf`/array
  `items`) to prove a cited field, or auth `mode`/backend `kind` value,
  actually exists (or does not).
- [`docs/site/operations/<service>/<operation>.md`](../site/) — generated
  per-operation reference pages.
- Cited documentation files/text (e.g. `docs/releases/v1.0.0.md`).

It also runs a heuristic scan across hand-authored docs (`README.md`,
`docs/project-maturity.md`, `docs/index.md`, `docs/adoption.md`,
`docs/releases/`, `docs/deployment/`, `docs/workloads/`) for unhedged, uncited
maturity claims ("GA", "production-ready", "generally available") — i.e. a
claim with no nearby `candidate`/`conditional`/`blocked` qualifier, no
"requires"/"until"/"cannot"/"historical" hedge, and no citation to
`docs/site/workload-ga.json` anywhere in the same file. Generated pages under
`docs/site/**` are out of scope for this scan: they already come from the same
canonical inputs this evaluator cross-checks.

## Dataset

The dataset lives at
[`tools/Aws2Azure.DocsEval/Dataset/retrieval-eval-dataset.json`](../../tools/Aws2Azure.DocsEval/Dataset/retrieval-eval-dataset.json).
Each case has:

- `question` — the natural-language question a retriever/model would face.
- `expectedAnswer.summary` / `canonicalSources` / `precedence` — the correct
  answer, the file(s) that hold it, and the precedence rule to apply when
  sources disagree (documented for a future model benchmark; not graded
  verbatim by the deterministic gate).
- `prohibitedConclusions` — conclusions the answer must never draw (e.g. "must
  not claim GA").
- `checks` — one or more mechanical checks (`profile_verdict`,
  `operation_status`, `finding_disposition`, `schema_path_exists`,
  `schema_canonical_value_exists`, `source_exists`, `operation_reference_exists`,
  `text_contains`) that prove the expected answer still holds.

Cases cover all six service modules (S3, SQS, SNS, DynamoDB, Kinesis, Secrets
Manager) and all six required categories (adoption status, configuration,
operation gaps, authentication, deployment, rollback), including deliberately
adversarial cases where a historical release claim ("GA" in
`docs/releases/v1.0.0.md`) now disagrees with the live verdict in
`docs/site/workload-ga.json`.

## Running it locally

```bash
dotnet run --project tools/Aws2Azure.DocsEval
```

Exits `0` with `docs-eval: clean.` when every case's checks pass and no
uncited maturity claim is found; exits non-zero with a diagnostic list of
violations otherwise. It never requires network access or model credentials.

Unit tests for the evaluator logic (schema path resolution, check evaluation,
the maturity-claim heuristic) live at
`tests/Aws2Azure.UnitTests/DocsEval/DocsEvalTests.cs` and run with the rest of
the unit test suite:

```bash
dotnet test tests/Aws2Azure.UnitTests --filter FullyQualifiedName~DocsEval
```

## Optional model benchmarking (out of scope by default)

`tools/Aws2Azure.DocsEval/ModelBenchmarkPlaceholder.cs` is an explicit,
clearly-labeled extension point for actually sending `question` to a language
model and grading its answer against `expectedAnswer`/`prohibitedConclusions`.
It is **not implemented**, is **never invoked** by `Program.cs` or by this
evaluator, and must never be required to build, test, or run the deterministic
gate above. Any future model-benchmarking tool should be a separate,
explicitly-invoked command that requires its own credentials.

## CI wiring

This gate is currently local/manual only. No `.github/workflows/*.yml` file
invokes it yet — wiring it into CI (and choosing which gate/label it belongs
to) is deferred to [#770](https://github.com/pedrosakuma/aws2azure/issues/770),
the designated workflow writer for this wave.
