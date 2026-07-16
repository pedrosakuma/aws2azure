using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aws2Azure.GapDocs;

public sealed class EmulatorQualificationMetadata
{
    public string RunId { get; set; } = string.Empty;
    public string RunUrl { get; set; } = string.Empty;
    public string GitSha { get; set; } = string.Empty;
    public string ArtifactDigest { get; set; } = string.Empty;
    public string ConfigDigest { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public int MaxRowAgeHours { get; set; } = 2;
}

public static class EmulatorQualificationGenerator
{
    public static SloQualificationDocument Generate(
        string referencePath,
        string latestPath,
        EmulatorQualificationMetadata metadata)
    {
        var reference = LoadReference(referencePath);
        var latest = LoadLatest(latestPath);
        ValidateInputDocuments(reference, latest);
        if (reference.Scenarios.Count == 0)
        {
            throw new InvalidDataException($"{referencePath}: scenarios missing or empty");
        }
        if (latest.Scenarios.Count == 0)
        {
            throw new InvalidDataException($"{latestPath}: scenarios missing or empty");
        }
        if (metadata.MaxRowAgeHours <= 0
            || metadata.MaxRowAgeHours > TimeSpan.MaxValue.TotalHours)
        {
            throw new ArgumentException("Max row age must be within the TimeSpan range.", nameof(metadata));
        }

        var orderedNames = reference.Scenarios.Keys
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        var scenarioIds = orderedNames
            .Select((name, index) => (name, id: $"scenario-{index + 1:D3}"))
            .ToDictionary(entry => entry.name, entry => entry.id, StringComparer.Ordinal);
        var availableRows = latest.Scenarios
            .Where(entry => reference.Scenarios.ContainsKey(entry.Key))
            .ToList();
        if (availableRows.Count == 0)
        {
            throw new InvalidDataException("No latest perf rows match the reference scenarios.");
        }

        var document = new SloQualificationDocument
        {
            SchemaVersion = 1,
            ArtifactKind = "emulator_regression",
            Profile = new SloQualificationProfile
            {
                Id = "emulator-regression-suite",
                Version = 1,
                Services =
                [
                    new SloQualificationProfileService
                    {
                        Service = "perf",
                        Operations = orderedNames
                    }
                ]
            },
            Candidate = new SloQualificationCandidate
            {
                GitSha = metadata.GitSha,
                ArtifactDigest = metadata.ArtifactDigest,
                ConfigDigest = metadata.ConfigDigest
            },
            Provenance = new SloQualificationProvenance
            {
                RunId = metadata.RunId,
                RunUrl = metadata.RunUrl,
                GeneratedAtUtc = metadata.GeneratedAtUtc.ToUniversalTime(),
                WindowStartUtc = availableRows.Min(entry => entry.Value.CapturedAtUtc).ToUniversalTime(),
                WindowEndUtc = availableRows.Max(entry => entry.Value.CapturedAtUtc).ToUniversalTime()
                    .AddTicks(1)
            },
            Rules = new SloQualificationRules
            {
                MaxArtifactAgeHours = metadata.MaxRowAgeHours,
                MinSamplesPerScenario = 1,
                MinDurationSeconds = 0.001,
                MaxFailureRate = 0.10,
                ZeroCompletionsDisqualify = true,
                OnlySkippedRealAzureDisqualifies = true
            }
        };

        var failed = false;
        var inconclusive = false;
        var freshness = TimeSpan.FromHours(metadata.MaxRowAgeHours);

        foreach (var name in orderedNames)
        {
            if (!latest.Scenarios.TryGetValue(name, out var row))
            {
                inconclusive = true;
                AddFinding(
                    document,
                    "missing_scenario",
                    "blocking",
                    null,
                    $"Reference scenario did not produce a result: {name}");
                continue;
            }

            ValidateFiniteRow(name, row);
            var scenarioId = scenarioIds[name];
            document.Scenarios.Add(new SloQualificationScenario
            {
                Id = scenarioId,
                Service = "perf",
                Operation = name,
                EvidenceSource = "emulator",
                Completions = row.Completed,
                Failures = row.Failures,
                DurationSeconds = row.ElapsedSeconds,
                CapturedAtUtc = row.CapturedAtUtc.ToUniversalTime()
            });

            if (metadata.GeneratedAtUtc.ToUniversalTime() - row.CapturedAtUtc.ToUniversalTime() > freshness)
            {
                inconclusive = true;
                AddFinding(
                    document,
                    "stale_scenario",
                    "blocking",
                    scenarioId,
                    $"Scenario result is older than {metadata.MaxRowAgeHours} hours.");
            }
            if (row.Completed == 0)
            {
                failed = true;
                AddFinding(
                    document,
                    "zero_completions",
                    "blocking",
                    scenarioId,
                    "Scenario completed zero operations.");
            }

            var attempts = (double)row.Completed + row.Failures;
            var failureRate = attempts == 0 ? 0 : row.Failures / attempts;
            if (failureRate > document.Rules.MaxFailureRate)
            {
                failed = true;
                AddFinding(
                    document,
                    "failure_rate_exceeded",
                    "blocking",
                    scenarioId,
                    $"Failure rate {failureRate:F6} exceeds {document.Rules.MaxFailureRate:F6}.");
            }
            else if (row.Failures > 0)
            {
                AddFinding(
                    document,
                    "nonzero_failures",
                    "advisory",
                    scenarioId,
                    $"Scenario reported {row.Failures} failures ({failureRate:P2}).");
            }

            var threshold = reference.Scenarios[name];
            failed |= AddSignal(
                document,
                scenarioId,
                "throughput",
                "throughput_per_sec",
                row.ThroughputPerSec,
                row.Completed,
                row.CapturedAtUtc,
                minValue: PositiveOrNull(threshold.MinThroughputPerSec));
            failed |= AddSignal(
                document,
                scenarioId,
                "p99",
                "p99_ms",
                row.P99Ms,
                row.Completed,
                row.CapturedAtUtc,
                maxValue: PositiveOrNull(threshold.MaxP99Ms));

            if (threshold.MaxPeakWorkingSetMb > 0)
            {
                if (row.MemoryMeasured)
                {
                    failed |= AddSignal(
                        document,
                        scenarioId,
                        "working-set",
                        "peak_working_set_mb",
                        row.PeakWorkingSetMb,
                        row.Completed,
                        row.CapturedAtUtc,
                        maxValue: threshold.MaxPeakWorkingSetMb);
                }
                else
                {
                    inconclusive = true;
                    AddFinding(
                        document,
                        "memory_not_measured",
                        "blocking",
                        scenarioId,
                        "Peak working-set gate was configured but memory was not measured.");
                }
            }
            if (threshold.MaxAllocBytesPerOp > 0)
            {
                if (row.MemoryMeasured)
                {
                    failed |= AddSignal(
                        document,
                        scenarioId,
                        "allocation",
                        "alloc_bytes_per_op",
                        row.AllocBytesPerOp,
                        row.Completed,
                        row.CapturedAtUtc,
                        maxValue: threshold.MaxAllocBytesPerOp);
                }
                else
                {
                    inconclusive = true;
                    AddFinding(
                        document,
                        "allocation_not_measured",
                        "blocking",
                        scenarioId,
                        "Allocation gate was configured but memory was not measured.");
                }
            }
        }

        foreach (var name in latest.Scenarios.Keys
                     .Where(name => !reference.Scenarios.ContainsKey(name))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            inconclusive = true;
            AddFinding(
                document,
                "untracked_scenario",
                "blocking",
                null,
                $"Latest results contain a scenario absent from baseline-reference.json: {name}");
        }

        foreach (var (proxyName, pairing) in reference.Pairings.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!latest.Scenarios.TryGetValue(proxyName, out var proxy)
                || !latest.Scenarios.TryGetValue(pairing.Baseline, out var baseline)
                || !scenarioIds.TryGetValue(proxyName, out var scenarioId))
            {
                inconclusive = true;
                AddFinding(
                    document,
                    "relative_pair_missing",
                    "blocking",
                    null,
                    $"Relative pairing could not be evaluated: {proxyName} vs {pairing.Baseline}");
                continue;
            }
            if (proxy.Completed == 0 || baseline.Completed == 0)
            {
                inconclusive = true;
                AddFinding(
                    document,
                    "relative_pair_zero_completions",
                    "blocking",
                    scenarioId,
                    $"Relative pairing has zero completions: proxy={proxy.Completed}, baseline={baseline.Completed}.");
                continue;
            }
            if ((proxy.CapturedAtUtc - baseline.CapturedAtUtc).Duration() > freshness)
            {
                inconclusive = true;
                AddFinding(
                    document,
                    "relative_pair_stale",
                    "blocking",
                    scenarioId,
                    $"Relative pairing rows were captured more than {metadata.MaxRowAgeHours} hours apart.");
                continue;
            }

            if (pairing.MinThroughputRatio > 0)
            {
                failed |= AddRatioSignal(
                    document,
                    scenarioId,
                    "throughput-ratio",
                    "throughput_ratio",
                    proxy.ThroughputPerSec,
                    baseline.ThroughputPerSec,
                    proxy.Completed,
                    proxy.CapturedAtUtc,
                    out var throughputEvaluable,
                    minValue: pairing.MinThroughputRatio);
                inconclusive |= !throughputEvaluable;
            }
            if (pairing.MaxP50Ratio > 0)
            {
                failed |= AddRatioSignal(
                    document,
                    scenarioId,
                    "p50-ratio",
                    "p50_ratio",
                    proxy.P50Ms,
                    baseline.P50Ms,
                    proxy.Completed,
                    proxy.CapturedAtUtc,
                    out var p50Evaluable,
                    maxValue: pairing.MaxP50Ratio);
                inconclusive |= !p50Evaluable;
            }
            if (pairing.MaxP99Ratio > 0)
            {
                failed |= AddRatioSignal(
                    document,
                    scenarioId,
                    "p99-ratio",
                    "p99_ratio",
                    proxy.P99Ms,
                    baseline.P99Ms,
                    proxy.Completed,
                    proxy.CapturedAtUtc,
                    out var p99Evaluable,
                    maxValue: pairing.MaxP99Ratio);
                inconclusive |= !p99Evaluable;
            }
        }

        document.Verdict = failed ? "failed" : inconclusive ? "inconclusive" : "passed";
        return document;
    }

    public static void RenderYaml(SloQualificationDocument document, string outputPath)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new DateTimeOffsetYamlTypeConverter())
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(outputPath, serializer.Serialize(document));
    }

    private static EmulatorReferenceDocument LoadReference(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Perf reference file not found", path);
        }
        return JsonSerializer.Deserialize(
                   File.ReadAllText(path),
                   EmulatorQualificationJsonContext.Default.EmulatorReferenceDocument)
               ?? throw new InvalidDataException($"{path}: empty reference document");
    }

    private static EmulatorLatestDocument LoadLatest(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Perf latest file not found", path);
        }
        return JsonSerializer.Deserialize(
                   File.ReadAllText(path),
                   EmulatorQualificationJsonContext.Default.EmulatorLatestDocument)
               ?? throw new InvalidDataException($"{path}: empty latest document");
    }

    private static void ValidateInputDocuments(
        EmulatorReferenceDocument reference,
        EmulatorLatestDocument latest)
    {
        if (reference.Scenarios is null)
        {
            throw new InvalidDataException("Perf reference scenarios must not be null.");
        }
        if (reference.Pairings is null)
        {
            throw new InvalidDataException("Perf reference pairings must not be null.");
        }
        if (latest.Scenarios is null)
        {
            throw new InvalidDataException("Latest perf scenarios must not be null.");
        }

        foreach (var (name, threshold) in reference.Scenarios)
        {
            if (threshold is null)
            {
                throw new InvalidDataException($"Perf reference scenario must not be null: {name}");
            }
        }
        foreach (var (name, pairing) in reference.Pairings)
        {
            if (pairing is null)
            {
                throw new InvalidDataException($"Perf reference pairing must not be null: {name}");
            }
        }
        foreach (var (name, row) in latest.Scenarios)
        {
            if (row is null)
            {
                throw new InvalidDataException($"Latest perf scenario must not be null: {name}");
            }
        }
    }

    private static bool AddSignal(
        SloQualificationDocument document,
        string scenarioId,
        string suffix,
        string metric,
        double measuredValue,
        long samples,
        DateTimeOffset capturedAtUtc,
        double? minValue = null,
        double? maxValue = null)
    {
        var blocking = minValue is not null || maxValue is not null;
        document.Signals.Add(new SloQualificationSignal
        {
            Id = $"{scenarioId}-{suffix}",
            ScenarioId = scenarioId,
            Source = "proxy_overhead",
            Disposition = blocking ? "blocking" : "report_only",
            Metric = metric,
            MinValue = minValue,
            MaxValue = maxValue,
            MeasuredValue = measuredValue,
            Samples = samples,
            CapturedAtUtc = capturedAtUtc.ToUniversalTime()
        });
        return (minValue is not null && measuredValue < minValue)
               || (maxValue is not null && measuredValue > maxValue);
    }

    private static bool AddRatioSignal(
        SloQualificationDocument document,
        string scenarioId,
        string suffix,
        string metric,
        double numerator,
        double denominator,
        long samples,
        DateTimeOffset capturedAtUtc,
        out bool evaluable,
        double? minValue = null,
        double? maxValue = null)
    {
        if (denominator <= 0)
        {
            evaluable = false;
            AddFinding(
                document,
                "relative_baseline_not_positive",
                "blocking",
                scenarioId,
                $"Relative metric '{metric}' requires a positive baseline value; observed {denominator}.");
            return false;
        }

        evaluable = true;
        return AddSignal(
            document,
            scenarioId,
            suffix,
            metric,
            numerator / denominator,
            samples,
            capturedAtUtc,
            minValue,
            maxValue);
    }

    private static double? PositiveOrNull(double value) => value > 0 ? value : null;

    private static void AddFinding(
        SloQualificationDocument document,
        string code,
        string disposition,
        string? scenarioId,
        string message)
    {
        document.Findings.Add(new SloQualificationFinding
        {
            Code = code,
            Disposition = disposition,
            ScenarioId = scenarioId,
            Message = message
        });
    }

    private static void ValidateFiniteRow(string name, EmulatorLatestRow row)
    {
        if (!double.IsFinite(row.ElapsedSeconds)
            || !double.IsFinite(row.ThroughputPerSec)
            || !double.IsFinite(row.P50Ms)
            || !double.IsFinite(row.P95Ms)
            || !double.IsFinite(row.P99Ms)
            || !double.IsFinite(row.PeakWorkingSetMb)
            || !double.IsFinite(row.AllocBytesPerOp))
        {
            throw new InvalidDataException($"Latest perf row contains a non-finite value: {name}");
        }
        if (row.Completed < 0 || row.Failures < 0)
        {
            throw new InvalidDataException($"Latest perf row contains a negative count: {name}");
        }
    }

    private sealed class DateTimeOffsetYamlTypeConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(DateTimeOffset);

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            return DateTimeOffset.Parse(
                parser.Consume<Scalar>().Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal);
        }

        public void WriteYaml(
            IEmitter emitter,
            object? value,
            Type type,
            ObjectSerializer serializer)
        {
            var timestamp = ((DateTimeOffset)value!).ToUniversalTime();
            emitter.Emit(new Scalar(timestamp.ToString("O", CultureInfo.InvariantCulture)));
        }
    }
}

public sealed class EmulatorReferenceDocument
{
    public Dictionary<string, EmulatorReferenceThreshold> Scenarios { get; set; } = new();
    public Dictionary<string, EmulatorReferencePairing> Pairings { get; set; } = new();
}

public sealed class EmulatorReferenceThreshold
{
    public double MinThroughputPerSec { get; set; }
    public double MaxP99Ms { get; set; }
    public double MaxPeakWorkingSetMb { get; set; }
    public double MaxAllocBytesPerOp { get; set; }
}

public sealed class EmulatorReferencePairing
{
    public string Baseline { get; set; } = string.Empty;
    public double MinThroughputRatio { get; set; }
    public double MaxP50Ratio { get; set; }
    public double MaxP99Ratio { get; set; }
}

public sealed class EmulatorLatestDocument
{
    public Dictionary<string, EmulatorLatestRow> Scenarios { get; set; } = new();
}

public sealed class EmulatorLatestRow
{
    public double ElapsedSeconds { get; set; }
    public long Completed { get; set; }
    public long Failures { get; set; }
    public double ThroughputPerSec { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
    public bool MemoryMeasured { get; set; }
    public double PeakWorkingSetMb { get; set; }
    public double AllocBytesPerOp { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(EmulatorReferenceDocument))]
[JsonSerializable(typeof(EmulatorLatestDocument))]
internal sealed partial class EmulatorQualificationJsonContext : JsonSerializerContext
{
}
