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

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var results = new List<OperationDoc>();
        foreach (var file in Directory.EnumerateFiles(gapsRoot, "*.yaml", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
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
}
