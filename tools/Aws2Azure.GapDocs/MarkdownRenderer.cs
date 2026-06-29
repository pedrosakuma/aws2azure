using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Aws2Azure.GapDocs;

public static class MarkdownRenderer
{
    public static void Render(IReadOnlyList<OperationDoc> docs, string siteRoot)
    {
        Directory.CreateDirectory(siteRoot);

        var byService = docs
            .GroupBy(d => d.Service.ToLowerInvariant())
            .OrderBy(g => g.Key, System.StringComparer.Ordinal)
            .ToList();

        WriteIndex(byService, siteRoot);
        WriteCoverage(byService, siteRoot);
        WriteDivergences(byService, siteRoot);
        foreach (var group in byService)
        {
            WriteServicePage(group.Key, group.OrderBy(o => o.Operation, System.StringComparer.Ordinal).ToList(), siteRoot);
        }
    }

    private static void WriteIndex(IList<IGrouping<string, OperationDoc>> byService, string siteRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# aws2azure — gap documentation");
        sb.AppendLine();
        sb.AppendLine("Authoritative inventory of which AWS operations the proxy translates, with the Azure mapping and the known behavioural gaps.");
        sb.AppendLine();
        sb.AppendLine("## Services");
        sb.AppendLine();
        foreach (var group in byService)
        {
            sb.AppendLine($"- [{group.Key}]({group.Key}.md) — {group.Count()} operation(s)");
        }
        sb.AppendLine();
        sb.AppendLine("See [coverage matrix](coverage.md) for a one-screen overview.");
        sb.AppendLine();
        sb.AppendLine("See [real-Azure conformance & divergences](divergences.md) for verification state.");
        File.WriteAllText(Path.Combine(siteRoot, "index.md"), sb.ToString());
    }

    private static void WriteCoverage(IList<IGrouping<string, OperationDoc>> byService, string siteRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Coverage matrix");
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
            if (!string.IsNullOrEmpty(op.VerifiedRealAzure))
            {
                sb.AppendLine($"- **Real-Azure verified:** ✅ {Esc(op.VerifiedRealAzure)}");
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

    private static string StatusBadge(string status) => status.ToLowerInvariant() switch
    {
        "implemented" => "✅ implemented",
        "partial" => "🟡 partial",
        "stub" => "⚪ stub",
        "unsupported" => "⛔ unsupported",
        _ => status
    };

    private static string Seal(string verified) => string.IsNullOrEmpty(verified) ? "—" : "✅";

    // Theme C divergence report: a one-screen dossier of every documented
    // behaviour difference plus the real-Azure verification state. The
    // emulator caveat says nothing is trustworthy as "implemented" without a
    // real-Azure seal, so implemented-but-unsealed ops are surfaced as the
    // backlog to close. Generated alongside the site so the conformance
    // workflow can upload it as the run's divergence artifact.
    private static void WriteDivergences(IList<IGrouping<string, OperationDoc>> byService, string siteRoot)
    {
        var all = byService.SelectMany(g => g).ToList();
        var sealed_ = all.Count(o => !string.IsNullOrEmpty(o.VerifiedRealAzure));
        var unsealedImplemented = all
            .Where(o => o.Status.Equals("implemented", System.StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrEmpty(o.VerifiedRealAzure))
            .OrderBy(o => o.Service + "/" + o.Operation, System.StringComparer.Ordinal)
            .ToList();

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
            sb.AppendLine("| Service | Operation |");
            sb.AppendLine("|---|---|");
            foreach (var o in unsealedImplemented) sb.AppendLine($"| {o.Service} | {o.Operation} |");
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

    private static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("|", "\\|").Replace("\n", " ");
}
