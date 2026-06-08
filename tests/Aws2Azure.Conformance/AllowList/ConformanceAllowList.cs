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

    /// <summary>Separator marking a case-scoped tag: <c>&lt;caseName&gt;::&lt;tag&gt;</c>.</summary>
    public const string ScopeSeparator = "::";

    private readonly HashSet<string> _serviceWide;
    private readonly Dictionary<string, HashSet<string>> _caseScoped;

    public ConformanceAllowList(IEnumerable<string> declaredTags)
    {
        _serviceWide = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _caseScoped = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in declaredTags)
        {
            var declared = raw.Trim();
            var sep = declared.IndexOf(ScopeSeparator, StringComparison.Ordinal);
            if (sep > 0)
            {
                var caseName = declared[..sep].Trim();
                var tag = declared[(sep + ScopeSeparator.Length)..].Trim();
                if (!_caseScoped.TryGetValue(caseName, out var set))
                {
                    _caseScoped[caseName] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                set.Add(tag);
            }
            else
            {
                _serviceWide.Add(declared);
            }
        }
    }

    /// <summary>Every declared tag (service-wide and case-scoped, re-qualified) — for diagnostics.</summary>
    public IReadOnlyCollection<string> Tags
    {
        get
        {
            var all = new List<string>(_serviceWide);
            foreach (var (caseName, set) in _caseScoped)
            {
                foreach (var tag in set)
                {
                    all.Add(caseName + ScopeSeparator + tag);
                }
            }
            return all;
        }
    }

    /// <summary>
    /// A divergence is accepted when it is documented either service-wide
    /// (<c>[conformance:&lt;tag&gt;]</c>) or scoped to <paramref name="caseName"/>
    /// (<c>[conformance:&lt;caseName&gt;::&lt;tag&gt;]</c>). Case scoping prevents a
    /// per-case waiver (e.g. <c>field-value:Code</c>) from silently suppressing the
    /// same divergence in every other case.
    /// </summary>
    public bool Accepts(Divergence divergence, string? caseName = null)
    {
        if (_serviceWide.Contains(divergence.Tag))
        {
            return true;
        }
        return caseName is not null
            && _caseScoped.TryGetValue(caseName, out var set)
            && set.Contains(divergence.Tag);
    }

    /// <summary>
    /// Partitions divergences into the ones the gap docs accept and the ones that
    /// are unexpected (and therefore must fail the conformance run).
    /// </summary>
    public (IReadOnlyList<Divergence> Accepted, IReadOnlyList<Divergence> Unexpected) Partition(
        IEnumerable<Divergence> divergences, string? caseName = null)
    {
        var accepted = new List<Divergence>();
        var unexpected = new List<Divergence>();
        foreach (var d in divergences)
        {
            (Accepts(d, caseName) ? accepted : unexpected).Add(d);
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
