---
name: aws2azure-context-map
description: Map the minimum code, documentation, generated, ownership, and validation surfaces needed before a non-trivial or initially fuzzy aws2azure change.
---

# Aws2Azure context map

Use this skill before editing when the task is non-trivial, spans multiple
files, or starts without a precise implementation surface. Skip it for pointed
single-file work.

## Procedure

1. State the requested outcome and the behavior that must remain unchanged.
2. Locate prior art using the narrowest available search. Follow one continuous
   execution trace directly; delegate only genuinely independent research or
   implementation trails.
3. Build a concise map:

   | Surface | Identify |
   |---|---|
   | Primary | Files that directly implement the requested behavior |
   | Dependent | Callers, contracts, configuration, tests, and documentation that must remain consistent |
   | Generated | Source inputs and their generated outputs; never plan hand edits to generated files |
   | Shared | Workflows, Bicep, gap manifests, qualification matrices, and baselines that require a single writer |
   | Validation | Smallest focused test plus likely integration, real-Azure, performance, footprint, AOT, or documentation gates |

4. Check whether gap YAML, workload YAML, documentation discovery, or generated
   capability artifacts are part of the change. For any gap or workload YAML
   change, map the canonical-input revision in
   `docs/workloads/certification/authority.yaml`, GapDocs outputs, and the
   Documentation outputs (`documentation-manifest.json` and `llms.txt`) rather
   than only the visible operation page.
5. Identify external facts that can become stale. Use one authoritative
   external lookup when the current fact materially changes the implementation
   or validation plan; avoid broad multi-source research when the issue and
   repository already bound the decision. Record the source and verification
   date instead of treating an old claim as current.
6. Split work only when trails can use separate sessions, worktrees, branches,
   and PRs without overlapping writers. Otherwise retain one continuous trace.
7. Stop exploring once the primary path, dependent surfaces, ownership, and
   validation requirements are supported by repository evidence.

## Output

Return the map and any genuine blocker or unresolved design decision. Do not
produce a long research diary, copy repository policy into the output, or
claim that a validation passed before it ran.
