using Aws2Azure.Conformance.Canonicalization;

namespace Aws2Azure.Conformance.Goldens;

/// <summary>
/// On-disk persistence for the proxy-over-real-Azure side of the Tier-3,
/// credential-free diff. The file format intentionally mirrors
/// <see cref="GoldenStore"/> so the offline comparer can parse both sides with
/// the same logic; only the file extension and the human-readable header label
/// differ.
///
/// Single-request cases use one flat <c>&lt;case&gt;.evidence</c> file. Multi-step
/// happy-path cases store one file per step under
/// <c>&lt;case&gt;/&lt;step&gt;.evidence</c>, matching the step-aware golden layout.
/// </summary>
public sealed class EvidenceStore
{
    public const string RootEnvironmentVariableName =
        "AWS2AZURE_CONFORMANCE_TIER3_EVIDENCE_ROOT";

    private readonly string _root;

    public EvidenceStore(string root) => _root = root;

    /// <summary>
    /// Resolves the service-specific evidence directory. Future workflows can
    /// point this at a downloaded artifact by setting
    /// <see cref="RootEnvironmentVariableName"/>; otherwise the default is the
    /// inert source-tree <c>evidence/&lt;service&gt;</c> path.
    /// </summary>
    public static EvidenceStore ForService(string service)
    {
        var baseRoot = Environment.GetEnvironmentVariable(RootEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(baseRoot))
        {
            baseRoot = Path.Combine(ConformanceProjectPaths.ProjectRoot(), "evidence");
        }

        return new EvidenceStore(Path.Combine(baseRoot, service));
    }

    public string PathFor(string caseName) => Path.Combine(_root, caseName + ".evidence");

    public string DirectoryForCase(string caseName) => Path.Combine(_root, caseName);

    public string PathForStep(string caseName, string stepName) =>
        Path.Combine(DirectoryForCase(caseName), stepName + ".evidence");

    public bool TryLoad(string caseName, out GoldenFile evidence) =>
        TryLoadPath(PathFor(caseName), out evidence);

    public bool TryLoadStep(string caseName, string stepName, out GoldenFile evidence) =>
        TryLoadPath(PathForStep(caseName, stepName), out evidence);

    public void Save(string caseName, CanonicalResponse response, GoldenProvenance provenance)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(PathFor(caseName), Serialize(response, provenance));
    }

    public void SaveStep(
        string caseName,
        string stepName,
        CanonicalResponse response,
        GoldenProvenance provenance)
    {
        Directory.CreateDirectory(DirectoryForCase(caseName));
        File.WriteAllText(PathForStep(caseName, stepName), Serialize(response, provenance));
    }

    internal static string Serialize(CanonicalResponse response, GoldenProvenance provenance) =>
        CanonicalCaptureFileFormat.Serialize("evidence", response, provenance);

    private static bool TryLoadPath(string path, out GoldenFile evidence)
    {
        if (!File.Exists(path))
        {
            evidence = null!;
            return false;
        }

        evidence = CanonicalCaptureFileFormat.Parse(File.ReadAllText(path));
        return true;
    }
}
