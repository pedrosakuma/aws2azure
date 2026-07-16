using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Aws2Azure.GapDocs;

public static class MarkdownRenderer
{
    public static void Render(
        IReadOnlyList<OperationDoc> docs,
        IReadOnlyList<ServiceDesignDoc> designDocs,
        RealAzureMigrationDoc migration,
        string siteRoot)
    {
        Directory.CreateDirectory(siteRoot);

        var byService = docs
            .GroupBy(d => d.Service.ToLowerInvariant())
            .OrderBy(g => g.Key, System.StringComparer.Ordinal)
            .ToList();

        WriteIndex(byService, designDocs, siteRoot);
        WriteCoverage(byService, siteRoot);
        WriteWorkloadCompatibility(byService, designDocs, siteRoot);
        WriteDivergences(byService, migration, siteRoot);
        WriteDesignGaps(designDocs, siteRoot);
        foreach (var group in byService)
        {
            WriteServicePage(group.Key, group.OrderBy(o => o.Operation, System.StringComparer.Ordinal).ToList(), siteRoot);
        }
    }

    private static void WriteIndex(IList<IGrouping<string, OperationDoc>> byService, IReadOnlyList<ServiceDesignDoc> designDocs, string siteRoot)
    {
        var designByService = designDocs
            .GroupBy(d => d.Service.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.SelectMany(d => d.DesignGaps).Count(), System.StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine("# aws2azure — gap documentation");
        sb.AppendLine();
        sb.AppendLine("Authoritative inventory of which AWS operations the proxy translates, with the Azure mapping and the known behavioural gaps.");
        sb.AppendLine();
        sb.AppendLine("Start with the [coverage matrix](coverage.md) for a one-screen overview, then drill");
        sb.AppendLine("into a service for per-operation detail. Cross-cutting, architectural limitations");
        sb.AppendLine("that do not map to a single operation live in [design gaps](design-gaps.md).");
        sb.AppendLine();
        sb.AppendLine("## Services");
        sb.AppendLine();
        foreach (var group in byService)
        {
            var extra = designByService.TryGetValue(group.Key, out var n) && n > 0
                ? $", {n} design gap(s)"
                : string.Empty;
            sb.AppendLine($"- [{group.Key}]({group.Key}.md) — {group.Count()} operation(s){extra}");
        }
        sb.AppendLine();
        sb.AppendLine("## Cross-cutting");
        sb.AppendLine();
        sb.AppendLine("- [Coverage matrix](coverage.md) — every operation and status on one screen.");
        sb.AppendLine("- [Workload compatibility](workload-compatibility.md) — adoption patterns and go/no-go guidance.");
        sb.AppendLine("- [Design gaps](design-gaps.md) — architectural limitations spanning operations.");
        sb.AppendLine("- [Real-Azure conformance & divergences](divergences.md) — verification state.");
        File.WriteAllText(Path.Combine(siteRoot, "index.md"), sb.ToString());
    }

    private static void WriteCoverage(IList<IGrouping<string, OperationDoc>> byService, string siteRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Coverage matrix");
        sb.AppendLine();
        sb.AppendLine("For adoption decisions, start with the generated [workload compatibility](workload-compatibility.md) guide.");
        sb.AppendLine();
        sb.AppendLine("| Service | Operation | Status | Real-Azure | Azure equivalent |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var group in byService)
        {
            foreach (var op in group.OrderBy(o => o.Operation, System.StringComparer.Ordinal))
            {
                sb.AppendLine($"| {op.Service} | [{op.Operation}]({group.Key}.md#{op.Operation.ToLowerInvariant()}) | {StatusBadge(op.Status)} | {Seal(op.VerifiedRealAzure)} | `{op.AzureEquivalent}` |");
            }
        }
        File.WriteAllText(Path.Combine(siteRoot, "coverage.md"), sb.ToString());
    }

    private static void WriteWorkloadCompatibility(
        IList<IGrouping<string, OperationDoc>> byService,
        IReadOnlyList<ServiceDesignDoc> designDocs,
        string siteRoot)
    {
        var operationsByService = byService.ToDictionary(
            g => g.Key,
            g => g.ToDictionary(o => o.Operation, System.StringComparer.OrdinalIgnoreCase),
            System.StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.AppendLine("# Workload compatibility");
        sb.AppendLine();
        sb.AppendLine("Use this page before adopting the proxy. A module being available means it can");
        sb.AppendLine("route that AWS wire protocol; it does **not** mean full AWS service parity.");
        sb.AppendLine("The assessments below are generated from the operation and design-gap YAMLs.");
        sb.AppendLine();
        sb.AppendLine("Legend: ✅ supported · 🟡 conditional · ⛔ blocked");
        sb.AppendLine();
        sb.AppendLine("## Service coverage profile");
        sb.AppendLine();
        sb.AppendLine("| Service | Module | Implemented | Partial | Stub | Unsupported | Real-Azure sealed |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|");
        foreach (var group in byService)
        {
            var ops = group.ToList();
            sb.AppendLine(
                $"| [{group.Key}]({group.Key}.md) | Available | " +
                $"{CountStatus(ops, "implemented")} | {CountStatus(ops, "partial")} | " +
                $"{CountStatus(ops, "stub")} | {CountStatus(ops, "unsupported")} | " +
                $"{ops.Count(o => o.VerifiedRealAzure is not null)}/{ops.Count} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Adoption decision");
        sb.AppendLine();
        sb.AppendLine("1. Find the closest workload pattern below.");
        sb.AppendLine("2. Confirm every operation your application calls in the [coverage matrix](coverage.md).");
        sb.AppendLine("3. Read each linked design gap and decide whether its workaround is acceptable.");
        sb.AppendLine("4. Treat missing real-Azure seals as validation work required in your own staging environment.");
        sb.AppendLine("5. Stop the migration when a required pattern is blocked; do not assume the proxy emulates it.");
        sb.AppendLine();
        sb.AppendLine("## Automated workload check");
        sb.AppendLine();
        sb.AppendLine("Create a versioned manifest that lists every AWS operation the application calls");
        sb.AppendLine("and enables the contextual requirement IDs from the profiles below:");
        sb.AppendLine();
        sb.AppendLine("```yaml");
        sb.AppendLine("schema_version: 1");
        sb.AppendLine("workload: checkout");
        sb.AppendLine("operations:");
        sb.AppendLine("  - dynamodb:TransactWriteItems");
        sb.AppendLine("  - sqs:SendMessage");
        sb.AppendLine("requirements:");
        sb.AppendLine("  cross_partition_transactions: true");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Run a human-readable discovery report:");
        sb.AppendLine();
        sb.AppendLine("```bash");
        sb.AppendLine("dotnet run --project tools/Aws2Azure.GapDocs -- check-workload workload.yaml");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("For CI, emit source-generated JSON and opt into a non-zero exit code when");
        sb.AppendLine("the valid workload contains blockers:");
        sb.AppendLine();
        sb.AppendLine("```bash");
        sb.AppendLine("dotnet run --project tools/Aws2Azure.GapDocs -- check-workload workload.yaml \\");
        sb.AppendLine("  --format json --output compatibility.json --fail-on-blocked");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Exit code `0` means the report was produced, `1` means the manifest or command");
        sb.AppendLine("was invalid, and `2` means `--fail-on-blocked` found at least one blocker.");
        sb.AppendLine("A `conditional` result does not fail CI; its guidance and workarounds require");
        sb.AppendLine("an explicit migration decision.");
        sb.AppendLine();

        foreach (var serviceDoc in designDocs.OrderBy(d => d.Service, System.StringComparer.Ordinal))
        {
            if (serviceDoc.WorkloadPatterns.Count == 0) continue;
            operationsByService.TryGetValue(serviceDoc.Service, out var serviceOperations);
            var gapsByArea = serviceDoc.DesignGaps.ToDictionary(
                g => g.Area,
                System.StringComparer.OrdinalIgnoreCase);

            sb.AppendLine($"## {serviceDoc.Service.ToLowerInvariant()}");
            sb.AppendLine();
            sb.AppendLine("| Workload pattern | Assessment | Operation coverage | Real-Azure | Decision guidance | Requirement ID |");
            sb.AppendLine("|---|---|---|---:|---|---|");
            foreach (var pattern in serviceDoc.WorkloadPatterns)
            {
                var referencedOperations = pattern.Operations
                    .Where(name => serviceOperations is not null && serviceOperations.ContainsKey(name))
                    .Select(name => serviceOperations![name])
                    .ToList();
                var coverage = referencedOperations.Count == 0
                    ? "Design-level requirement"
                    : string.Join(", ", referencedOperations
                        .GroupBy(o => o.Status.ToLowerInvariant())
                        .OrderBy(g => StatusOrder(g.Key))
                        .Select(g => $"{g.Count()} {g.Key}"));
                var seals = referencedOperations.Count == 0
                    ? "—"
                    : $"{referencedOperations.Count(o => o.VerifiedRealAzure is not null)}/{referencedOperations.Count}";
                var details = new List<string> { Esc(pattern.Summary), Esc(pattern.Guidance) };
                foreach (var operation in referencedOperations.Where(o => !o.Status.Equals("implemented", System.StringComparison.OrdinalIgnoreCase)))
                {
                    details.Add($"[{operation.Operation}]({serviceDoc.Service.ToLowerInvariant()}.md#{operation.Operation.ToLowerInvariant()}) is {operation.Status}");
                }
                foreach (var area in pattern.DesignGaps)
                {
                    if (gapsByArea.TryGetValue(area, out var gap))
                    {
                        details.Add($"[Design gap](design-gaps.md#{DocumentationLinks.Anchor(serviceDoc.Service + "-" + area)}): {gap.Area}");
                    }
                }
                sb.AppendLine(
                    $"| {Esc(pattern.Name)} | {CompatibilityBadge(pattern.Compatibility)} | " +
                    $"{coverage} | {seals} | {string.Join("<br>", details)} | `{pattern.Id}` |");
            }
            sb.AppendLine();
        }

        File.WriteAllText(Path.Combine(siteRoot, "workload-compatibility.md"), sb.ToString());
    }

    private static void WriteServicePage(string service, IList<OperationDoc> ops, string siteRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {service}");
        sb.AppendLine();
        foreach (var op in ops)
        {
            sb.AppendLine($"## {op.Operation}");
            sb.AppendLine();
            sb.AppendLine($"- **Status:** {StatusBadge(op.Status)}");
            sb.AppendLine($"- **Azure equivalent:** `{op.AzureEquivalent}`");
            if (op.VerifiedRealAzure is not null)
            {
                sb.AppendLine($"- **Real-Azure verified:** ✅ {VerificationDetails(op.VerifiedRealAzure)}");
            }
            sb.AppendLine();

            if (op.SubFeatures.Count > 0)
            {
                sb.AppendLine("### Sub-features");
                sb.AppendLine();
                sb.AppendLine("| Name | Status | Real-Azure | Notes | Gap | Workaround |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var sf in op.SubFeatures)
                {
                    sb.AppendLine($"| {sf.Name} | {StatusBadge(sf.Status)} | {Seal(sf.VerifiedRealAzure)} | {Esc(sf.Notes)} | {Esc(sf.Gap)} | {Esc(sf.Workaround)} |");
                }
                sb.AppendLine();
            }

            if (op.BehaviorDifferences.Count > 0)
            {
                sb.AppendLine("### Behaviour differences");
                sb.AppendLine();
                foreach (var bd in op.BehaviorDifferences) sb.AppendLine($"- {bd}");
                sb.AppendLine();
            }

            if (op.References.Count > 0)
            {
                sb.AppendLine("### References");
                sb.AppendLine();
                foreach (var r in op.References) sb.AppendLine($"- <{r}>");
                sb.AppendLine();
            }
        }
        File.WriteAllText(Path.Combine(siteRoot, service + ".md"), sb.ToString());
    }

    // Cross-cutting design gaps: architectural limitations that span operations
    // (consistency model, transaction scope, absent control-plane surfaces...).
    // Rendered as its own page so the per-operation matrix stays focused and the
    // reader can drill in only when they need the "why can't I do X at all" story.
    private static void WriteDesignGaps(IReadOnlyList<ServiceDesignDoc> designDocs, string siteRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Design gaps");
        sb.AppendLine();
        sb.AppendLine("Architectural limitations that do **not** map to a single operation — the");
        sb.AppendLine("consistency model, transaction scope, and control-plane surfaces that differ");
        sb.AppendLine("between the AWS service and its Azure target. Per-operation behaviour lives on");
        sb.AppendLine("each [service page](index.md); this page is the cross-cutting story.");
        sb.AppendLine();
        sb.AppendLine("Legend: 🔵 by design · 🟡 partial · ⛔ unsupported · 🗓️ planned");
        sb.AppendLine();

        var ordered = designDocs
            .OrderBy(d => d.Service.ToLowerInvariant(), System.StringComparer.Ordinal)
            .ToList();

        if (ordered.Count == 0)
        {
            sb.AppendLine("_No design gaps documented yet._");
            File.WriteAllText(Path.Combine(siteRoot, "design-gaps.md"), sb.ToString());
            return;
        }

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Service | Area | Status |");
        sb.AppendLine("|---|---|---|");
        foreach (var doc in ordered)
        {
            foreach (var g in doc.DesignGaps)
            {
                sb.AppendLine($"| [{doc.Service.ToLowerInvariant()}](#{doc.Service.ToLowerInvariant()}) | {Esc(g.Area)} | {DesignBadge(g.Status)} |");
            }
        }
        sb.AppendLine();

        foreach (var doc in ordered)
        {
            sb.AppendLine($"## {doc.Service.ToLowerInvariant()}");
            sb.AppendLine();
            foreach (var g in doc.DesignGaps)
            {
                sb.AppendLine($"<a id=\"{DocumentationLinks.Anchor(doc.Service + "-" + g.Area)}\"></a>");
                sb.AppendLine();
                sb.AppendLine($"### {Esc(g.Area)}");
                sb.AppendLine();
                sb.AppendLine($"- **Status:** {DesignBadge(g.Status)}");
                sb.AppendLine();
                sb.AppendLine(g.Summary);
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(g.Impact))
                {
                    sb.AppendLine($"**Impact.** {g.Impact}");
                    sb.AppendLine();
                }
                if (!string.IsNullOrWhiteSpace(g.Workaround))
                {
                    sb.AppendLine($"**Workaround.** {g.Workaround}");
                    sb.AppendLine();
                }
                if (g.References.Count > 0)
                {
                    sb.AppendLine("References:");
                    sb.AppendLine();
                    foreach (var r in g.References) sb.AppendLine($"- <{r}>");
                    sb.AppendLine();
                }
            }
        }

        File.WriteAllText(Path.Combine(siteRoot, "design-gaps.md"), sb.ToString());
    }

    private static string DesignBadge(string status) => status.ToLowerInvariant() switch
    {
        "by_design" => "🔵 by design",
        "partial" => "🟡 partial",
        "unsupported" => "⛔ unsupported",
        "planned" => "🗓️ planned",
        _ => status
    };

    private static string StatusBadge(string status) => status.ToLowerInvariant() switch
    {
        "implemented" => "✅ implemented",
        "partial" => "🟡 partial",
        "stub" => "⚪ stub",
        "unsupported" => "⛔ unsupported",
        _ => status
    };

    private static string Seal(RealAzureVerification? verified) => verified is null ? "—" : "✅";

    private static string CompatibilityBadge(string compatibility) => compatibility.ToLowerInvariant() switch
    {
        "supported" => "✅ supported",
        "conditional" => "🟡 conditional",
        "blocked" => "⛔ blocked",
        _ => compatibility
    };

    private static int CountStatus(IEnumerable<OperationDoc> docs, string status) =>
        docs.Count(o => o.Status.Equals(status, System.StringComparison.OrdinalIgnoreCase));

    private static int StatusOrder(string status) => status switch
    {
        "implemented" => 0,
        "partial" => 1,
        "stub" => 2,
        "unsupported" => 3,
        _ => 4
    };

    // Theme C divergence report: a one-screen dossier of every documented
    // behaviour difference plus the real-Azure verification state. The
    // emulator caveat says nothing is trustworthy as "implemented" without a
    // real-Azure seal, so implemented-but-unsealed ops are surfaced as the
    // backlog to close. Generated alongside the site so the conformance
    // workflow can upload it as the run's divergence artifact.
    private static void WriteDivergences(
        IList<IGrouping<string, OperationDoc>> byService,
        RealAzureMigrationDoc migration,
        string siteRoot)
    {
        var all = byService.SelectMany(g => g).ToList();
        var sealed_ = all.Count(o => o.VerifiedRealAzure is not null);
        var unsealedImplemented = all
            .Where(o => o.Status.Equals("implemented", System.StringComparison.OrdinalIgnoreCase)
                        && o.VerifiedRealAzure is null)
            .OrderBy(o => o.Service + "/" + o.Operation, System.StringComparer.Ordinal)
            .ToList();
        var migrationByOperation = migration.Services
            .SelectMany(service => service.Operations.Select(operation => new
            {
                Key = service.Service + "/" + operation,
                service.TrackingIssue,
                service.ExpiresOn
            }))
            .ToDictionary(entry => entry.Key, System.StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine("# Real-Azure conformance & divergences");
        sb.AppendLine();
        sb.AppendLine("Emulators are a necessary, not sufficient, signal: nothing is trusted as");
        sb.AppendLine("`implemented` without ≥1 recorded real-Azure validation. This report aggregates");
        sb.AppendLine("the documented behaviour differences and the real-Azure seal state.");
        sb.AppendLine();
        sb.AppendLine($"- Operations: **{all.Count}** — real-Azure verified: **{sealed_}**, implemented-but-unsealed: **{unsealedImplemented.Count}**");
        sb.AppendLine();

        sb.AppendLine("## Implemented without a real-Azure seal");
        sb.AppendLine();
        if (unsealedImplemented.Count == 0)
        {
            sb.AppendLine("_None — every implemented operation carries a real-Azure seal._");
        }
        else
        {
            sb.AppendLine("| Service | Operation | Tracking issue | Expires |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var o in unsealedImplemented)
            {
                migrationByOperation.TryGetValue(o.Service + "/" + o.Operation, out var debt);
                var tracking = debt is null ? "—" : $"[issue]({debt.TrackingIssue})";
                var expires = debt?.ExpiresOn ?? "—";
                sb.AppendLine($"| {o.Service} | {o.Operation} | {tracking} | {expires} |");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Documented behaviour differences");
        sb.AppendLine();
        sb.AppendLine("| Service | Operation | Verified | Difference |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var o in all.OrderBy(o => o.Service + "/" + o.Operation, System.StringComparer.Ordinal))
        {
            foreach (var bd in o.BehaviorDifferences)
            {
                sb.AppendLine($"| {o.Service} | {o.Operation} | {Seal(o.VerifiedRealAzure)} | {Esc(bd)} |");
            }
        }
        File.WriteAllText(Path.Combine(siteRoot, "divergences.md"), sb.ToString());
    }

    private static string VerificationDetails(RealAzureVerification verification)
    {
        var details = new List<string>
        {
            Esc(verification.Date),
            $"[evidence]({verification.Evidence})"
        };
        if (!string.IsNullOrEmpty(verification.WorkflowRun))
        {
            details.Add($"[workflow run]({verification.WorkflowRun})");
        }
        return string.Join(" · ", details);
    }

    private static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("|", "\\|").Replace("\n", " ");
}
