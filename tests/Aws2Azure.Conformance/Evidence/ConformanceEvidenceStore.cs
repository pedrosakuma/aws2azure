using System.Text;
using Aws2Azure.Conformance.Canonicalization;

namespace Aws2Azure.Conformance.Evidence;

/// <summary>
/// Provenance stamped on each canonical evidence artifact captured from the
/// real-Azure proxy path. The file format intentionally mirrors
/// <see cref="Goldens.GoldenStore"/> so a human or future diff tool can read a
/// golden and a live evidence file with the same <c># key: value</c> header +
/// canonical-body shape.
/// </summary>
public sealed record ConformanceEvidenceMetadata(
    string Source,
    string Service,
    string CaseName,
    string Operation,
    string Step,
    DateTimeOffset CapturedAtUtc,
    string? Note = null,
    string? SkippedReason = null)
{
    public const string SourceRealAzureProxy = "real-azure-proxy";
}

/// <summary>A parsed real-Azure evidence file.</summary>
public sealed record ConformanceEvidenceFile(
    ConformanceEvidenceMetadata Metadata,
    string CanonicalText);

/// <summary>
/// On-disk persistence for canonical real-Azure evidence captured from the
/// shared conformance happy-path matrix. Each file stores service/case/step
/// metadata and the verbatim <see cref="CanonicalResponse.Render"/> output.
///
/// <para>
/// The default root is <c>TestResults/real-azure-conformance/canonical-cases</c>
/// under the repository root. Set
/// <c>AWS2AZURE_CONFORMANCE_EVIDENCE_DIR</c> to override it.
/// </para>
/// </summary>
public sealed class ConformanceEvidenceStore
{
    private const string EvidenceFileSuffix = ".evidence";
    public const string SkippedStepName = "skipped";
    private readonly string _root;

    public ConformanceEvidenceStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
    }

    public static string DefaultRelativeRoot =>
        Path.Combine("TestResults", "real-azure-conformance", "canonical-cases");

    /// <summary>
    /// Resolves the configured evidence root. When no override is supplied, the
    /// repository root is discovered by walking up from <see cref="AppContext.BaseDirectory"/>
    /// until <c>aws2azure.slnx</c> is found.
    /// </summary>
    public static string ResolveRoot(string? configuredRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "aws2azure.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                "Could not locate aws2azure.slnx to resolve the real-Azure conformance evidence directory.");
        }

        return Path.Combine(directory.FullName, DefaultRelativeRoot);
    }

    public string PathFor(string service, string caseName, string step)
        => Path.Combine(_root, service, caseName, step + EvidenceFileSuffix);

    public void Save(
        CanonicalResponse response,
        ConformanceEvidenceMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(metadata);

        var path = PathFor(metadata.Service, metadata.CaseName, metadata.Step);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serialize(response, metadata));
    }

    public void SaveSkipped(ConformanceEvidenceMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (string.IsNullOrWhiteSpace(metadata.SkippedReason))
        {
            throw new ArgumentException(
                "Skipped evidence metadata must include a skipped reason.",
                nameof(metadata));
        }

        var path = PathFor(metadata.Service, metadata.CaseName, metadata.Step);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, SerializeSkipped(metadata));
    }

    internal static string Serialize(
        CanonicalResponse response,
        ConformanceEvidenceMetadata metadata)
    {
        var builder = new StringBuilder();
        builder.Append("# aws2azure conformance evidence\n");
        builder.Append("# source: ").Append(metadata.Source).Append('\n');
        builder.Append("# service: ").Append(metadata.Service).Append('\n');
        builder.Append("# case: ").Append(metadata.CaseName).Append('\n');
        builder.Append("# operation: ").Append(metadata.Operation).Append('\n');
        builder.Append("# step: ").Append(metadata.Step).Append('\n');
        builder.Append("# captured: ").Append(metadata.CapturedAtUtc.ToString("O")).Append('\n');
        if (!string.IsNullOrWhiteSpace(metadata.Note))
        {
            builder.Append("# note: ").Append(metadata.Note).Append('\n');
        }

        builder.Append("# ---\n");
        builder.Append(response.Render());
        return builder.ToString();
    }

    internal static string SerializeSkipped(ConformanceEvidenceMetadata metadata)
    {
        var builder = new StringBuilder();
        builder.Append("# aws2azure conformance evidence\n");
        builder.Append("# source: ").Append(metadata.Source).Append('\n');
        builder.Append("# service: ").Append(metadata.Service).Append('\n');
        builder.Append("# case: ").Append(metadata.CaseName).Append('\n');
        builder.Append("# operation: ").Append(metadata.Operation).Append('\n');
        builder.Append("# step: ").Append(metadata.Step).Append('\n');
        builder.Append("# captured: ").Append(metadata.CapturedAtUtc.ToString("O")).Append('\n');
        if (!string.IsNullOrWhiteSpace(metadata.Note))
        {
            builder.Append("# note: ").Append(metadata.Note).Append('\n');
        }

        builder.Append("# skipped: ").Append(metadata.SkippedReason).Append('\n');
        builder.Append("# ---\n");
        return builder.ToString();
    }

    internal static ConformanceEvidenceFile Parse(string text)
    {
        string source = "unknown";
        string service = "unknown";
        string caseName = "unknown";
        string operation = "unknown";
        string step = "unknown";
        DateTimeOffset captured = default;
        string? note = null;
        string? skippedReason = null;
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
                var keyValue = line[2..];
                var separator = keyValue.IndexOf(':');
                if (separator > 0)
                {
                    var key = keyValue[..separator].Trim();
                    var value = keyValue[(separator + 1)..].Trim();
                    switch (key)
                    {
                        case "source":
                            source = value;
                            break;
                        case "service":
                            service = value;
                            break;
                        case "case":
                            caseName = value;
                            break;
                        case "operation":
                            operation = value;
                            break;
                        case "step":
                            step = value;
                            break;
                        case "captured":
                            _ = DateTimeOffset.TryParse(value, out captured);
                            break;
                        case "note":
                            note = value;
                            break;
                        case "skipped":
                            skippedReason = value;
                            break;
                    }
                }

                continue;
            }

            body.Append(line).Append('\n');
        }

        return new ConformanceEvidenceFile(
            new ConformanceEvidenceMetadata(
                source,
                service,
                caseName,
                operation,
                step,
                captured,
                note,
                skippedReason),
            body.ToString());
    }
}
