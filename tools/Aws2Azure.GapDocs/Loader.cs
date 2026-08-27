using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aws2Azure.GapDocs;

public static class Loader
{
    public static IReadOnlyList<OperationDoc> LoadAll(string gapsRoot)
    {
        if (!Directory.Exists(gapsRoot))
        {
            throw new FileNotFoundException("Gaps directory not found", gapsRoot);
        }

        // No IgnoreUnmatchedProperties(): unknown keys (e.g. a "note:" typo for
        // "notes:") must fail loud rather than silently dropping documented content.
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var results = new List<OperationDoc>();
        foreach (var file in Directory.EnumerateFiles(gapsRoot, "*.yaml", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            // Files starting with '_' are non-operation docs (e.g. _design.yaml
            // holds cross-cutting design gaps); they use a different schema and
            // are loaded by LoadDesignDocs.
            if (Path.GetFileName(file).StartsWith('_'))
            {
                continue;
            }

            using var reader = new StreamReader(file);
            var doc = deserializer.Deserialize<OperationDoc>(reader);
            if (doc is null)
            {
                throw new InvalidDataException($"{file}: empty document");
            }
            doc.SourceFile = file;
            results.Add(doc);
        }
        return results;
    }

    public static IReadOnlyList<ServiceDesignDoc> LoadDesignDocs(string gapsRoot)
    {
        if (!Directory.Exists(gapsRoot))
        {
            throw new FileNotFoundException("Gaps directory not found", gapsRoot);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var results = new List<ServiceDesignDoc>();
        foreach (var file in Directory.EnumerateFiles(gapsRoot, "_design.yaml", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            using var reader = new StreamReader(file);
            var doc = deserializer.Deserialize<ServiceDesignDoc>(reader);
            if (doc is null)
            {
                throw new InvalidDataException($"{file}: empty document");
            }
            doc.SourceFile = file;
            results.Add(doc);
        }
        return results;
    }

    public static RealAzureMigrationDoc LoadRealAzureMigration(string gapsRoot)
    {
        var file = Path.Combine(gapsRoot, "_real_azure_migration.yaml");
        if (!File.Exists(file))
        {
            throw new FileNotFoundException("Real-Azure migration manifest not found", file);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        using var reader = new StreamReader(file);
        var doc = deserializer.Deserialize<RealAzureMigrationDoc>(reader);
        if (doc is null)
        {
            throw new InvalidDataException($"{file}: empty document");
        }
        doc.SourceFile = file;
        return doc;
    }
}

public static class Validator
{
    private static readonly HashSet<string> ReservedServicePages = new(
        [
            "index.md",
            "coverage.md",
            "completeness.md",
            "workload-compatibility.md",
            "workload-ga.md",
            "divergences.md",
            "design-gaps.md",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ReservedWorkloadCompatibilityAnchors = new(
        [
            "workload-compatibility",
            "service-coverage-profile",
            "adoption-decision",
            "automated-workload-check",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Validate(
        IReadOnlyList<OperationDoc> docs,
        RealAzureMigrationDoc migration,
        DateOnly currentDate)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDocumentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var serviceSlugOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var docsByKey = docs
            .GroupBy(OperationKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var migrationKeys = ValidateMigration(migration, docsByKey, currentDate, errors);
        foreach (var serviceGroup in docs
                     .Where(doc => !string.IsNullOrWhiteSpace(doc.Service))
                     .GroupBy(doc => doc.Service, StringComparer.OrdinalIgnoreCase))
        {
            var owner = serviceGroup.Key;
            var slug = DocumentationLinks.Anchor(owner);
            if (serviceSlugOwners.TryGetValue(slug, out var existing)
                && !existing.Equals(owner, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"{serviceGroup.First().SourceFile}: service '{owner}' documentation identity " +
                    $"collides with service '{existing}' as '{slug}'");
            }
            else
            {
                serviceSlugOwners[slug] = owner;
            }
            var servicePage = DocumentationLinks.ServicePage(owner);
            if (ReservedServicePages.Contains(servicePage))
            {
                errors.Add(
                    $"{serviceGroup.First().SourceFile}: service '{owner}' documentation page " +
                    $"'{servicePage}' is reserved for an aggregate generated page");
            }

            var servicePageAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                DocumentationLinks.Anchor(owner),
                DocumentationLinks.ServiceCanonicalAnchor(owner)
            };
            foreach (var operation in serviceGroup)
            {
                var compatibilityAnchor = DocumentationLinks.OperationCompatibilityAnchor(operation.Operation);
                if (!servicePageAnchors.Add(compatibilityAnchor))
                {
                    errors.Add(
                        $"{operation.SourceFile}: operation '{operation.Operation}' documentation anchor " +
                        $"'{compatibilityAnchor}' collides on service page '{servicePage}'");
                }
            }
        }

        foreach (var doc in docs)
        {
            void Err(string msg) => errors.Add($"{doc.SourceFile}: {msg}");

            if (string.IsNullOrWhiteSpace(doc.Service)) Err("missing required field 'service'");
            if (string.IsNullOrWhiteSpace(doc.Operation)) Err("missing required field 'operation'");
            if (string.IsNullOrWhiteSpace(doc.AzureEquivalent)) Err("missing required field 'azure_equivalent'");
            if (DocumentationLinks.Anchor(doc.Service).Length == 0)
            {
                Err($"service '{doc.Service}' does not produce a stable documentation identity");
            }
            if (DocumentationLinks.Anchor(doc.Operation).Length == 0)
            {
                Err($"operation '{doc.Operation}' does not produce a stable documentation identity");
            }

            if (!StatusValues.Operation.Contains(doc.Status))
            {
                Err($"invalid status '{doc.Status}'; allowed: {string.Join(", ", StatusValues.Operation)}");
            }
            ValidateDisposition(
                doc.Status,
                doc.Disposition,
                doc.TrackingIssue,
                context: $"operation '{doc.Operation}'",
                implementedStatus: "implemented",
                allowImplementedDisposition: false,
                error: Err);
            if (doc.VerifiedRealAzure is not null)
            {
                ValidateVerification(doc.VerifiedRealAzure, "verified_real_azure", Err);
            }
            else if (doc.Status.Equals("implemented", StringComparison.OrdinalIgnoreCase)
                     && !migrationKeys.Contains(OperationKey(doc)))
            {
                Err(
                    "status 'implemented' requires a valid 'verified_real_azure' seal; " +
                    "use status 'partial' until real-Azure evidence exists");
            }

            var expectedDir = Path.Combine("docs", "gaps", doc.Service.ToLowerInvariant());
            if (!doc.SourceFile.Replace('\\', '/').Contains("/" + expectedDir.Replace('\\', '/') + "/"))
            {
                Err($"file should live under {expectedDir}/ (got service='{doc.Service}')");
            }

            var key = doc.Service.ToLowerInvariant() + "/" + doc.Operation;
            if (!seen.Add(key))
            {
                Err($"duplicate service/operation pair '{key}'");
            }
            var documentPath = DocumentationLinks.OperationPage(doc.Service, doc.Operation);
            if (!seenDocumentPaths.Add(documentPath))
            {
                Err($"operation documentation path '{documentPath}' collides with another operation");
            }

            var seenSubFeatureAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < doc.SubFeatures.Count; i++)
            {
                var sf = doc.SubFeatures[i];
                if (string.IsNullOrWhiteSpace(sf.Name)) Err($"sub_features[{i}].name missing");
                var subFeatureAnchor = DocumentationLinks.SubFeatureAnchor(sf.Name);
                if (subFeatureAnchor == "sub-feature-")
                {
                    Err($"sub_features[{i}] '{sf.Name}' does not produce a stable documentation anchor");
                }
                else if (!seenSubFeatureAnchors.Add(subFeatureAnchor))
                {
                    Err($"sub_features[{i}] '{sf.Name}' produces duplicate documentation anchor '{subFeatureAnchor}'");
                }
                if (!StatusValues.SubFeature.Contains(sf.Status))
                {
                    Err($"sub_features[{i}] invalid status '{sf.Status}'");
                }
                ValidateDisposition(
                    sf.Status,
                    sf.Disposition,
                    sf.TrackingIssue,
                    context: $"sub_features[{i}] '{sf.Name}'",
                    implementedStatus: "implemented",
                    allowImplementedDisposition: false,
                    error: Err);
                if (sf.VerifiedRealAzure is not null)
                {
                    ValidateVerification(sf.VerifiedRealAzure, $"sub_features[{i}].verified_real_azure", Err);
                }
            }
        }

        return errors;
    }

    private static HashSet<string> ValidateMigration(
        RealAzureMigrationDoc migration,
        IReadOnlyDictionary<string, OperationDoc> docsByKey,
        DateOnly currentDate,
        List<string> errors)
    {
        var allowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Err(string msg) => errors.Add($"{migration.SourceFile}: {msg}");

        for (var i = 0; i < migration.Services.Count; i++)
        {
            var entry = migration.Services[i];
            var prefix = $"services[{i}]";
            if (string.IsNullOrWhiteSpace(entry.Service))
            {
                Err($"{prefix}.service missing");
                continue;
            }
            if (!services.Add(entry.Service))
            {
                Err($"{prefix} duplicates service '{entry.Service}'");
            }
            if (!IsGitHubIssueUrl(entry.TrackingIssue))
            {
                Err($"{prefix}.tracking_issue must be a GitHub issue URL ending in /issues/<id>");
            }
            if (!TryParseDate(entry.ExpiresOn, out var expiresOn))
            {
                Err($"{prefix}.expires_on must use YYYY-MM-DD");
            }
            else if (expiresOn > MigrationDeadline)
            {
                Err(
                    $"{prefix}.expires_on cannot extend the migration beyond " +
                    $"{MigrationDeadline:yyyy-MM-dd}");
            }
            else if (expiresOn < currentDate)
            {
                Err(
                    $"{prefix} expired on {entry.ExpiresOn}; seal or reclassify its operations " +
                    "before extending the migration");
            }
            if (entry.Operations.Count == 0)
            {
                Err($"{prefix}.operations must contain at least one operation");
            }

            foreach (var operation in entry.Operations)
            {
                var key = entry.Service.ToLowerInvariant() + "/" + operation;
                var isLegacyDebt = LegacyUnsealedOperations.Contains(key);
                if (!isLegacyDebt)
                {
                    Err(
                        $"{prefix} cannot add '{key}'; the migration may only shrink " +
                        "the fixed legacy real-Azure debt baseline");
                }
                if (!seenKeys.Add(key))
                {
                    Err($"{prefix} duplicates migration operation '{key}'");
                    continue;
                }
                if (!docsByKey.TryGetValue(key, out var doc))
                {
                    Err($"{prefix} references unknown operation '{key}'");
                }
                else if (!doc.Status.Equals("implemented", StringComparison.OrdinalIgnoreCase))
                {
                    Err($"{prefix} contains stale operation '{key}' with status '{doc.Status}'");
                }
                else if (doc.VerifiedRealAzure is not null)
                {
                    Err($"{prefix} contains stale operation '{key}' that already has a real-Azure seal");
                }
                else if (isLegacyDebt)
                {
                    allowedKeys.Add(key);
                }
            }
        }

        return allowedKeys;
    }

    private static void ValidateVerification(
        RealAzureVerification verification,
        string field,
        Action<string> error)
    {
        if (!TryParseDate(verification.Date, out _))
        {
            error($"{field}.date must use YYYY-MM-DD");
        }
        if (!IsHttpsUrl(verification.Evidence))
        {
            error($"{field}.evidence must be an absolute HTTPS URL");
        }
        if (!string.IsNullOrWhiteSpace(verification.WorkflowRun)
            && !IsGitHubActionsRunUrl(verification.WorkflowRun))
        {
            error($"{field}.workflow_run must be a GitHub Actions URL ending in /actions/runs/<id>");
        }
    }

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    private static bool IsHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(uri.Host);

    private static bool IsGitHubActionsRunUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 5
            && segments[2].Equals("actions", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("runs", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(segments[4], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static bool IsGitHubIssueUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 4
            && segments[2].Equals("issues", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static void ValidateDisposition(
        string status,
        string disposition,
        string trackingIssue,
        string context,
        string implementedStatus,
        bool allowImplementedDisposition,
        Action<string> error)
    {
        var isImplemented = status.Equals(implementedStatus, StringComparison.OrdinalIgnoreCase);
        var hasDisposition = !string.IsNullOrWhiteSpace(disposition);
        if (!hasDisposition)
        {
            if (!isImplemented)
            {
                error($"{context} with status '{status}' must declare disposition");
            }

            if (!string.IsNullOrWhiteSpace(trackingIssue))
            {
                error($"{context} cannot declare tracking_issue without disposition");
            }

            return;
        }

        if (isImplemented && !allowImplementedDisposition)
        {
            error($"{context} with status '{status}' must not declare disposition");
            if (!string.IsNullOrWhiteSpace(trackingIssue))
            {
                error($"{context} with status '{status}' must not declare tracking_issue");
            }

            return;
        }

        if (!StatusValues.Disposition.Contains(disposition))
        {
            error(
                $"{context} has invalid disposition '{disposition}'; allowed: " +
                $"{string.Join(", ", StatusValues.Disposition)}");
            return;
        }

        if (isImplemented
            && allowImplementedDisposition
            && !disposition.Equals(implementedStatus, StringComparison.OrdinalIgnoreCase))
        {
            error(
                $"{context} with status '{status}' may only declare disposition '{implementedStatus}'");
        }

        var isFeasibleBacklog = disposition.Equals("feasible_backlog", StringComparison.OrdinalIgnoreCase);
        if (isFeasibleBacklog)
        {
            if (!IsIssueReference(trackingIssue))
            {
                error($"{context} with disposition 'feasible_backlog' must declare tracking_issue as '#<number>'");
            }
        }
        else if (!string.IsNullOrWhiteSpace(trackingIssue))
        {
            error($"{context} with disposition '{disposition}' must not declare tracking_issue");
        }

        if (isImplemented && !string.IsNullOrWhiteSpace(trackingIssue))
        {
            error($"{context} with status '{status}' must not declare tracking_issue");
        }
    }

    private static bool IsIssueReference(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length > 1
        && value[0] == '#'
        && int.TryParse(value.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static string OperationKey(OperationDoc doc) =>
        doc.Service.ToLowerInvariant() + "/" + doc.Operation;

    private static readonly HashSet<string> LegacyUnsealedOperations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "dynamodb/CreateTable",
            "dynamodb/DeleteTable",
            "dynamodb/DescribeTable",
            "dynamodb/ListTables",
            "dynamodb/ListTagsOfResource",
            "dynamodb/TagResource",
            "dynamodb/UntagResource",
            "s3/AbortMultipartUpload",
            "s3/CompleteMultipartUpload",
            "s3/CopyObject",
            "s3/CreateBucket",
            "s3/CreateMultipartUpload",
            "s3/DeleteBucket",
            "s3/DeleteBucketTagging",
            "s3/DeleteObject",
            "s3/DeleteObjectTagging",
            "s3/DeleteObjects",
            "s3/GetObject",
            "s3/GetObjectTagging",
            "s3/HeadBucket",
            "s3/HeadObject",
            "s3/ListBuckets",
            "s3/ListObjects",
            "s3/ListObjectsV2",
            "s3/ListParts",
            "s3/PresignedUrl",
            "s3/PutObject",
            "s3/PutObjectTagging",
            "s3/UploadPart",
            "s3/UploadPartCopy",
            "secretsmanager/CreateSecret",
            "secretsmanager/DeleteSecret",
            "secretsmanager/DescribeSecret",
            "secretsmanager/GetSecretValue",
            "secretsmanager/ListSecrets",
            "secretsmanager/UpdateSecret",
            "sqs/CreateQueue",
            "sqs/DeleteMessage",
            "sqs/DeleteMessageBatch",
            "sqs/DeleteQueue",
            "sqs/GetQueueUrl",
            "sqs/ListDeadLetterSourceQueues",
            "sqs/ListQueues",
            "sqs/ReceiveMessage",
            "sqs/SendMessage",
            "sqs/SendMessageBatch"
        };

    private static readonly DateOnly MigrationDeadline = new(2026, 10, 31);

    public static IReadOnlyList<string> ValidateDesign(
        IReadOnlyList<ServiceDesignDoc> designDocs,
        IReadOnlyList<OperationDoc> operationDocs)
    {
        var errors = new List<string>();
        var knownServices = new HashSet<string>(
            operationDocs.Select(o => o.Service.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
        var seenServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var serviceSlugOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seenPatternIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var designGapIndexAnchors = new HashSet<string>(
            designDocs
                .Where(doc => !string.IsNullOrWhiteSpace(doc.Service))
                .Select(doc => DocumentationLinks.Anchor(doc.Service)),
            StringComparer.OrdinalIgnoreCase)
        {
            "design-gaps",
            "summary"
        };
        var operationsByService = operationDocs
            .GroupBy(o => o.Service, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(o => o.Operation, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(ops => ops.Key, ops => ops.First(), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        foreach (var doc in designDocs)
        {
            void Err(string msg) => errors.Add($"{doc.SourceFile}: {msg}");

            if (string.IsNullOrWhiteSpace(doc.Service))
            {
                Err("missing required field 'service'");
                continue;
            }

            var service = doc.Service.ToLowerInvariant();
            var serviceSlug = DocumentationLinks.Anchor(service);
            if (serviceSlugOwners.TryGetValue(serviceSlug, out var existingService)
                && !existingService.Equals(service, StringComparison.OrdinalIgnoreCase))
            {
                Err(
                    $"service '{doc.Service}' documentation identity collides with service " +
                    $"'{existingService}' as '{serviceSlug}'");
            }
            else
            {
                serviceSlugOwners[serviceSlug] = service;
            }
            if (doc.WorkloadPatterns.Count > 0
                && ReservedWorkloadCompatibilityAnchors.Contains(serviceSlug))
            {
                Err(
                    $"service '{doc.Service}' documentation anchor '{serviceSlug}' is reserved " +
                    "on aggregate page 'workload-compatibility.md'");
            }
            var expectedDir = Path.Combine("docs", "gaps", service);
            if (!doc.SourceFile.Replace('\\', '/').Contains("/" + expectedDir.Replace('\\', '/') + "/"))
            {
                Err($"file should live under {expectedDir}/ (got service='{doc.Service}')");
            }

            if (!knownServices.Contains(service))
            {
                Err($"service '{doc.Service}' has no operation gap docs; design gaps must attach to a known service");
            }

            if (!seenServices.Add(service))
            {
                Err($"duplicate _design.yaml for service '{service}'");
            }

            if (doc.DesignGaps.Count == 0)
            {
                Err("must declare at least one entry under 'design_gaps'");
            }

            for (var i = 0; i < doc.DesignGaps.Count; i++)
            {
                var g = doc.DesignGaps[i];
                if (string.IsNullOrWhiteSpace(g.Area)) Err($"design_gaps[{i}].area missing");
                if (DocumentationLinks.Anchor(g.Area).Length == 0)
                {
                    Err($"design_gaps[{i}] '{g.Area}' does not produce a stable documentation identity");
                }
                if (string.IsNullOrWhiteSpace(g.ReadinessChecklistQuestion))
                {
                    Err($"design_gaps[{i}].readiness_checklist_question missing");
                }
                if (string.IsNullOrWhiteSpace(g.Summary)) Err($"design_gaps[{i}].summary missing");
                if (!StatusValues.DesignGap.Contains(g.Status))
                {
                    Err($"design_gaps[{i}] invalid status '{g.Status}'; allowed: {string.Join(", ", StatusValues.DesignGap)}");
                }
                ValidateDisposition(
                    g.Status,
                    g.Disposition,
                    g.TrackingIssue,
                    context: $"design_gaps[{i}] '{g.Area}'",
                    implementedStatus: "by_design",
                    allowImplementedDisposition: true,
                    error: Err);
            }

            var designGapsByArea = new Dictionary<string, DesignGap>(StringComparer.OrdinalIgnoreCase);
            var designGapPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var gap in doc.DesignGaps.Where(g => !string.IsNullOrWhiteSpace(g.Area)))
            {
                if (!designGapsByArea.TryAdd(gap.Area, gap))
                {
                    Err($"duplicate design gap area '{gap.Area}'");
                }
                var documentPath = DocumentationLinks.DesignGapPage(doc.Service, gap.Area);
                if (!designGapPaths.Add(documentPath))
                {
                    Err($"design gap '{gap.Area}' produces duplicate documentation path '{documentPath}'");
                }
                var compatibilityAnchor = DocumentationLinks.DesignGapCompatibilityAnchor(doc.Service, gap.Area);
                if (!designGapIndexAnchors.Add(compatibilityAnchor))
                {
                    Err(
                        $"design gap '{gap.Area}' documentation anchor '{compatibilityAnchor}' " +
                        "collides on aggregate page 'design-gaps.md'");
                }
            }
            var seenPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            operationsByService.TryGetValue(service, out var serviceOperations);

            for (var i = 0; i < doc.WorkloadPatterns.Count; i++)
            {
                var pattern = doc.WorkloadPatterns[i];
                var prefix = $"workload_patterns[{i}]";
                if (string.IsNullOrWhiteSpace(pattern.Id))
                {
                    Err($"{prefix}.id missing");
                }
                else
                {
                    if (!IsRequirementId(pattern.Id))
                    {
                        Err($"{prefix}.id '{pattern.Id}' must use lowercase letters, digits, and underscores, starting with a letter");
                    }
                    if (!seenPatternIds.Add(pattern.Id))
                    {
                        Err($"{prefix} duplicates workload pattern id '{pattern.Id}'");
                    }
                }
                if (string.IsNullOrWhiteSpace(pattern.Name))
                {
                    Err($"{prefix}.name missing");
                }
                else if (!seenPatterns.Add(pattern.Name))
                {
                    Err($"{prefix} duplicates workload pattern '{pattern.Name}'");
                }

                if (!StatusValues.WorkloadCompatibility.Contains(pattern.Compatibility))
                {
                    Err($"{prefix} invalid compatibility '{pattern.Compatibility}'; allowed: {string.Join(", ", StatusValues.WorkloadCompatibility)}");
                }
                if (string.IsNullOrWhiteSpace(pattern.Summary)) Err($"{prefix}.summary missing");
                if (string.IsNullOrWhiteSpace(pattern.Guidance)) Err($"{prefix}.guidance missing");
                if (pattern.Operations.Count == 0 && pattern.DesignGaps.Count == 0)
                {
                    Err($"{prefix} must reference at least one operation or design gap");
                }

                var seenOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var operation in pattern.Operations)
                {
                    if (!seenOperations.Add(operation))
                    {
                        Err($"{prefix} repeats operation '{operation}'");
                    }
                    if (serviceOperations is null || !serviceOperations.ContainsKey(operation))
                    {
                        Err($"{prefix} references unknown operation '{operation}' for service '{service}'");
                    }
                }
                var seenDesignGaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var area in pattern.DesignGaps)
                {
                    if (!seenDesignGaps.Add(area))
                    {
                        Err($"{prefix} repeats design gap '{area}'");
                    }
                    if (!designGapsByArea.ContainsKey(area))
                    {
                        Err($"{prefix} references unknown design gap '{area}' for service '{service}'");
                    }
                }

                if (pattern.Compatibility.Equals("supported", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var operation in pattern.Operations)
                    {
                        if (serviceOperations is not null
                            && serviceOperations.TryGetValue(operation, out var operationDoc)
                            && !operationDoc.Status.Equals("implemented", StringComparison.OrdinalIgnoreCase))
                        {
                            Err($"{prefix} cannot be supported because operation '{operation}' is '{operationDoc.Status}'");
                        }
                    }
                    if (pattern.DesignGaps.Count > 0)
                    {
                        Err($"{prefix} cannot be supported while referencing design gaps");
                    }
                }
            }
        }

        return errors;
    }

    private static bool IsRequirementId(string value)
    {
        if (value.Length == 0 || value[0] is < 'a' or > 'z')
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c is not (>= 'a' and <= 'z') && c is not (>= '0' and <= '9') && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
