using System.Text;
using Aws2Azure.Conformance.Canonicalization;

namespace Aws2Azure.Conformance.Goldens;

/// <summary>
/// Provenance stamped on every golden so a reader can tell <em>where</em> the
/// reference response came from and how much to trust it. Goldens captured from
/// LocalStack are flagged emulator-derived (necessary, not sufficient — see the
/// emulator caveat in the repo conventions); goldens captured from real AWS are
/// authoritative.
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
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "Aws2Azure.Conformance.csproj")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not locate Aws2Azure.Conformance.csproj to resolve the fixtures directory.");
        }
        return new GoldenStore(Path.Combine(dir.FullName, "fixtures", service));
    }

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

    public void Save(string caseName, CanonicalResponse response, GoldenProvenance provenance)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(PathFor(caseName, provenance.Source), Serialize(response, provenance));
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
                _ => 0,
            };

    internal static string Serialize(CanonicalResponse response, GoldenProvenance provenance)
    {
        var sb = new StringBuilder();
        sb.Append("# aws2azure conformance golden\n");
        sb.Append("# source: ").Append(provenance.Source).Append('\n');
        sb.Append("# operation: ").Append(provenance.Operation).Append('\n');
        sb.Append("# captured: ").Append(provenance.CapturedAtUtc.ToString("O")).Append('\n');
        if (!string.IsNullOrEmpty(provenance.Note))
        {
            sb.Append("# note: ").Append(provenance.Note).Append('\n');
        }
        sb.Append("# ---\n");
        sb.Append(response.Render());
        return sb.ToString();
    }

    internal static GoldenFile Parse(string text)
    {
        string source = "unknown";
        string operation = "unknown";
        DateTimeOffset captured = default;
        string? note = null;
        var body = new StringBuilder();

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("# ---", StringComparison.Ordinal))
            {
                continue;
            }
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var kv = line[2..];
                var idx = kv.IndexOf(':');
                if (idx > 0)
                {
                    var key = kv[..idx].Trim();
                    var value = kv[(idx + 1)..].Trim();
                    switch (key)
                    {
                        case "source": source = value; break;
                        case "operation": operation = value; break;
                        case "captured":
                            _ = DateTimeOffset.TryParse(value, out captured);
                            break;
                        case "note": note = value; break;
                    }
                }
                continue;
            }
            body.Append(line).Append('\n');
        }

        return new GoldenFile(
            new GoldenProvenance(source, operation, captured, note),
            body.ToString());
    }
}
