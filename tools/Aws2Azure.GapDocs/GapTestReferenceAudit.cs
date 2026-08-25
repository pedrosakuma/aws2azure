using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Aws2Azure.GapDocs;

public sealed class GapTestReferenceFinding
{
    public string SourceFile { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string EntryPath { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

public static partial class GapTestReferenceAudit
{
    public static IReadOnlyList<GapTestReferenceFinding> FindMissingReferences(
        IReadOnlyList<OperationDoc> docs)
    {
        var findings = new List<GapTestReferenceFinding>();
        foreach (var doc in docs.OrderBy(value => value.SourceFile, StringComparer.Ordinal))
        {
            for (var index = 0; index < doc.BehaviorDifferences.Count; index++)
            {
                var difference = doc.BehaviorDifferences[index];
                if (HasDiscoverableTestReference(difference))
                {
                    continue;
                }

                findings.Add(new GapTestReferenceFinding
                {
                    SourceFile = doc.SourceFile,
                    Service = doc.Service,
                    Operation = doc.Operation,
                    EntryPath = $"behavior_differences[{index}]",
                    Summary = difference
                });
            }

            for (var index = 0; index < doc.SubFeatures.Count; index++)
            {
                var subFeature = doc.SubFeatures[index];
                if (subFeature.Status.Equals("implemented", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (HasDiscoverableTestReference(CombineSubFeatureText(subFeature)))
                {
                    continue;
                }

                findings.Add(new GapTestReferenceFinding
                {
                    SourceFile = doc.SourceFile,
                    Service = doc.Service,
                    Operation = doc.Operation,
                    EntryPath = $"sub_features[{index}] '{subFeature.Name}'",
                    Summary = $"{subFeature.Status}: {subFeature.Name}"
                });
            }
        }

        return findings;
    }

    public static bool HasDiscoverableTestReference(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return StructuredTestTagRegex().IsMatch(text)
               || QualifiedTestReferenceRegex().IsMatch(text);
    }

    public static string RenderText(
        IReadOnlyList<GapTestReferenceFinding> findings,
        string repoRoot)
    {
        var builder = new StringBuilder();
        builder.Append("[gap-docs] ");
        builder.Append(findings.Count);
        builder.Append(" documented divergence entr");
        builder.Append(findings.Count == 1 ? "y" : "ies");
        builder.AppendLine(" without discoverable test references.");

        foreach (var finding in findings)
        {
            var relativePath = Path.GetRelativePath(repoRoot, finding.SourceFile)
                .Replace('\\', '/');
            builder.Append("- ");
            builder.Append(relativePath);
            builder.Append(" :: ");
            builder.Append(finding.EntryPath);
            builder.Append(" :: ");
            builder.AppendLine(finding.Summary);
        }

        return builder.ToString();
    }

    private static string CombineSubFeatureText(SubFeature subFeature)
    {
        return string.Join(
            Environment.NewLine,
            new[]
            {
                subFeature.Name,
                subFeature.Notes,
                subFeature.Gap,
                subFeature.Workaround
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    [GeneratedRegex(@"\[(?:conformance|integration|unit|qualification|real[_-]azure):[^\]]+\]",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StructuredTestTagRegex();

    [GeneratedRegex(@"\b(?:[A-Za-z_][A-Za-z0-9_]*\.)+[A-Za-z_][A-Za-z0-9_]*Tests?\.[A-Za-z_][A-Za-z0-9_]*\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedTestReferenceRegex();
}
