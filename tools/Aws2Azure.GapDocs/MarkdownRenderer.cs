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
        File.WriteAllText(Path.Combine(siteRoot, "index.md"), sb.ToString());
    }

    private static void WriteCoverage(IList<IGrouping<string, OperationDoc>> byService, string siteRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Coverage matrix");
        sb.AppendLine();
        sb.AppendLine("| Service | Operation | Status | Azure equivalent |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var group in byService)
        {
            foreach (var op in group.OrderBy(o => o.Operation, System.StringComparer.Ordinal))
            {
                sb.AppendLine($"| {op.Service} | [{op.Operation}]({group.Key}.md#{op.Operation.ToLowerInvariant()}) | {StatusBadge(op.Status)} | `{op.AzureEquivalent}` |");
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
            sb.AppendLine();

            if (op.SubFeatures.Count > 0)
            {
                sb.AppendLine("### Sub-features");
                sb.AppendLine();
                sb.AppendLine("| Name | Status | Notes | Gap | Workaround |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var sf in op.SubFeatures)
                {
                    sb.AppendLine($"| {sf.Name} | {StatusBadge(sf.Status)} | {Esc(sf.Notes)} | {Esc(sf.Gap)} | {Esc(sf.Workaround)} |");
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

    private static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("|", "\\|").Replace("\n", " ");
}
