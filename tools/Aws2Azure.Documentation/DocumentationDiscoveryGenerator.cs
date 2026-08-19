using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aws2Azure.GapDocs;
using Microsoft.Win32.SafeHandles;

namespace Aws2Azure.Documentation;

public sealed class DocumentationManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Repository { get; set; } = string.Empty;
    public string SourceBaseUrl { get; set; } = string.Empty;
    public string PublicationBaseUrl { get; set; } = string.Empty;
    public string ManifestRevision { get; set; } = string.Empty;
    public DocumentationRevisionSemantics RevisionSemantics { get; set; } = new();
    public DocumentationWorkloadAuthority WorkloadAuthority { get; set; } = new();
    public List<DocumentationAuthorityPrecedence> AuthorityPrecedence { get; set; } = new();
    public List<DocumentationEntry> Documents { get; set; } = new();
}

public sealed class DocumentationRevisionSemantics
{
    public string DocumentRevisionType { get; set; } = "normalized_text_sha256";
    public string DocumentRevisionNormalization { get; set; } =
        "UTF-8 text, optional BOM removed, CRLF and CR normalized to LF";
    public string ManifestRevisionType { get; set; } = "ordered_document_index_sha256";
}

public sealed class DocumentationWorkloadAuthority
{
    public string CurrentVerdictPath { get; set; } = string.Empty;
    public string ContractPath { get; set; } = string.Empty;
    public string EvaluatedAsOfUtc { get; set; } = string.Empty;
    public string CanonicalInputsRevisionType { get; set; } = string.Empty;
    public string CanonicalInputsRevision { get; set; } = string.Empty;
    public int EvaluatorSchemaVersion { get; set; }
    public string EvaluatorImplementationRevisionType { get; set; } = string.Empty;
    public string EvaluatorImplementationRevision { get; set; } = string.Empty;
}

public sealed class DocumentationAuthorityPrecedence
{
    public int Rank { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public sealed class DocumentationEntry
{
    public string Id { get; set; } = string.Empty;
    public string? CanonicalId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string Authority { get; set; } = string.Empty;
    public string Provenance { get; set; } = string.Empty;
    public string? Service { get; set; }
    public string? Operation { get; set; }
    public string? Profile { get; set; }
    public string Revision { get; set; } = string.Empty;
    public DocumentationFreshness Freshness { get; set; } = new();
}

public sealed class DocumentationFreshness
{
    public string Mode { get; set; } = string.Empty;
    public string? EvaluatedAsOfUtc { get; set; }
    public string? Version { get; set; }
    public string? VerifiedRealAzureDate { get; set; }
}

public static class DocumentationDiscoveryGenerator
{
    public const string ManifestRelativePath = "documentation-manifest.json";
    public const string LlmsRelativePath = "llms.txt";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static int Run(string[] args)
    {
        try
        {
            var check = args.Length == 1 && args[0].Equals("--check", StringComparison.Ordinal);
            if (args.Length > 0 && !check)
            {
                throw new ArgumentException("Usage: Aws2Azure.Documentation [--check]");
            }

            var repoRoot = FindRepoRoot();
            var manifest = Build(repoRoot);
            var outputs = new[]
            {
                (
                    Path: ResolveRepositoryPath(
                        repoRoot,
                        ManifestRelativePath,
                        allowMissingLeaf: true),
                    Content: RenderManifest(manifest)),
                (
                    Path: ResolveRepositoryPath(
                        repoRoot,
                        LlmsRelativePath,
                        allowMissingLeaf: true),
                    Content: RenderLlms(manifest)),
            };

            if (check)
            {
                var stale = outputs
                    .Where(output => !RepositoryFileMatches(
                        repoRoot,
                        Path.GetRelativePath(repoRoot, output.Path).Replace('\\', '/'),
                        Encoding.UTF8.GetBytes(output.Content)))
                    .Select(output => Path.GetRelativePath(repoRoot, output.Path).Replace('\\', '/'))
                    .ToList();
                if (stale.Count > 0)
                {
                    Console.Error.WriteLine(
                        "[documentation] generated discovery artifacts are out of date: "
                        + string.Join(", ", stale));
                    Console.Error.WriteLine(
                        "[documentation] run 'dotnet run --project tools/Aws2Azure.Documentation'");
                    return 1;
                }

                Console.WriteLine("[documentation] discovery artifacts are current");
                return 0;
            }

            foreach (var output in outputs)
            {
                WriteRepositoryFile(
                    repoRoot,
                    Path.GetRelativePath(repoRoot, output.Path).Replace('\\', '/'),
                    Encoding.UTF8.GetBytes(output.Content),
                    hooks: null);
                Console.WriteLine(
                    $"[documentation] wrote {Path.GetRelativePath(repoRoot, output.Path).Replace('\\', '/')}");
            }

            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or DirectoryNotFoundException
                                          or FileNotFoundException
                                          or InvalidDataException
                                          or JsonException
                                          or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("[documentation] " + exception.Message);
            return 2;
        }
    }

    public static DocumentationManifest Build(string repoRoot) =>
        Build(repoRoot, hooks: null);

    internal static DocumentationManifest Build(
        string repoRoot,
        DocumentationIoHooks? hooks)
    {
        using var snapshot = DocumentationRepositorySnapshot.Capture(repoRoot, hooks);
        return BuildFromSnapshot(snapshot.Root);
    }

    private static DocumentationManifest BuildFromSnapshot(string repoRoot)
    {
        var fullRepoRoot = Path.GetFullPath(repoRoot);
        if (!File.Exists(Path.Combine(fullRepoRoot, "aws2azure.slnx")))
        {
            throw new DirectoryNotFoundException(
                $"Repository root does not contain aws2azure.slnx: {fullRepoRoot}");
        }

        var gapsRoot = Path.Combine(fullRepoRoot, "docs", "gaps");
        var workloadsRoot = Path.Combine(fullRepoRoot, "docs", "workloads");
        _ = EnumerateRelativeFiles(fullRepoRoot, "docs/gaps", "*", recursive: true);
        _ = EnumerateRelativeFiles(fullRepoRoot, "docs/workloads", "*", recursive: true);
        var operationDocs = Loader.LoadAll(gapsRoot);
        var designDocs = Loader.LoadDesignDocs(gapsRoot);
        var contract = WorkloadGaEvaluationContractLoader.Load(
            ResolveRepositoryPath(
                fullRepoRoot,
                WorkloadGaEvaluationMetadataBuilder.ContractPath));
        var authority = ReadCurrentWorkloadAuthority(fullRepoRoot, contract);
        var documents = new List<DocumentationEntry>();

        void Add(
            string id,
            string path,
            string type,
            string scope,
            string entryAuthority,
            string provenance,
            DocumentationFreshness freshness,
            string? canonicalId = null,
            string? service = null,
            string? operation = null,
            string? profile = null)
        {
            var normalizedPath = path.Replace('\\', '/');
            documents.Add(new DocumentationEntry
            {
                Id = id,
                CanonicalId = canonicalId,
                Path = normalizedPath,
                Type = type,
                Scope = scope,
                Authority = entryAuthority,
                Provenance = provenance,
                Service = service,
                Operation = operation,
                Profile = profile,
                Revision = ComputeFileRevision(fullRepoRoot, normalizedPath),
                Freshness = freshness,
            });
        }

        Add(
            "workload-certification:current:machine",
            "docs/site/workload-ga.json",
            "workload-certification",
            "current-workload-adoption",
            "current",
            "generated",
            PointInTime(authority.EvaluatedAsOfUtc),
            canonicalId: "workload-certification:current");
        Add(
            "workload-certification:current:human",
            "docs/site/workload-ga.md",
            "workload-certification-guide",
            "current-workload-adoption",
            "current",
            "generated",
            PointInTime(authority.EvaluatedAsOfUtc),
            canonicalId: "workload-certification:current");
        Add(
            "workload-certification:authority-contract",
            WorkloadGaEvaluationMetadataBuilder.ContractPath,
            "workload-authority-contract",
            "current-workload-adoption",
            "canonical",
            "source",
            PointInTime(authority.EvaluatedAsOfUtc));

        Add(
            "configuration:schema",
            "config.schema.json",
            "configuration-schema",
            "operator-configuration",
            "canonical",
            "generated",
            Current());
        foreach (var path in new[]
                 {
                     "docs/configuration-schema.md",
                     "docs/configuration-reference.md",
                     "docs/configuration-environment.md",
                     "docs/configuration-examples.md",
                 })
        {
            var name = Path.GetFileNameWithoutExtension(path);
            Add(
                $"configuration:guide:{DocumentationLinks.Anchor(name)}",
                path,
                "configuration-guide",
                "operator-configuration",
                "explanatory",
                "source",
                Current());
        }
        foreach (var path in EnumerateRelativeFiles(
                     fullRepoRoot,
                     "docs/configuration/examples",
                     "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            Add(
                $"configuration:example:{DocumentationLinks.Anchor(name)}",
                path,
                "configuration-example",
                "operator-configuration",
                "explanatory",
                "source",
                Current());
        }

        foreach (var path in EnumerateRelativeFiles(fullRepoRoot, "docs/workloads", "*.yaml", true))
        {
            if (path.Equals(
                    WorkloadGaEvaluationMetadataBuilder.ContractPath,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var relativeToWorkloads = path["docs/workloads/".Length..];
            var segments = relativeToWorkloads.Split('/');
            var profile = Path.GetFileNameWithoutExtension(path);
            if (segments.Length == 1)
            {
                var workloadManifest = WorkloadGaManifestLoader.Load(Path.Combine(
                    fullRepoRoot,
                    path.Replace('/', Path.DirectorySeparatorChar)));
                Add(
                    $"profile:{workloadManifest.Id}:manifest",
                    path,
                    "workload-profile-manifest",
                    "workload-profile",
                    "normative-input",
                    "source",
                    Versioned(workloadManifest.Version.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)),
                    canonicalId: $"profile:{workloadManifest.Id}",
                    profile: workloadManifest.Id);
                continue;
            }

            var category = segments[0];
            var (type, idSuffix) = category switch
            {
                "approved-runtimes" => ("approved-runtime-record", "approved-runtime"),
                "evidence" => ("workload-evidence", "evidence"),
                "observation" => ("workload-observation-policy", "observation"),
                "qualification" => ("workload-qualification", "qualification"),
                _ => throw new InvalidDataException(
                    $"Unsupported canonical workload source category '{category}' in {path}"),
            };
            Add(
                $"profile:{profile}:{idSuffix}",
                path,
                type,
                "workload-profile",
                "normative-input",
                "source",
                Current(),
                canonicalId: $"profile:{profile}",
                profile: profile);
        }

        Add(
            "workload-profiles:guide",
            "docs/workloads/README.md",
            "workload-profile-guide-index",
            "workload-profile",
            "explanatory",
            "source",
            Current());
        foreach (var path in EnumerateRelativeFiles(
                     fullRepoRoot,
                     "docs/workloads",
                     "*.md"))
        {
            if (path.EndsWith("/README.md", StringComparison.Ordinal))
            {
                continue;
            }

            var profile = Path.GetFileNameWithoutExtension(path);
            Add(
                $"profile:{profile}:guide",
                path,
                "workload-profile-guide",
                "workload-profile",
                "explanatory",
                "source",
                Current(),
                canonicalId: $"profile:{profile}",
                profile: profile);
        }

        Add(
            "qualification:real-azure-conformance",
            "docs/testing/real-azure-conformance.yaml",
            "qualification-matrix",
            "all-services",
            "normative-input",
            "source",
            Current());
        Add(
            "qualification:dynamodb-persisted-format-scenarios",
            "docs/testing/dynamodb-persisted-format-scenarios.yaml",
            "qualification-scenario-source",
            "dynamodb-persisted-format",
            "normative-input",
            "source",
            Current(),
            service: "dynamodb");

        foreach (var path in EnumerateRelativeFiles(
                     fullRepoRoot,
                     "docs/compatibility",
                     "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var versionAt = name.LastIndexOf("-v", StringComparison.Ordinal);
            var version = versionAt < 0 ? name : name[(versionAt + 2)..];
            Add(
                $"compatibility:{DocumentationLinks.Anchor(name)}",
                path,
                "persisted-format-contract",
                "dynamodb-persisted-format",
                "canonical",
                "source",
                Versioned(version),
                service: "dynamodb");
        }

        foreach (var path in EnumerateRelativeFiles(fullRepoRoot, "docs/perf", "*-reference.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            Add(
                $"performance:{DocumentationLinks.Anchor(name)}",
                path,
                "performance-reference",
                "performance-and-footprint",
                "canonical",
                "source",
                Current());
        }

        foreach (var operationDoc in operationDocs)
        {
            var service = DocumentationLinks.Anchor(operationDoc.Service);
            var operation = DocumentationLinks.Anchor(operationDoc.Operation);
            var canonicalId = DocumentationLinks.OperationIdentity(
                operationDoc.Service,
                operationDoc.Operation);
            var freshness = Current(operationDoc.VerifiedRealAzure?.Date);
            Add(
                $"{canonicalId}:source",
                RelativePath(fullRepoRoot, operationDoc.SourceFile),
                "operation-gap-source",
                "operation",
                "normative-capability",
                "source",
                freshness,
                canonicalId,
                service,
                operationDoc.Operation);
            Add(
                $"{canonicalId}:reference",
                $"docs/site/{DocumentationLinks.OperationPage(operationDoc.Service, operationDoc.Operation)}",
                "operation-reference",
                "operation",
                "normative-capability",
                "generated",
                DerivedCurrent(operationDoc.VerifiedRealAzure?.Date),
                canonicalId,
                service,
                operationDoc.Operation);
        }

        foreach (var designDoc in designDocs)
        {
            var service = DocumentationLinks.Anchor(designDoc.Service);
            Add(
                $"service:{service}:design-source",
                RelativePath(fullRepoRoot, designDoc.SourceFile),
                "service-design-gap-source",
                "service",
                "normative-capability",
                "source",
                Current(),
                canonicalId: $"service:{service}:design",
                service: service);
            foreach (var gap in designDoc.DesignGaps)
            {
                var canonicalId = DocumentationLinks.DesignGapIdentity(designDoc.Service, gap.Area);
                Add(
                    $"{canonicalId}:reference",
                    $"docs/site/{DocumentationLinks.DesignGapPage(designDoc.Service, gap.Area)}",
                    "design-gap-reference",
                    "service-design-gap",
                    "normative-capability",
                    "generated",
                    DerivedCurrent(),
                    canonicalId,
                    service);
            }
        }

        Add(
            "qualification:real-azure-migration",
            "docs/gaps/_real_azure_migration.yaml",
            "real-azure-migration-source",
            "all-services",
            "normative-capability",
            "source",
            Current());

        foreach (var path in EnumerateRelativeFiles(fullRepoRoot, "docs/releases", "*"))
        {
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = Path.GetFileNameWithoutExtension(path);
            var separator = fileName.IndexOf('-', StringComparison.Ordinal);
            var version = separator < 0 ? fileName : fileName[..separator];
            var suffix = separator < 0 ? "notes" : fileName[(separator + 1)..];
            Add(
                $"release:{version}:{DocumentationLinks.Anchor(suffix)}",
                path,
                extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                    ? "release-notes"
                    : "release-evidence",
                "release",
                "historical",
                "source",
                Immutable(version));
        }

        documents.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        var manifest = new DocumentationManifest
        {
            Repository = contract.SourceRepository,
            SourceBaseUrl =
                "https://raw.githubusercontent.com/pedrosakuma/aws2azure/main/",
            PublicationBaseUrl = "https://pedrosakuma.github.io/aws2azure/",
            WorkloadAuthority = authority,
            AuthorityPrecedence = ReadAuthorityPrecedence(fullRepoRoot),
            Documents = documents,
        };
        manifest.ManifestRevision = ComputeManifestRevision(manifest);

        var errors = ValidateSnapshot(fullRepoRoot, manifest);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "Documentation manifest validation failed:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => "- " + error)));
        }

        return manifest;
    }

    public static IReadOnlyList<string> Validate(
        string repoRoot,
        DocumentationManifest manifest)
    {
        using var snapshot = DocumentationRepositorySnapshot.Capture(repoRoot, hooks: null);
        return ValidateSnapshot(snapshot.Root, manifest);
    }

    private static IReadOnlyList<string> ValidateSnapshot(
        string repoRoot,
        DocumentationManifest manifest)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Documents)
        {
            if (!IsStableId(entry.Id))
            {
                errors.Add($"document id '{entry.Id}' is not a stable lowercase identifier");
            }
            if (entry.CanonicalId is not null && !IsStableId(entry.CanonicalId))
            {
                errors.Add(
                    $"canonical id '{entry.CanonicalId}' is not a stable lowercase identifier");
            }
            if (!ids.Add(entry.Id))
            {
                errors.Add($"duplicate document id '{entry.Id}'");
            }
            if (!paths.Add(entry.Path))
            {
                errors.Add($"duplicate document path '{entry.Path}'");
            }
            if (entry.Path.Length == 0
                || entry.Path.StartsWith("/", StringComparison.Ordinal)
                || entry.Path.Contains('\\', StringComparison.Ordinal)
                || entry.Path.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                errors.Add($"document path '{entry.Path}' is not repository-relative and normalized");
                continue;
            }
            if (IsTransientPath(entry.Path))
            {
                errors.Add($"document path '{entry.Path}' points to a transient artifact");
            }

            try
            {
                _ = ResolveRepositoryPath(repoRoot, entry.Path);
            }
            catch (Exception exception) when (exception is FileNotFoundException
                                              or DirectoryNotFoundException
                                              or InvalidDataException
                                              or UnauthorizedAccessException)
            {
                errors.Add(exception.Message);
            }

            if (!IsSha256(entry.Revision))
            {
                errors.Add($"document '{entry.Id}' has invalid revision '{entry.Revision}'");
            }
            if (entry.Provenance is not ("source" or "generated"))
            {
                errors.Add($"document '{entry.Id}' has invalid provenance '{entry.Provenance}'");
            }
            if (entry.Authority is not (
                    "current"
                    or "canonical"
                    or "normative-input"
                    or "normative-capability"
                    or "historical"
                    or "explanatory"))
            {
                errors.Add($"document '{entry.Id}' has invalid authority '{entry.Authority}'");
            }
            if (entry.Authority == "historical" && entry.Freshness.Mode != "immutable")
            {
                errors.Add($"historical document '{entry.Id}' must be immutable");
            }
        }

        if (manifest.Documents.Count == 0)
        {
            errors.Add("manifest must contain documents");
        }
        if (!IsSha256(manifest.ManifestRevision))
        {
            errors.Add($"manifest revision '{manifest.ManifestRevision}' is invalid");
        }
        if (!manifest.WorkloadAuthority.CurrentVerdictPath.Equals(
                "docs/site/workload-ga.json",
                StringComparison.Ordinal)
            || !manifest.WorkloadAuthority.ContractPath.Equals(
                WorkloadGaEvaluationMetadataBuilder.ContractPath,
                StringComparison.Ordinal))
        {
            errors.Add("workload authority paths do not identify the current verdict and contract");
        }
        if (manifest.AuthorityPrecedence.Select(entry => entry.Rank)
            .SequenceEqual(Enumerable.Range(1, manifest.AuthorityPrecedence.Count)) is false)
        {
            errors.Add("authority precedence ranks must be contiguous and start at one");
        }
        if (manifest.AuthorityPrecedence.Count != 5
            || manifest.AuthorityPrecedence[0].Source != "live_workload_certification"
            || manifest.AuthorityPrecedence[3].Source != "release_notes"
            || manifest.AuthorityPrecedence[4].Source != "explanatory_guides")
        {
            errors.Add("authority precedence does not preserve current-over-historical semantics");
        }

        ValidateCoverage(repoRoot, manifest.Documents, errors);
        return errors;
    }

    public static string RenderManifest(DocumentationManifest manifest) =>
        JsonSerializer.Serialize(manifest, SerializerOptions) + "\n";

    public static string RenderLlms(DocumentationManifest manifest)
    {
        var authority = manifest.WorkloadAuthority;
        return $$"""
            # aws2azure

            > Vendor-neutral discovery map for the canonical aws2azure documentation and evidence trail.

            aws2azure translates AWS wire-protocol requests into direct Azure REST calls. Compatibility is workload-specific; module availability is not a claim of complete AWS parity.

            ## Authority precedence

            For current adoption decisions, use this order:

            1. [Live workload certification](https://raw.githubusercontent.com/pedrosakuma/aws2azure/main/docs/site/workload-ga.json) - canonical current, point-in-time verdicts.
            2. [Versioned workload manifests](https://pedrosakuma.github.io/aws2azure/workloads/) - normative profile contracts and qualification inputs.
            3. [Gap YAML and generated operation/design-gap artifacts](https://pedrosakuma.github.io/aws2azure/site/) - normative capability detail.
            4. [Immutable historical release notes](https://pedrosakuma.github.io/aws2azure/releases/v1.0.0/) - what was promoted at that time, never the current verdict.
            5. [Explanatory guides](https://pedrosakuma.github.io/aws2azure/) - orientation and procedures, not authority over structured sources.

            A historical GA statement never overrides a later `candidate`, `conditional`, or `blocked` live certification verdict.

            ## Artifact classes

            - **Canonical/current:** `docs/site/workload-ga.json`, its authority contract, and `config.schema.json`.
            - **Generated:** workload certification plus stable operation and design-gap reference pages; regenerate rather than hand-edit.
            - **Historical:** versioned release notes and release evidence under `docs/releases/`.
            - **Explanatory:** portal, configuration, workload, deployment, and operational guides.

            ## Recommended reading order

            1. [Project maturity and support terms](https://pedrosakuma.github.io/aws2azure/project-maturity/)
            2. [Current workload certification](https://pedrosakuma.github.io/aws2azure/site/workload-ga/)
            3. The matching [versioned workload profile](https://pedrosakuma.github.io/aws2azure/workloads/)
            4. [Operation coverage](https://pedrosakuma.github.io/aws2azure/site/coverage/) and [design gaps](https://pedrosakuma.github.io/aws2azure/site/design-gaps/)
            5. [Canonical operator configuration schema](https://raw.githubusercontent.com/pedrosakuma/aws2azure/main/config.schema.json) and [human configuration reference](https://pedrosakuma.github.io/aws2azure/configuration-schema/)
            6. [Production runbook](https://pedrosakuma.github.io/aws2azure/deployment/production-runbook/)

            ## Machine-readable discovery

            - [Documentation manifest](https://pedrosakuma.github.io/aws2azure/documentation-manifest.json) - exhaustive stable IDs, paths, types, scopes, authority classes, provenance, service/operation/profile identities, and content revisions.
            - [Current workload verdicts](https://raw.githubusercontent.com/pedrosakuma/aws2azure/main/docs/site/workload-ga.json)
            - [Workload authority contract](https://raw.githubusercontent.com/pedrosakuma/aws2azure/main/docs/workloads/certification/authority.yaml)
            - [Configuration schema](https://raw.githubusercontent.com/pedrosakuma/aws2azure/main/config.schema.json)

            Current workload authority was evaluated at `{{authority.EvaluatedAsOfUtc}}`.
            Canonical workload inputs revision: `{{authority.CanonicalInputsRevision}}`.
            Evaluator implementation revision: `{{authority.EvaluatorImplementationRevision}}`.
            Documentation manifest revision: `{{manifest.ManifestRevision}}`.
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static DocumentationWorkloadAuthority ReadCurrentWorkloadAuthority(
        string repoRoot,
        WorkloadGaEvaluationContract contract)
    {
        var path = ResolveRepositoryPath(repoRoot, "docs/site/workload-ga.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() == 0)
        {
            throw new InvalidDataException("docs/site/workload-ga.json must contain profile reports");
        }

        var first = document.RootElement[0];
        var evaluation = first.GetProperty("evaluation");
        var source = evaluation.GetProperty("source");
        var evaluatedAsOfUtc = evaluation.GetProperty("evaluated_as_of_utc").GetString()!;
        var canonicalRevision = source.GetProperty("canonical_inputs_revision").GetString()!;
        var evaluatorRevision =
            source.GetProperty("evaluator_implementation_revision").GetString()!;
        foreach (var report in document.RootElement.EnumerateArray())
        {
            var reportEvaluation = report.GetProperty("evaluation");
            var reportSource = reportEvaluation.GetProperty("source");
            if (reportEvaluation.GetProperty("evaluated_as_of_utc").GetString() != evaluatedAsOfUtc
                || reportSource.GetProperty("canonical_inputs_revision").GetString()
                != canonicalRevision
                || reportSource.GetProperty("evaluator_implementation_revision").GetString()
                != evaluatorRevision)
            {
                throw new InvalidDataException(
                    "docs/site/workload-ga.json profile reports do not share one authority revision");
            }
        }

        if (evaluatedAsOfUtc != contract.EvaluatedAsOfUtc
            || canonicalRevision != contract.ExpectedCanonicalInputsRevision
            || evaluatorRevision != contract.ExpectedEvaluatorImplementationRevision)
        {
            throw new InvalidDataException(
                "docs/site/workload-ga.json authority metadata does not match "
                + WorkloadGaEvaluationMetadataBuilder.ContractPath);
        }

        return new DocumentationWorkloadAuthority
        {
            CurrentVerdictPath = "docs/site/workload-ga.json",
            ContractPath = WorkloadGaEvaluationMetadataBuilder.ContractPath,
            EvaluatedAsOfUtc = evaluatedAsOfUtc,
            CanonicalInputsRevisionType =
                source.GetProperty("canonical_inputs_revision_type").GetString()!,
            CanonicalInputsRevision = canonicalRevision,
            EvaluatorSchemaVersion = source.GetProperty("evaluator_schema_version").GetInt32(),
            EvaluatorImplementationRevisionType =
                source.GetProperty("evaluator_implementation_revision_type").GetString()!,
            EvaluatorImplementationRevision = evaluatorRevision,
        };
    }

    private static List<DocumentationAuthorityPrecedence> ReadAuthorityPrecedence(
        string repoRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            ResolveRepositoryPath(repoRoot, "docs/site/workload-ga.json")));
        var authority = document.RootElement[0].GetProperty("authority");
        if (authority.GetProperty("historical_claims_may_override").GetBoolean())
        {
            throw new InvalidDataException(
                "Current workload authority must not permit historical claims to override it");
        }

        return authority.GetProperty("precedence")
            .EnumerateArray()
            .Select(entry => new DocumentationAuthorityPrecedence
            {
                Rank = entry.GetProperty("rank").GetInt32(),
                Source = entry.GetProperty("source").GetString()!,
                Role = entry.GetProperty("role").GetString()!,
            })
            .OrderBy(entry => entry.Rank)
            .ToList();
    }

    private static void ValidateCoverage(
        string repoRoot,
        IReadOnlyList<DocumentationEntry> documents,
        List<string> errors)
    {
        var actualPaths = documents.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        var requiredPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            ManifestRelativePath,
            LlmsRelativePath,
            "config.schema.json",
            "docs/site/workload-ga.json",
            "docs/site/workload-ga.md",
            WorkloadGaEvaluationMetadataBuilder.ContractPath,
            "docs/testing/real-azure-conformance.yaml",
        };
        requiredPaths.Remove(ManifestRelativePath);
        requiredPaths.Remove(LlmsRelativePath);

        foreach (var path in EnumerateRelativeFiles(repoRoot, "docs", "*.yaml", true)
                     .Concat(EnumerateRelativeFiles(repoRoot, "docs", "*.json", true)))
        {
            requiredPaths.Add(path);
        }
        foreach (var path in EnumerateRelativeFiles(repoRoot, "docs/releases", "*"))
        {
            if (Path.GetExtension(path) is ".json" or ".md")
            {
                requiredPaths.Add(path);
            }
        }
        foreach (var path in new[]
                 {
                     "docs/configuration-schema.md",
                     "docs/configuration-reference.md",
                     "docs/configuration-environment.md",
                     "docs/configuration-examples.md",
                     "docs/workloads/README.md",
                 })
        {
            requiredPaths.Add(path);
        }
        foreach (var path in EnumerateRelativeFiles(
                     repoRoot,
                     "docs/configuration/examples",
                     "*.json"))
        {
            requiredPaths.Add(path);
        }
        foreach (var path in EnumerateRelativeFiles(repoRoot, "docs/workloads", "*.md"))
        {
            requiredPaths.Add(path);
        }

        var operationDocs = Loader.LoadAll(Path.Combine(repoRoot, "docs", "gaps"));
        foreach (var operationDoc in operationDocs)
        {
            requiredPaths.Add(
                $"docs/site/{DocumentationLinks.OperationPage(operationDoc.Service, operationDoc.Operation)}");
        }
        var designDocs = Loader.LoadDesignDocs(Path.Combine(repoRoot, "docs", "gaps"));
        foreach (var designDoc in designDocs)
        {
            foreach (var gap in designDoc.DesignGaps)
            {
                requiredPaths.Add(
                    $"docs/site/{DocumentationLinks.DesignGapPage(designDoc.Service, gap.Area)}");
            }
        }

        foreach (var missing in requiredPaths.Except(actualPaths).Order(StringComparer.Ordinal))
        {
            errors.Add($"required canonical documentation path is not indexed: {missing}");
        }
    }

    private static string ComputeManifestRevision(DocumentationManifest manifest)
    {
        var builder = new StringBuilder();
        builder.Append(manifest.SchemaVersion).Append('\n')
            .Append(manifest.Repository).Append('\n')
            .Append(manifest.SourceBaseUrl).Append('\n')
            .Append(manifest.PublicationBaseUrl).Append('\n')
            .Append(manifest.WorkloadAuthority.EvaluatedAsOfUtc).Append('\n')
            .Append(manifest.WorkloadAuthority.CanonicalInputsRevision).Append('\n')
            .Append(manifest.WorkloadAuthority.EvaluatorImplementationRevision).Append('\n');
        foreach (var precedence in manifest.AuthorityPrecedence.OrderBy(entry => entry.Rank))
        {
            builder.Append(precedence.Rank).Append('\t')
                .Append(precedence.Source).Append('\t')
                .Append(precedence.Role).Append('\n');
        }
        foreach (var entry in manifest.Documents.OrderBy(entry => entry.Id, StringComparer.Ordinal))
        {
            builder.Append(entry.Id).Append('\t')
                .Append(entry.Path).Append('\t')
                .Append(entry.Type).Append('\t')
                .Append(entry.Scope).Append('\t')
                .Append(entry.Authority).Append('\t')
                .Append(entry.Provenance).Append('\t')
                .Append(entry.Revision).Append('\n');
        }
        return HashText(builder.ToString());
    }

    private static string ComputeFileRevision(string repoRoot, string relativePath)
    {
        var path = ResolveRepositoryPath(repoRoot, relativePath);
        var content = File.ReadAllText(path);
        if (content.Length > 0 && content[0] == '\uFEFF')
        {
            content = content[1..];
        }
        content = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        return HashText(content);
    }

    private static string HashText(string content) =>
        "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static bool IsSha256(string value) =>
        value.Length == 71
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool IsStableId(string value)
    {
        if (value.Length == 0 || value[0] is < 'a' or > 'z')
        {
            return false;
        }
        return value.All(character =>
            character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or ':'
            or '.'
            or '_'
            or '-');
    }

    private static bool IsTransientPath(string path)
    {
        var normalized = "/" + path.ToLowerInvariant() + "/";
        return normalized.Contains("/.git/", StringComparison.Ordinal)
               || normalized.Contains("/bin/", StringComparison.Ordinal)
               || normalized.Contains("/obj/", StringComparison.Ordinal)
               || normalized.Contains("/testresults/", StringComparison.Ordinal)
               || normalized.Contains("/artifacts/", StringComparison.Ordinal)
               || normalized.Contains("/site/", StringComparison.Ordinal)
                  && !normalized.Contains("/docs/site/", StringComparison.Ordinal);
    }

    private static string RelativePath(string repoRoot, string path)
    {
        var relativePath = Path.GetRelativePath(
                Path.GetFullPath(repoRoot),
                Path.GetFullPath(path))
            .Replace('\\', '/');
        _ = ResolveRepositoryPath(repoRoot, relativePath);
        return relativePath;
    }

    private static IReadOnlyList<string> EnumerateRelativeFiles(
        string repoRoot,
        string relativeRoot,
        string pattern,
        bool recursive = false)
    {
        var root = ResolveRepositoryPath(repoRoot, relativeRoot, requireDirectory: true);
        var pending = new Stack<string>();
        var visited = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        var results = new List<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (!visited.Add(directory))
            {
                var relativeDirectory = Path.GetRelativePath(repoRoot, directory).Replace('\\', '/');
                throw new InvalidDataException(
                    $"Documentation traversal cycle detected at '{relativeDirectory}'");
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
            {
                var relativePath = Path.GetRelativePath(repoRoot, entry).Replace('\\', '/');
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw ReparsePointException(relativePath, relativePath);
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (recursive)
                    {
                        pending.Push(ResolveRepositoryPath(
                            repoRoot,
                            relativePath,
                            requireDirectory: true));
                    }
                    continue;
                }

                _ = ResolveRepositoryPath(repoRoot, relativePath);
                if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                        pattern,
                        Path.GetFileName(entry),
                        ignoreCase: false))
                {
                    results.Add(relativePath);
                }
            }
        }

        results.Sort(StringComparer.Ordinal);
        return results;
    }

    private static string ResolveRepositoryPath(
        string repoRoot,
        string relativePath,
        bool requireDirectory = false,
        bool allowMissingLeaf = false)
    {
        var fullRoot = Path.GetFullPath(repoRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        ValidateNotReparsePoint(fullRoot, ".");

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException(
                $"Documentation path '{relativePath}' must be repository-relative");
        }

        var normalizedPath = relativePath.Replace('\\', '/');
        var fullPath = Path.GetFullPath(Path.Combine(
            fullRoot,
            normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Documentation path '{normalizedPath}' escapes the repository root");
        }

        var segments = relative.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        var current = fullRoot;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            var componentPath = Path.GetRelativePath(fullRoot, current).Replace('\\', '/');
            try
            {
                ValidateNotReparsePoint(current, normalizedPath, componentPath);
            }
            catch (FileNotFoundException) when (allowMissingLeaf && index == segments.Length - 1)
            {
                return fullPath;
            }
            catch (DirectoryNotFoundException) when (
                allowMissingLeaf && index == segments.Length - 1)
            {
                return fullPath;
            }
            catch (Exception exception) when (exception is FileNotFoundException
                                              or DirectoryNotFoundException)
            {
                throw new FileNotFoundException(
                    $"Document path '{normalizedPath}' does not resolve to a repository file "
                    + $"(missing component '{componentPath}')",
                    normalizedPath,
                    exception);
            }
        }

        if (requireDirectory && !Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"Documentation source directory not found: {normalizedPath}");
        }
        if (!requireDirectory && !File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Document path '{normalizedPath}' does not resolve to a repository file",
                normalizedPath);
        }

        return fullPath;
    }

    private static void ValidateNotReparsePoint(
        string path,
        string documentPath,
        string? componentPath = null)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw ReparsePointException(documentPath, componentPath ?? documentPath);
        }
    }

    private static InvalidDataException ReparsePointException(
        string documentPath,
        string componentPath) =>
        new(
            $"Documentation path '{documentPath}' contains a symbolic link or reparse point "
            + $"at '{componentPath}'");

    private static byte[] ReadRepositoryFile(
        string repoRoot,
        string relativePath,
        DocumentationIoHooks? hooks) =>
        ReadRepositoryFile(
            repoRoot,
            ResolveCanonicalRepositoryRoot(repoRoot),
            relativePath,
            hooks);

    private static byte[] ReadRepositoryFile(
        string repoRoot,
        string canonicalRepoRoot,
        string relativePath,
        DocumentationIoHooks? hooks)
    {
        var expectedPath = ResolveRepositoryPath(repoRoot, relativePath);
        using var rootHandle = OpenRepositoryDirectory(repoRoot);
        hooks?.AfterPathValidationBeforeIo?.Invoke(
            new DocumentationIoEvent(relativePath, DocumentationIoOperation.Read));

        using var handle = OperatingSystem.IsWindows()
            ? File.OpenHandle(
                expectedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                FileOptions.SequentialScan)
            : OpenRelativeFile(rootHandle, relativePath);
        if (OperatingSystem.IsWindows())
        {
            ValidateOpenedHandle(
                repoRoot,
                canonicalRepoRoot,
                relativePath,
                expectedPath,
                handle);
        }

        using var stream = new FileStream(handle, FileAccess.Read);
        if (stream.Length > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Documentation path '{relativePath}' exceeds the supported snapshot size");
        }

        var content = new byte[stream.Length];
        stream.ReadExactly(content);
        return content;
    }

    private static bool RepositoryFileMatches(
        string repoRoot,
        string relativePath,
        ReadOnlySpan<byte> expected)
    {
        try
        {
            return ReadRepositoryFile(repoRoot, relativePath, hooks: null)
                .AsSpan()
                .SequenceEqual(expected);
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                          or DirectoryNotFoundException)
        {
            return false;
        }
    }

    internal static void WriteRepositoryFile(
        string repoRoot,
        string relativePath,
        ReadOnlySpan<byte> content,
        DocumentationIoHooks? hooks)
    {
        var canonicalRepoRoot = ResolveCanonicalRepositoryRoot(repoRoot);
        var destination = ResolveRepositoryPath(
            repoRoot,
            relativePath,
            allowMissingLeaf: true);
        using var rootHandle = OpenRepositoryDirectory(repoRoot);
        ValidatePublicationRoot(repoRoot, canonicalRepoRoot, rootHandle);

        using var temporary = CreatePrivateTemporaryFile(
            rootHandle,
            repoRoot);
        ValidatePrivateTemporaryFile(
            rootHandle,
            repoRoot,
            canonicalRepoRoot,
            temporary);
        if (GetHandleLinkCount(temporary.Handle) != 1)
        {
            throw new InvalidDataException(
                $"Private documentation output '{temporary.Name}' has multiple hard links");
        }

        RandomAccess.Write(temporary.Handle, content, fileOffset: 0);
        RandomAccess.FlushToDisk(temporary.Handle);
        ValidatePrivateTemporaryFile(
            rootHandle,
            repoRoot,
            canonicalRepoRoot,
            temporary);

        hooks?.BeforeAtomicPublish?.Invoke(
            new DocumentationPublishEvent(relativePath, temporary.Path));

        ValidatePublicationRoot(repoRoot, canonicalRepoRoot, rootHandle);
        ValidatePrivateTemporaryFile(
            rootHandle,
            repoRoot,
            canonicalRepoRoot,
            temporary);
        if (GetHandleLinkCount(temporary.Handle) != 1)
        {
            throw new InvalidDataException(
                $"Private documentation output '{temporary.Name}' has multiple hard links");
        }
        ValidateDestinationEntry(
            rootHandle,
            repoRoot,
            canonicalRepoRoot,
            relativePath,
            destination);
        AtomicPublish(
            rootHandle,
            temporary.Handle,
            temporary.Name,
            relativePath);
        temporary.Published = true;
    }

    private static SafeFileHandle OpenRepositoryDirectory(string repoRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = CreateFile(
                Path.GetFullPath(repoRoot),
                FileListDirectory | Synchronize,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw LastIoException("Could not open the repository directory safely");
            }
            return handle;
        }

        var descriptor = Open(
            Path.GetFullPath(repoRoot),
            UnixOpenReadOnly | UnixOpenDirectory | UnixOpenCloseOnExec | UnixOpenNoFollow);
        if (descriptor < 0)
        {
            throw LastIoException("Could not open the repository directory safely");
        }
        return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
    }

    private static PrivateTemporaryFile CreatePrivateTemporaryFile(
        SafeFileHandle rootHandle,
        string repoRoot)
    {
        var temporaryName = $".documentation-{Guid.NewGuid():N}.tmp";
        var temporaryPath = Path.Combine(repoRoot, temporaryName);
        if (OperatingSystem.IsWindows())
        {
            var handle = CreateFile(
                temporaryPath,
                GenericRead | GenericWrite | DeleteAccess,
                FileShareRead | FileShareDelete,
                IntPtr.Zero,
                CreateNew,
                FileFlagOpenReparsePoint | FileFlagWriteThrough,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw LastIoException(
                    $"Could not create private documentation output '{temporaryName}'");
            }
            return new PrivateTemporaryFile(
                rootHandle,
                handle,
                temporaryName,
                temporaryPath);
        }

        if (OperatingSystem.IsMacOS())
        {
            var template = Encoding.UTF8.GetBytes(
                Path.Combine(
                    repoRoot,
                    $".documentation-{Guid.NewGuid():N}-XXXXXX")
                + "\0");
            var descriptor = MakeTemporaryFile(template);
            if (descriptor < 0)
            {
                throw LastIoException(
                    "Could not create a private documentation output on macOS");
            }
            var terminator = Array.IndexOf(template, (byte)0);
            temporaryPath = Encoding.UTF8.GetString(template, 0, terminator);
            temporaryName = Path.GetFileName(temporaryPath);
            return new PrivateTemporaryFile(
                rootHandle,
                new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true),
                temporaryName,
                temporaryPath);
        }

        var linuxDescriptor = OpenAtCreate(
            rootHandle.DangerousGetHandle().ToInt32(),
            temporaryName,
            UnixOpenReadWrite
            | UnixOpenCreate
            | UnixOpenExclusive
            | UnixOpenCloseOnExec
            | UnixOpenNoFollow,
            Convert.ToUInt32("600", 8));
        if (linuxDescriptor < 0)
        {
            throw LastIoException(
                $"Could not create private documentation output '{temporaryName}'");
        }
        return new PrivateTemporaryFile(
            rootHandle,
            new SafeFileHandle(new IntPtr(linuxDescriptor), ownsHandle: true),
            temporaryName,
            temporaryPath);
    }

    private static SafeFileHandle OpenRelativeFile(
        SafeFileHandle rootHandle,
        string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/');
        SafeFileHandle? currentDirectory = null;
        try
        {
            var directoryDescriptor = rootHandle.DangerousGetHandle().ToInt32();
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var descriptor = OpenAtNoMode(
                    directoryDescriptor,
                    segments[index],
                    UnixOpenReadOnly
                    | UnixOpenDirectory
                    | UnixOpenCloseOnExec
                    | UnixOpenNoFollow);
                if (descriptor < 0)
                {
                    throw new InvalidDataException(
                        $"Documentation path '{relativePath}' contains an unsafe directory "
                        + $"component '{segments[index]}'",
                        LastIoException("Handle-relative directory open failed"));
                }
                currentDirectory?.Dispose();
                currentDirectory = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
                directoryDescriptor = descriptor;
            }

            var fileDescriptor = OpenAtNoMode(
                directoryDescriptor,
                segments[^1],
                UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow);
            if (fileDescriptor < 0)
            {
                throw new InvalidDataException(
                    $"Documentation path '{relativePath}' could not be opened without following links",
                    LastIoException("Handle-relative file open failed"));
            }
            return new SafeFileHandle(new IntPtr(fileDescriptor), ownsHandle: true);
        }
        finally
        {
            currentDirectory?.Dispose();
        }
    }

    private static void ValidatePrivateTemporaryFile(
        SafeFileHandle rootHandle,
        string repoRoot,
        string canonicalRepoRoot,
        PrivateTemporaryFile temporary)
    {
        if (OperatingSystem.IsWindows())
        {
            ValidateOpenedHandle(
                repoRoot,
                canonicalRepoRoot,
                temporary.Name,
                temporary.Path,
                temporary.Handle);
            return;
        }

        using var currentEntry = OpenRelativeFile(rootHandle, temporary.Name);
        if (GetHandleIdentity(currentEntry) != GetHandleIdentity(temporary.Handle))
        {
            throw new InvalidDataException(
                $"Private documentation output '{temporary.Name}' changed before publication");
        }
    }

    private static void ValidatePublicationRoot(
        string repoRoot,
        string canonicalRepoRoot,
        SafeFileHandle rootHandle)
    {
        if (!OperatingSystem.IsWindows())
        {
            using var currentRoot = OpenRepositoryDirectory(repoRoot);
            if (GetHandleIdentity(currentRoot) != GetHandleIdentity(rootHandle))
            {
                throw new InvalidDataException(
                    "The repository directory changed during documentation publication");
            }
            return;
        }

        var resolvedRoot = Path.GetFullPath(ResolveOpenedHandlePath(rootHandle))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!resolvedRoot.Equals(canonicalRepoRoot, comparison)
            || !ResolveCanonicalRepositoryRoot(repoRoot).Equals(
                canonicalRepoRoot,
                comparison))
        {
            throw new InvalidDataException(
                "The repository directory changed during documentation publication");
        }
    }

    private static void ValidateDestinationEntry(
        SafeFileHandle rootHandle,
        string repoRoot,
        string canonicalRepoRoot,
        string relativePath,
        string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            var descriptor = OpenAtNoMode(
                rootHandle.DangerousGetHandle().ToInt32(),
                relativePath,
                UnixOpenReadOnly | UnixOpenCloseOnExec | UnixOpenNoFollow);
            if (descriptor < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 2)
                {
                    return;
                }
                throw new InvalidDataException(
                    $"Documentation output '{relativePath}' is an unsafe existing entry "
                    + $"(errno {error})");
            }
            using var unixHandle = new SafeFileHandle(
                new IntPtr(descriptor),
                ownsHandle: true);
            if (GetHandleLinkCount(unixHandle) != 1)
            {
                throw new InvalidDataException(
                    $"Documentation output '{relativePath}' must not be a hard link");
            }
            return;
        }

        if (!File.Exists(destination))
        {
            return;
        }

        using var handle = File.OpenHandle(
            destination,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Write | FileShare.Delete);
        ValidateOpenedHandle(
            repoRoot,
            canonicalRepoRoot,
            relativePath,
            destination,
            handle);
        if (GetHandleLinkCount(handle) != 1)
        {
            throw new InvalidDataException(
                $"Documentation output '{relativePath}' must not be a hard link");
        }
    }

    private static void AtomicPublish(
        SafeFileHandle rootHandle,
        SafeFileHandle temporaryHandle,
        string temporaryName,
        string relativePath)
    {
        if (relativePath.Contains('/', StringComparison.Ordinal)
            || relativePath.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Documentation output '{relativePath}' must be in the repository root");
        }

        if (OperatingSystem.IsWindows())
        {
            RenameFileByHandle(
                temporaryHandle,
                rootHandle,
                relativePath);
            return;
        }

        var rootDescriptor = rootHandle.DangerousGetHandle().ToInt32();
        if (RenameAt(
                rootDescriptor,
                temporaryName,
                rootDescriptor,
                relativePath) != 0)
        {
            throw LastIoException(
                $"Could not publish documentation output '{relativePath}' atomically");
        }
    }

    private static void RenameFileByHandle(
        SafeFileHandle fileHandle,
        SafeFileHandle rootHandle,
        string destinationName)
    {
        var destinationBytes = Encoding.Unicode.GetBytes(destinationName);
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var bufferSize = checked(nameOffset + destinationBytes.Length);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        var ioStatusBlock = Marshal.AllocHGlobal(IntPtr.Size * 2);
        try
        {
            for (var index = 0; index < bufferSize; index++)
            {
                Marshal.WriteByte(buffer, index, 0);
            }
            Marshal.WriteByte(buffer, 0, 1);
            Marshal.WriteIntPtr(buffer, rootOffset, rootHandle.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, destinationBytes.Length);
            Marshal.Copy(destinationBytes, 0, IntPtr.Add(buffer, nameOffset), destinationBytes.Length);
            Marshal.WriteIntPtr(ioStatusBlock, 0, IntPtr.Zero);
            Marshal.WriteIntPtr(ioStatusBlock, IntPtr.Size, IntPtr.Zero);

            var status = NtSetInformationFile(
                    fileHandle,
                    ioStatusBlock,
                    buffer,
                    (uint)bufferSize,
                    FileRenameInformation);
            if (status < 0)
            {
                throw new IOException(
                    $"Could not publish documentation output '{destinationName}' atomically "
                    + $"(NTSTATUS 0x{status:x8})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ioStatusBlock);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static uint GetHandleLinkCount(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw LastIoException("Could not inspect documentation output link count");
            }
            return information.NumberOfLinks;
        }

        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (CallFStat(handle.DangerousGetHandle().ToInt32(), buffer) != 0)
            {
                throw LastIoException("Could not inspect documentation output link count");
            }
            if (OperatingSystem.IsMacOS())
            {
                return unchecked((ushort)Marshal.ReadInt16(buffer, 6));
            }
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => checked((uint)Marshal.ReadInt64(buffer, 16)),
                Architecture.Arm64 => unchecked((uint)Marshal.ReadInt32(buffer, 20)),
                _ => throw new PlatformNotSupportedException(
                    "Documentation publication supports Linux x64 and arm64"),
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static FileIdentity GetHandleIdentity(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (CallFStat(handle.DangerousGetHandle().ToInt32(), buffer) != 0)
            {
                throw LastIoException("Could not inspect a documentation file identity");
            }
            return OperatingSystem.IsMacOS()
                ? new FileIdentity(
                    unchecked((uint)Marshal.ReadInt32(buffer, 0)),
                    unchecked((ulong)Marshal.ReadInt64(buffer, 8)))
                : new FileIdentity(
                    unchecked((ulong)Marshal.ReadInt64(buffer, 0)),
                    unchecked((ulong)Marshal.ReadInt64(buffer, 8)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int CallFStat(int descriptor, IntPtr buffer) =>
        OperatingSystem.IsMacOS()
        && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? FStatInode64(descriptor, buffer)
            : FStat(descriptor, buffer);

    private static void ValidateOpenedHandle(
        string repoRoot,
        string canonicalRepoRoot,
        string relativePath,
        string expectedPath,
        SafeFileHandle handle)
    {
        var resolvedPath = ResolveOpenedHandlePath(handle);
        var relative = Path.GetRelativePath(canonicalRepoRoot, resolvedPath);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Opened documentation path '{relativePath}' resolves outside the repository root");
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var expectedRelativePath = Path.GetRelativePath(
            Path.GetFullPath(repoRoot),
            Path.GetFullPath(expectedPath));
        var canonicalExpectedPath = Path.GetFullPath(
            Path.Combine(canonicalRepoRoot, expectedRelativePath));
        if (!canonicalExpectedPath.Equals(Path.GetFullPath(resolvedPath), comparison))
        {
            throw new InvalidDataException(
                $"Opened documentation path '{relativePath}' changed after validation");
        }
    }

    private static string ResolveCanonicalRepositoryRoot(string repoRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Path.GetFullPath(repoRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var markerPath = Path.Combine(Path.GetFullPath(repoRoot), "aws2azure.slnx");
        using var handle = File.OpenHandle(
            markerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Write | FileShare.Delete);
        return Path.GetDirectoryName(ResolveOpenedHandlePath(handle))
               ?? throw new InvalidDataException(
                   "Could not resolve the canonical repository root");
    }

    private static string ResolveOpenedHandlePath(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            var capacity = 512;
            while (true)
            {
                var buffer = new StringBuilder(capacity);
                var length = GetFinalPathNameByHandle(
                    handle,
                    buffer,
                    (uint)buffer.Capacity,
                    0);
                if (length == 0)
                {
                    throw new IOException(
                        "Could not resolve an opened documentation file handle",
                        Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
                }
                if (length < buffer.Capacity)
                {
                    var path = buffer.ToString();
                    if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                    {
                        return @"\\" + path[8..];
                    }
                    return path.StartsWith(@"\\?\", StringComparison.Ordinal)
                        ? path[4..]
                        : path;
                }
                capacity = checked((int)length + 1);
            }
        }

        var descriptor = handle.DangerousGetHandle().ToInt32();
        var descriptorPath = Path.Combine(
            "/proc/self/fd",
            descriptor.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        var target = File.ResolveLinkTarget(descriptorPath, returnFinalTarget: true);
        if (target is not null)
        {
            return Path.GetFullPath(target.FullName);
        }

        throw new PlatformNotSupportedException(
            "The platform cannot resolve opened documentation file handles safely");
    }

    private static IOException LastIoException(string message) =>
        new(
            message,
            Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

    private static int UnixOpenCreate =>
        OperatingSystem.IsMacOS() ? 0x0200 : 0x0040;

    private static int UnixOpenExclusive =>
        OperatingSystem.IsMacOS() ? 0x0800 : 0x0080;

    private static int UnixOpenNoFollow =>
        OperatingSystem.IsMacOS() ? 0x0100 : 0x20000;

    private static int UnixOpenDirectory =>
        OperatingSystem.IsMacOS() ? 0x100000 : 0x10000;

    private static int UnixOpenCloseOnExec =>
        OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

    private const int UnixOpenReadOnly = 0;
    private const int UnixOpenReadWrite = 2;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileRenameInformation = 10;
    private const int FileDispositionInformation = 13;

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(
        SafeFileHandle file,
        IntPtr ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtNoMode(int directory, string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtCreate(int directory, string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "mkstemp", SetLastError = true)]
    private static extern int MakeTemporaryFile([In, Out] byte[] template);

    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
    private static extern int RenameAt(
        int oldDirectory,
        string oldPath,
        int newDirectory,
        string newPath);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(int directory, string path, int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int descriptor, IntPtr buffer);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int FStatInode64(int descriptor, IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal FileTime CreationTime;
        internal FileTime LastAccessTime;
        internal FileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    internal sealed class DocumentationIoHooks
    {
        internal Action<DocumentationIoEvent>? AfterPathValidationBeforeIo { get; init; }
        internal Action<DocumentationPublishEvent>? BeforeAtomicPublish { get; init; }
    }

    internal readonly record struct DocumentationIoEvent(
        string RelativePath,
        DocumentationIoOperation Operation);

    internal readonly record struct DocumentationPublishEvent(
        string RelativePath,
        string TemporaryPath);

    internal enum DocumentationIoOperation
    {
        Read,
        Write,
    }

    private readonly record struct FileIdentity(ulong Device, ulong Inode);

    private sealed class PrivateTemporaryFile : IDisposable
    {
        internal PrivateTemporaryFile(
            SafeFileHandle rootHandle,
            SafeFileHandle handle,
            string name,
            string path)
        {
            RootHandle = rootHandle;
            Handle = handle;
            Name = name;
            Path = path;
        }

        private SafeFileHandle RootHandle { get; }
        internal SafeFileHandle Handle { get; }
        internal string Name { get; }
        internal string Path { get; }
        internal bool Published { get; set; }

        public void Dispose()
        {
            try
            {
                if (!Published)
                {
                    DeletePrivateFile();
                }
            }
            finally
            {
                Handle.Dispose();
            }
        }

        private void DeletePrivateFile()
        {
            if (OperatingSystem.IsWindows())
            {
                var disposition = Marshal.AllocHGlobal(1);
                var ioStatusBlock = Marshal.AllocHGlobal(IntPtr.Size * 2);
                try
                {
                    Marshal.WriteByte(disposition, 0, 1);
                    Marshal.WriteIntPtr(ioStatusBlock, 0, IntPtr.Zero);
                    Marshal.WriteIntPtr(ioStatusBlock, IntPtr.Size, IntPtr.Zero);
                    var status = NtSetInformationFile(
                        Handle,
                        ioStatusBlock,
                        disposition,
                        1,
                        FileDispositionInformation);
                    if (status < 0)
                    {
                        throw new IOException(
                            $"Could not remove private documentation output '{Name}' "
                            + $"(NTSTATUS 0x{status:x8})");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ioStatusBlock);
                    Marshal.FreeHGlobal(disposition);
                }
                return;
            }

            var result = UnlinkAt(
                RootHandle.DangerousGetHandle().ToInt32(),
                Name,
                flags: 0);
            if (result != 0 && Marshal.GetLastPInvokeError() != 2)
            {
                throw LastIoException(
                    $"Could not remove private documentation output '{Name}'");
            }
        }
    }

    private sealed class DocumentationRepositorySnapshot : IDisposable
    {
        private DocumentationRepositorySnapshot(string root)
        {
            Root = root;
        }

        internal string Root { get; }

        internal static DocumentationRepositorySnapshot Capture(
            string repoRoot,
            DocumentationIoHooks? hooks)
        {
            var fullRepoRoot = Path.GetFullPath(repoRoot);
            var canonicalRepoRoot = ResolveCanonicalRepositoryRoot(fullRepoRoot);
            var files = EnumerateRelativeFiles(fullRepoRoot, "docs", "*", recursive: true)
                .Append("aws2azure.slnx")
                .Append("config.schema.json")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(path => (
                    Path: path,
                    Content: ReadRepositoryFile(
                        fullRepoRoot,
                        canonicalRepoRoot,
                        path,
                        hooks)))
                .ToList();

            var snapshotRoot = Path.Combine(
                Path.GetTempPath(),
                $"aws2azure-documentation-snapshot-{Guid.NewGuid():N}");
            Directory.CreateDirectory(snapshotRoot);
            try
            {
                foreach (var file in files)
                {
                    var destination = Path.Combine(
                        snapshotRoot,
                        file.Path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.WriteAllBytes(destination, file.Content);
                }
                return new DocumentationRepositorySnapshot(snapshotRoot);
            }
            catch
            {
                Directory.Delete(snapshotRoot, recursive: true);
                throw;
            }
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private static DocumentationFreshness Current(string? verifiedRealAzureDate = null) =>
        new()
        {
            Mode = "current",
            VerifiedRealAzureDate = verifiedRealAzureDate,
        };

    private static DocumentationFreshness DerivedCurrent(
        string? verifiedRealAzureDate = null) =>
        new()
        {
            Mode = "derived-current",
            VerifiedRealAzureDate = verifiedRealAzureDate,
        };

    private static DocumentationFreshness PointInTime(string evaluatedAsOfUtc) =>
        new()
        {
            Mode = "point-in-time",
            EvaluatedAsOfUtc = evaluatedAsOfUtc,
        };

    private static DocumentationFreshness Versioned(string version) =>
        new()
        {
            Mode = "versioned",
            Version = version,
        };

    private static DocumentationFreshness Immutable(string version) =>
        new()
        {
            Mode = "immutable",
            Version = version,
        };

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aws2azure.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate repository root (aws2azure.slnx not found)");
    }
}
