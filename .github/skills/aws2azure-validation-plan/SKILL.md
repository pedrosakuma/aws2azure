---
name: aws2azure-validation-plan
description: Select and interpret focused aws2azure validation after a diff stabilizes, including required labels, paid-gate authorization, generated drift, and bounded failure diagnosis.
---

# Aws2Azure validation plan

Use this skill either to forecast validation while planning or to classify a
stable diff before declaring a non-trivial change complete.

## Procedure

1. Select the mode:
   - **Forecast:** no stable diff exists. Map likely gates from the intended
     surfaces, label every result as provisional, and do not claim required
     labels or successful validation.
   - **Completion:** the intended diff is stable. Inspect the actual changed
     files and classify the behavior affected.
2. Run the smallest focused test that exercises the changed behavior. Escalate
   only when its result or the repository contract requires a broader harness.
   In forecast mode, name the focused test without running it.
3. In completion mode, after fetching `main`, run:

   ```text
   dotnet run --project tools/Aws2Azure.ChangeAwareValidation -- --base main --pretty
   ```

   In forecast mode, identify the likely classifier triggers and state that the
   command must be rerun against the eventual diff.
4. In completion mode, read the JSON plan as authoritative classification:
   - account for every `required`, `optional`, and `not-applicable` gate;
   - apply every effective `requiredLabels` entry before merge;
   - never treat a required paid gate as spending authorization;
   - report a paid gate as blocked when operator authorization is absent.
5. Check regeneration and drift requirements from the changed sources:
   - any content change under `docs/gaps/**/*.yaml` or
     `docs/workloads/**/*.yaml` changes the canonical-input revision: update
     `docs/workloads/certification/authority.yaml`
     `expected_canonical_inputs_revision`, run
     `dotnet run --project tools/Aws2Azure.GapDocs`, then run
     `dotnet run --project tools/Aws2Azure.Documentation`;
   - review all resulting GapDocs and Documentation outputs, including
     `docs/site/*`, generated capability/workload artifacts,
     `documentation-manifest.json`, and `llms.txt`;
   - generated files must come from their source generator, never hand edits;
   - startup or build-graph changes require footprint consideration;
   - hot paths require performance consideration;
   - authentication or transport changes require integration and real-Azure
     consideration.
6. For a failure, compare the same gate with the merge base or current `main`
   under equivalent conditions before calling it a regression. Retry at most
   once, and only after identifying a concrete transient hypothesis.
7. Obtain an independent diff review for non-trivial changes and address every
   real finding. Deterministic repository checks remain the final authority.

## Output

State whether the plan is forecast or completion. Report focused checks planned
or run, projected or required gates and labels, paid authorization blockers,
generated-artifact actions, and unresolved failures. Do not present forecast
classification, accepted evidence, or an unexecuted check as a passed result,
and do not silently omit a required gate.
