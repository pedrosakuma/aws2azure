using System.Text;
using Aws2Azure.Conformance.Canonicalization;

namespace Aws2Azure.Conformance.Goldens;

/// <summary>
/// Provenance stamped on every canonical conformance capture so a reader can
/// tell <em>where</em> the response came from and how much to trust it. Replay
/// goldens captured from LocalStack are flagged emulator-derived (necessary, not
/// sufficient — see the emulator caveat in the repo conventions); captures from
/// real AWS are authoritative. The same metadata record is also reused by the
/// Tier-3 real-Azure evidence files.
/// </summary>
public sealed record GoldenProvenance(
    string Source,
    string Operation,
    DateTimeOffset CapturedAtUtc,
    string? Note = null)
{
    public const string SourceLocalStack = "localstack";
    public const string SourceRealAws = "aws";
    public const string SourceProxySelf = "proxy-self";
    public const string SourceProxyRealAzure = "proxy-real-azure";

    /// <summary>
    /// True when the golden comes from real AWS and can therefore serve as the
    /// highest-trust replay reference.
    /// </summary>
    public bool IsAuthoritative => Source == SourceRealAws;
}

/// <summary>A parsed golden: provenance metadata + the canonical comparison text.</summary>
public sealed record GoldenFile(GoldenProvenance Provenance, string CanonicalText);

/// <summary>
/// On-disk persistence for canonical goldens. Format is a small comment header
/// (<c># key: value</c>) followed by the verbatim <see cref="CanonicalResponse.Render"/>
/// text. Plain text on purpose: goldens are reviewed in PRs and a clean diff is
/// the whole point.
///
/// Record mode (<c>AWS2AZURE_CONFORMANCE_RECORD=1</c>) flips the replay tests
/// from verify to capture so the Tier-2 LocalStack job can (re)generate the
/// committed goldens.
/// </summary>
public sealed class GoldenStore
{
    private readonly string _root;
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public GoldenStore(string root) => _root = root;

    public static bool RecordMode =>
        Environment.GetEnvironmentVariable("AWS2AZURE_CONFORMANCE_RECORD") is "1" or "true";

    /// <summary>Resolves <c>fixtures/&lt;service&gt;</c> in the source tree (not bin/).</summary>
    public static GoldenStore ForService(string service)
        => new(Path.Combine(ConformanceProjectPaths.ProjectRoot(), "fixtures", service));

    /// <summary>
    /// Legacy LocalStack path kept for backward compatibility with existing
    /// committed <c>&lt;case&gt;.golden</c> files.
    /// </summary>
    public string PathFor(string caseName) => Path.Combine(_root, caseName + ".golden");

    /// <summary>
    /// Provenance-specific path. Real-AWS and proxy-self captures get a source
    /// suffix so they can coexist with the legacy LocalStack golden for the same
    /// case without clobbering it.
    /// </summary>
    public string PathFor(string caseName, string source) =>
        source == GoldenProvenance.SourceLocalStack
            ? PathFor(caseName)
            : Path.Combine(_root, caseName + "." + source + ".golden");

    /// <summary>
    /// Step-scoped golden path for multi-request cases. Happy-path captures keep
    /// one canonical response per step under a case directory so the offline
    /// Tier-3 diff can compare full CRUD/pagination/batch plans, not only
    /// single-response error cases.
    /// </summary>
    public string PathForStep(string caseName, string stepName, string source) =>
        Path.Combine(_root, caseName, stepName + "." + source + ".golden");

    public bool Exists(string caseName) => EnumerateCandidatePaths(caseName).Any(File.Exists);

    public bool TryLoad(string caseName, out GoldenFile golden)
    {
        var best = default(GoldenFile);
        var bestPriority = int.MinValue;

        foreach (var path in EnumerateCandidatePaths(caseName))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var candidate = Parse(File.ReadAllText(path));
            var priority = ReplayPriority(candidate.Provenance);
            if (priority > bestPriority)
            {
                best = candidate;
                bestPriority = priority;
            }
        }

        if (best is null)
        {
            golden = null!;
            return false;
        }

        golden = best;
        return true;
    }

    /// <summary>
    /// Loads a golden only when an exact provenance-specific file exists. Tier-3
    /// credential-free replay uses this to require the authoritative real-AWS
    /// capture instead of silently falling back to LocalStack.
    /// </summary>
    public bool TryLoad(string caseName, string source, out GoldenFile golden)
    {
        var path = PathFor(caseName, source);
        if (!File.Exists(path))
        {
            golden = null!;
            return false;
        }

        golden = Parse(File.ReadAllText(path));
        return true;
    }

    public bool TryLoadStep(string caseName, string stepName, string source, out GoldenFile golden)
    {
        var path = PathForStep(caseName, stepName, source);
        if (!File.Exists(path))
        {
            golden = null!;
            return false;
        }

        golden = Parse(File.ReadAllText(path));
        return true;
    }

    public void Save(string caseName, CanonicalResponse response, GoldenProvenance provenance)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(PathFor(caseName, provenance.Source), Serialize(response, provenance));
    }

    public void SaveStep(
        string caseName,
        string stepName,
        CanonicalResponse response,
        GoldenProvenance provenance)
    {
        Directory.CreateDirectory(Path.Combine(_root, caseName));
        File.WriteAllText(
            PathForStep(caseName, stepName, provenance.Source),
            Serialize(response, provenance));
    }

    private IEnumerable<string> EnumerateCandidatePaths(string caseName)
    {
        yield return PathFor(caseName);

        if (!Directory.Exists(_root))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(_root, caseName + ".*.golden"))
        {
            if (!PathComparer.Equals(path, PathFor(caseName)))
            {
                yield return path;
            }
        }
    }

    private static int ReplayPriority(GoldenProvenance provenance) =>
        provenance.IsAuthoritative
            ? 300
            : provenance.Source switch
            {
                GoldenProvenance.SourceLocalStack => 200,
                GoldenProvenance.SourceProxySelf => 100,
                GoldenProvenance.SourceProxyRealAzure => 50,
                _ => 0,
            };

    internal static string Serialize(CanonicalResponse response, GoldenProvenance provenance)
        => CanonicalCaptureFileFormat.Serialize("golden", response, provenance);

    internal static GoldenFile Parse(string text)
        => CanonicalCaptureFileFormat.Parse(text);
}
