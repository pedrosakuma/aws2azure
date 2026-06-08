using System.Text.RegularExpressions;
using Aws2Azure.Conformance.Canonicalization;
using Aws2Azure.GapDocs;

namespace Aws2Azure.Conformance.AllowList;

/// <summary>
/// The set of divergence tags the gap docs declare as <em>accepted</em>. A
/// behavior is the single source of truth: to accept a faithful-divergence
/// (e.g. the proxy omits the server-side <c>x-amz-id-2</c> header) you document
/// it in <c>docs/gaps/&lt;service&gt;/&lt;Operation&gt;.yaml</c> under
/// <c>behavior_differences</c> with a machine-readable tag
/// <c>[conformance:&lt;tag&gt;]</c>. The harness then treats a <see cref="Divergence"/>
/// whose <see cref="Divergence.Tag"/> matches as expected rather than a failure.
/// </summary>
public sealed class ConformanceAllowList
{
    private static readonly Regex TagPattern =
        new(@"\[conformance:(?<tag>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HashSet<string> _tags;

    public ConformanceAllowList(IEnumerable<string> tags) =>
        _tags = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Tags => _tags;

    public bool Accepts(Divergence divergence) => _tags.Contains(divergence.Tag);

    /// <summary>
    /// Partitions divergences into the ones the gap docs accept and the ones that
    /// are unexpected (and therefore must fail the conformance run).
    /// </summary>
    public (IReadOnlyList<Divergence> Accepted, IReadOnlyList<Divergence> Unexpected) Partition(
        IEnumerable<Divergence> divergences)
    {
        var accepted = new List<Divergence>();
        var unexpected = new List<Divergence>();
        foreach (var d in divergences)
        {
            (Accepts(d) ? accepted : unexpected).Add(d);
        }
        return (accepted, unexpected);
    }

    /// <summary>Loads accepted tags from every gap doc under a service directory.</summary>
    public static ConformanceAllowList FromGapDocs(string service)
    {
        var gapsRoot = Path.Combine(RepoRoot(), "docs", "gaps", service);
        if (!Directory.Exists(gapsRoot))
        {
            return new ConformanceAllowList(Array.Empty<string>());
        }
        var docs = Loader.LoadAll(gapsRoot);
        return new ConformanceAllowList(ExtractTags(docs.SelectMany(d => d.BehaviorDifferences)));
    }

    internal static IEnumerable<string> ExtractTags(IEnumerable<string> behaviorDifferences)
    {
        foreach (var line in behaviorDifferences)
        {
            foreach (Match m in TagPattern.Matches(line))
            {
                yield return m.Groups["tag"].Value.Trim();
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "aws2azure.slnx")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repository root (aws2azure.slnx).");
        }
        return dir.FullName;
    }
}
