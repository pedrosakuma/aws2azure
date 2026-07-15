using System;
using System.Collections.Generic;
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
}

public static class Validator
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<OperationDoc> docs)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            }
        }

        return errors;
    }

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
