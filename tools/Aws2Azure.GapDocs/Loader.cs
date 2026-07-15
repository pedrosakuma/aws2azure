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
    public static IReadOnlyList<string> Validate(
        IReadOnlyList<OperationDoc> docs,
        RealAzureMigrationDoc migration,
        DateOnly currentDate)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var docsByKey = docs
            .GroupBy(OperationKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var migrationKeys = ValidateMigration(migration, docsByKey, currentDate, errors);

        foreach (var doc in docs)
        {
            void Err(string msg) => errors.Add($"{doc.SourceFile}: {msg}");

            if (string.IsNullOrWhiteSpace(doc.Service)) Err("missing required field 'service'");
            if (string.IsNullOrWhiteSpace(doc.Operation)) Err("missing required field 'operation'");
            if (string.IsNullOrWhiteSpace(doc.AzureEquivalent)) Err("missing required field 'azure_equivalent'");

            if (!StatusValues.Operation.Contains(doc.Status))
            {
                Err($"invalid status '{doc.Status}'; allowed: {string.Join(", ", StatusValues.Operation)}");
            }
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

            for (var i = 0; i < doc.SubFeatures.Count; i++)
            {
                var sf = doc.SubFeatures[i];
                if (string.IsNullOrWhiteSpace(sf.Name)) Err($"sub_features[{i}].name missing");
                if (!StatusValues.SubFeature.Contains(sf.Status))
                {
                    Err($"sub_features[{i}] invalid status '{sf.Status}'");
                }
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
                if (string.IsNullOrWhiteSpace(g.Summary)) Err($"design_gaps[{i}].summary missing");
                if (!StatusValues.DesignGap.Contains(g.Status))
                {
                    Err($"design_gaps[{i}] invalid status '{g.Status}'; allowed: {string.Join(", ", StatusValues.DesignGap)}");
                }
            }

            var designGapsByArea = new Dictionary<string, DesignGap>(StringComparer.OrdinalIgnoreCase);
            foreach (var gap in doc.DesignGaps.Where(g => !string.IsNullOrWhiteSpace(g.Area)))
            {
                if (!designGapsByArea.TryAdd(gap.Area, gap))
                {
                    Err($"duplicate design gap area '{gap.Area}'");
                }
            }
            var seenPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            operationsByService.TryGetValue(service, out var serviceOperations);

            for (var i = 0; i < doc.WorkloadPatterns.Count; i++)
            {
                var pattern = doc.WorkloadPatterns[i];
                var prefix = $"workload_patterns[{i}]";
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
}
