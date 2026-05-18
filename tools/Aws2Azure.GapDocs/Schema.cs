using System.Collections.Generic;

namespace Aws2Azure.GapDocs;

public sealed class OperationDoc
{
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string AzureEquivalent { get; set; } = string.Empty;
    public string Status { get; set; } = "stub";
    public List<SubFeature> SubFeatures { get; set; } = new();
    public List<string> BehaviorDifferences { get; set; } = new();
    public List<string> References { get; set; } = new();

    // Provenance — set by loader.
    public string SourceFile { get; set; } = string.Empty;
}

public sealed class SubFeature
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "unsupported";
    public string Notes { get; set; } = string.Empty;
    public string Gap { get; set; } = string.Empty;
    public string Workaround { get; set; } = string.Empty;
}

public static class StatusValues
{
    public static readonly HashSet<string> Operation = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "implemented", "partial", "stub", "unsupported"
    };
    public static readonly HashSet<string> SubFeature = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "implemented", "partial", "unsupported"
    };
}
