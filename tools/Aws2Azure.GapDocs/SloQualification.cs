using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aws2Azure.GapDocs;

public sealed class SloQualificationDocument
{
    public int SchemaVersion { get; set; }
    public string ArtifactKind { get; set; } = string.Empty;
    public string Verdict { get; set; } = string.Empty;
    public SloQualificationProfile Profile { get; set; } = new();
    public SloQualificationCandidate Candidate { get; set; } = new();
    public SloQualificationProvenance Provenance { get; set; } = new();
    public SloQualificationRules Rules { get; set; } = new();
    public List<SloQualificationSignal> Signals { get; set; } = new();
    public List<SloQualificationScenario> Scenarios { get; set; } = new();
    public List<SloQualificationFinding> Findings { get; set; } = new();
    [YamlIgnore]
    public string SourceFile { get; set; } = string.Empty;
}

public sealed class SloQualificationProfile
{
    public string Id { get; set; } = string.Empty;
    public int Version { get; set; }
    public List<SloQualificationProfileService> Services { get; set; } = new();
}

public sealed class SloQualificationProfileService
{
    public string Service { get; set; } = string.Empty;
    public List<string> Operations { get; set; } = new();
}

public sealed class SloQualificationCandidate
{
    public string GitSha { get; set; } = string.Empty;
    public string ArtifactDigest { get; set; } = string.Empty;
    public string ConfigDigest { get; set; } = string.Empty;
}

public sealed class SloQualificationProvenance
{
    public string RunId { get; set; } = string.Empty;
    public string RunUrl { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public DateTimeOffset WindowStartUtc { get; set; }
    public DateTimeOffset WindowEndUtc { get; set; }
    public string Region { get; set; } = string.Empty;
    public string BackendDescription { get; set; } = string.Empty;
}

public sealed class SloQualificationRules
{
    public int MaxArtifactAgeHours { get; set; }
    public int MinSamplesPerScenario { get; set; }
    public double MinDurationSeconds { get; set; }
    public double MaxFailureRate { get; set; }
    public bool ZeroCompletionsDisqualify { get; set; }
    public bool OnlySkippedRealAzureDisqualifies { get; set; }
}

public sealed class SloQualificationSignal
{
    public string Id { get; set; } = string.Empty;
    public string ScenarioId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Disposition { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
    public double? MeasuredValue { get; set; }
    public long Samples { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
}

public sealed class SloQualificationFinding
{
    public string Code { get; set; } = string.Empty;
    public string Disposition { get; set; } = string.Empty;
    public string? ScenarioId { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class SloQualificationScenario
{
    public string Id { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string EvidenceSource { get; set; } = string.Empty;
    public long Completions { get; set; }
    public long Failures { get; set; }
    public long Skipped { get; set; }
    public double DurationSeconds { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
}

public static class SloQualificationLoader
{
    public static SloQualificationDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("SLO qualification artifact not found", path);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        using var reader = new StreamReader(path);
        var document = deserializer.Deserialize<SloQualificationDocument>(reader)
            ?? throw new InvalidDataException($"{path}: empty document");
        document.SourceFile = path;
        SloQualificationValidator.Normalize(document);
        return document;
    }
}

public static class SloQualificationValidator
{
    public const int CurrentSchemaVersion = 1;

    public static IReadOnlyList<string> Validate(
        SloQualificationDocument document,
        DateTimeOffset nowUtc)
    {
        Normalize(document);
        var errors = new List<string>();
        var source = string.IsNullOrWhiteSpace(document.SourceFile)
            ? "SLO qualification artifact"
            : document.SourceFile;
        void Err(string message) => errors.Add($"{source}: {message}");

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            Err($"unsupported schema_version '{document.SchemaVersion}'; expected {CurrentSchemaVersion}");
        }
        if (!SloQualificationValues.ArtifactKinds.Contains(document.ArtifactKind))
        {
            Err(
                $"invalid artifact_kind '{document.ArtifactKind}'; allowed: " +
                string.Join(", ", SloQualificationValues.ArtifactKinds.OrderBy(value => value, StringComparer.Ordinal)));
        }
        ValidateVerdict(document, Err);
        ValidateProfile(document.Profile, Err);
        ValidateCandidate(document.Candidate, Err);
        ValidateProvenance(document, nowUtc.ToUniversalTime(), Err);
        ValidateRules(document.Rules, Err);
        ValidateSignals(document, nowUtc.ToUniversalTime(), Err);
        ValidateScenarios(document, nowUtc.ToUniversalTime(), Err);
        ValidateFindings(document, Err);
        return errors;
    }

    public static void Normalize(SloQualificationDocument document)
    {
        document.Profile ??= new SloQualificationProfile();
        document.Candidate ??= new SloQualificationCandidate();
        document.Provenance ??= new SloQualificationProvenance();
        document.Rules ??= new SloQualificationRules();
        document.Signals ??= new List<SloQualificationSignal>();
        document.Scenarios ??= new List<SloQualificationScenario>();
        document.Findings ??= new List<SloQualificationFinding>();
        document.Profile.Services ??= new List<SloQualificationProfileService>();

        for (var index = 0; index < document.Profile.Services.Count; index++)
        {
            document.Profile.Services[index] ??= new SloQualificationProfileService();
            document.Profile.Services[index].Operations ??= new List<string>();
        }
        for (var index = 0; index < document.Signals.Count; index++)
        {
            document.Signals[index] ??= new SloQualificationSignal();
        }
        for (var index = 0; index < document.Scenarios.Count; index++)
        {
            document.Scenarios[index] ??= new SloQualificationScenario();
        }
        for (var index = 0; index < document.Findings.Count; index++)
        {
            document.Findings[index] ??= new SloQualificationFinding();
        }
    }

    private static void ValidateVerdict(
        SloQualificationDocument document,
        Action<string> err)
    {
        var allowed = document.ArtifactKind switch
        {
            "emulator_regression" => SloQualificationValues.EmulatorVerdicts,
            "real_azure_workload_qualification" => SloQualificationValues.RealAzureVerdicts,
            "ab_experiment" => SloQualificationValues.AbVerdicts,
            _ => SloQualificationValues.NoVerdicts
        };
        if (!allowed.Contains(document.Verdict))
        {
            err(
                $"invalid verdict '{document.Verdict}' for artifact_kind '{document.ArtifactKind}'; " +
                $"allowed: {string.Join(", ", allowed.OrderBy(value => value, StringComparer.Ordinal))}");
        }
    }

    private static void ValidateProfile(
        SloQualificationProfile profile,
        Action<string> err)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            err("profile.id missing");
        }
        if (profile.Version <= 0)
        {
            err("profile.version must be greater than zero");
        }
        if (profile.Services.Count == 0)
        {
            err("profile.services must contain at least one service");
            return;
        }

        var seenServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < profile.Services.Count; index++)
        {
            var service = profile.Services[index];
            var prefix = $"profile.services[{index}]";
            if (string.IsNullOrWhiteSpace(service.Service))
            {
                err($"{prefix}.service missing");
            }
            else if (!seenServices.Add(service.Service))
            {
                err($"{prefix} duplicates service '{service.Service}'");
            }
            if (service.Operations.Count == 0)
            {
                err($"{prefix}.operations must contain at least one operation");
            }
            else if (service.Operations.Any(string.IsNullOrWhiteSpace))
            {
                err($"{prefix}.operations contains an empty operation");
            }
            else if (service.Operations
                     .GroupBy(operation => operation, StringComparer.OrdinalIgnoreCase)
                     .Any(group => group.Count() > 1))
            {
                err($"{prefix}.operations contains a duplicate operation");
            }
        }
    }

    private static void ValidateCandidate(
        SloQualificationCandidate candidate,
        Action<string> err)
    {
        if (string.IsNullOrWhiteSpace(candidate.GitSha))
        {
            err("candidate.git_sha missing");
        }
        if (string.IsNullOrWhiteSpace(candidate.ArtifactDigest))
        {
            err("candidate.artifact_digest missing");
        }
        if (string.IsNullOrWhiteSpace(candidate.ConfigDigest))
        {
            err("candidate.config_digest missing");
        }
    }

    private static void ValidateProvenance(
        SloQualificationDocument document,
        DateTimeOffset nowUtc,
        Action<string> err)
    {
        var provenance = document.Provenance;
        if (string.IsNullOrWhiteSpace(provenance.RunId))
        {
            err("provenance.run_id missing");
        }
        if (!Uri.TryCreate(provenance.RunUrl, UriKind.Absolute, out var runUri)
            || (runUri.Scheme != Uri.UriSchemeHttps && runUri.Scheme != Uri.UriSchemeHttp))
        {
            err("provenance.run_url must be an absolute HTTP(S) URL");
        }
        if (provenance.GeneratedAtUtc == default)
        {
            err("provenance.generated_at_utc missing");
        }
        else if (provenance.GeneratedAtUtc > nowUtc.AddMinutes(5))
        {
            err("provenance.generated_at_utc must not be in the future");
        }
        if (provenance.WindowStartUtc == default)
        {
            err("provenance.window_start_utc missing");
        }
        if (provenance.WindowEndUtc == default)
        {
            err("provenance.window_end_utc missing");
        }
        else if (provenance.WindowStartUtc >= provenance.WindowEndUtc)
        {
            err("provenance.window_start_utc must be earlier than window_end_utc");
        }
        if (provenance.GeneratedAtUtc != default
            && provenance.WindowEndUtc != default
            && provenance.GeneratedAtUtc < provenance.WindowEndUtc)
        {
            err("provenance.generated_at_utc must not be earlier than window_end_utc");
        }
        if (document.ArtifactKind == "real_azure_workload_qualification")
        {
            if (string.IsNullOrWhiteSpace(provenance.Region))
            {
                err("provenance.region missing for real-Azure qualification");
            }
            if (string.IsNullOrWhiteSpace(provenance.BackendDescription))
            {
                err("provenance.backend_description missing for real-Azure qualification");
            }
        }
    }

    private static void ValidateRules(
        SloQualificationRules rules,
        Action<string> err)
    {
        if (rules.MaxArtifactAgeHours <= 0
            || rules.MaxArtifactAgeHours > TimeSpan.MaxValue.TotalHours)
        {
            err(
                "rules.max_artifact_age_hours must be greater than zero and " +
                "within the TimeSpan range");
        }
        if (rules.MinSamplesPerScenario <= 0)
        {
            err("rules.min_samples_per_scenario must be greater than zero");
        }
        if (!double.IsFinite(rules.MinDurationSeconds) || rules.MinDurationSeconds <= 0)
        {
            err("rules.min_duration_seconds must be a finite number greater than zero");
        }
        if (!double.IsFinite(rules.MaxFailureRate)
            || rules.MaxFailureRate is < 0 or > 1)
        {
            err("rules.max_failure_rate must be a finite number between zero and one");
        }
    }

    private static void ValidateSignals(
        SloQualificationDocument document,
        DateTimeOffset nowUtc,
        Action<string> err)
    {
        if (document.Signals.Count == 0)
        {
            err("signals must contain at least one signal");
            return;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scenarioIds = new HashSet<string>(
            document.Scenarios.Select(scenario => scenario.Id),
            StringComparer.OrdinalIgnoreCase);
        var scenariosById = document.Scenarios
            .Where(scenario => !string.IsNullOrWhiteSpace(scenario.Id))
            .GroupBy(scenario => scenario.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < document.Signals.Count; index++)
        {
            var signal = document.Signals[index];
            var prefix = $"signals[{index}]";
            if (string.IsNullOrWhiteSpace(signal.Id))
            {
                err($"{prefix}.id missing");
            }
            else if (!seenIds.Add(signal.Id))
            {
                err($"{prefix} duplicates signal id '{signal.Id}'");
            }
            if (string.IsNullOrWhiteSpace(signal.ScenarioId))
            {
                err($"{prefix}.scenario_id missing");
            }
            else if (!scenarioIds.Contains(signal.ScenarioId))
            {
                err($"{prefix} references unknown scenario_id '{signal.ScenarioId}'");
            }
            if (!SloQualificationValues.SignalSources.Contains(signal.Source))
            {
                err($"{prefix}.source invalid: '{signal.Source}'");
            }
            if (!SloQualificationValues.Dispositions.Contains(signal.Disposition))
            {
                err($"{prefix}.disposition invalid: '{signal.Disposition}'");
            }
            if (!SloQualificationValues.Metrics.Contains(signal.Metric))
            {
                err($"{prefix}.metric invalid: '{signal.Metric}'");
            }
            if (signal.MinValue is not null && signal.MaxValue is not null)
            {
                err($"{prefix} must not declare both min_value and max_value");
            }
            if (signal.MinValue is not null && !double.IsFinite(signal.MinValue.Value))
            {
                err($"{prefix}.min_value must be a finite number");
            }
            if (signal.MaxValue is not null && !double.IsFinite(signal.MaxValue.Value))
            {
                err($"{prefix}.max_value must be a finite number");
            }
            if (signal.Disposition == "blocking"
                && signal.MinValue is null
                && signal.MaxValue is null)
            {
                err($"{prefix} blocking signal requires min_value or max_value");
            }
            if (signal.Disposition == "report_only"
                && (signal.MinValue is not null || signal.MaxValue is not null))
            {
                err($"{prefix} report_only signal must not declare a threshold");
            }
            if (signal.MeasuredValue is null
                || double.IsNaN(signal.MeasuredValue.Value)
                || double.IsInfinity(signal.MeasuredValue.Value))
            {
                err($"{prefix}.measured_value must be a finite number");
            }
            if (signal.Samples < 0)
            {
                err($"{prefix}.samples must not be negative");
            }
            if (signal.CapturedAtUtc == default)
            {
                err($"{prefix}.captured_at_utc missing");
            }
            else if (document.Provenance.WindowStartUtc != default
                     && document.Provenance.WindowEndUtc != default
                     && (signal.CapturedAtUtc < document.Provenance.WindowStartUtc
                         || signal.CapturedAtUtc > document.Provenance.WindowEndUtc))
            {
                err($"{prefix}.captured_at_utc must fall within the provenance window");
            }
            if (document.ArtifactKind == "emulator_regression"
                && signal.Source != "proxy_overhead")
            {
                err($"{prefix} emulator_regression may only use source 'proxy_overhead'");
            }
            if (document.ArtifactKind == "ab_experiment"
                && signal.Disposition != "report_only")
            {
                err($"{prefix} ab_experiment signals must be report_only");
            }
            if (document.ArtifactKind == "real_azure_workload_qualification"
                && signal.Source == "backend_capacity"
                && scenariosById.TryGetValue(signal.ScenarioId, out var capacityScenario)
                && capacityScenario.EvidenceSource != "real_azure")
            {
                err($"{prefix} backend_capacity signal must reference a real_azure scenario");
            }
        }

        if (document.ArtifactKind == "real_azure_workload_qualification"
            && !document.Signals.Any(signal => signal.Disposition == "blocking"))
        {
            err("real-Azure qualification requires at least one blocking signal");
        }
        if (document.ArtifactKind == "real_azure_workload_qualification"
            && document.Verdict == "qualified"
            && !document.Signals.Any(
                signal => signal.Disposition == "blocking"
                          && scenariosById.TryGetValue(signal.ScenarioId, out var scenario)
                          && scenario.EvidenceSource == "real_azure"))
        {
            err("qualified real-Azure artifact requires a blocking signal for a real_azure scenario");
        }

        var gatesMustPass = (document.ArtifactKind == "real_azure_workload_qualification"
                             && document.Verdict == "qualified")
                            || (document.ArtifactKind == "emulator_regression"
                                && document.Verdict != "failed");
        if (!gatesMustPass)
        {
            return;
        }

        foreach (var signal in document.Signals.Where(signal => signal.Disposition == "blocking"))
        {
            if (signal.MeasuredValue is null)
            {
                continue;
            }
            if (signal.MinValue is not null && signal.MeasuredValue < signal.MinValue)
            {
                err(
                    $"signal '{signal.Id}' measured {signal.MeasuredValue} below " +
                    $"minimum {signal.MinValue}");
            }
            if (signal.MaxValue is not null && signal.MeasuredValue > signal.MaxValue)
            {
                err(
                    $"signal '{signal.Id}' measured {signal.MeasuredValue} above " +
                    $"maximum {signal.MaxValue}");
            }
            if (document.ArtifactKind == "real_azure_workload_qualification"
                && signal.Samples < document.Rules.MinSamplesPerScenario)
            {
                err(
                    $"signal '{signal.Id}' has {signal.Samples} samples; " +
                    $"minimum is {document.Rules.MinSamplesPerScenario}");
            }
            if (document.ArtifactKind == "real_azure_workload_qualification"
                && HasValidMaxAge(document.Rules)
                && nowUtc - signal.CapturedAtUtc.ToUniversalTime()
                > TimeSpan.FromHours(document.Rules.MaxArtifactAgeHours))
            {
                err($"signal '{signal.Id}' measurement is stale");
            }
        }
    }

    private static void ValidateScenarios(
        SloQualificationDocument document,
        DateTimeOffset nowUtc,
        Action<string> err)
    {
        if (document.Scenarios.Count == 0)
        {
            err("scenarios must contain at least one scenario");
            return;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profileOperations = document.Profile.Services
            .Where(service => !string.IsNullOrWhiteSpace(service.Service))
            .GroupBy(service => service.Service, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new HashSet<string>(
                    group.SelectMany(service => service.Operations),
                    StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < document.Scenarios.Count; index++)
        {
            var scenario = document.Scenarios[index];
            var prefix = $"scenarios[{index}]";
            if (string.IsNullOrWhiteSpace(scenario.Id))
            {
                err($"{prefix}.id missing");
            }
            else if (!seenIds.Add(scenario.Id))
            {
                err($"{prefix} duplicates scenario id '{scenario.Id}'");
            }
            if (string.IsNullOrWhiteSpace(scenario.Service))
            {
                err($"{prefix}.service missing");
            }
            if (string.IsNullOrWhiteSpace(scenario.Operation))
            {
                err($"{prefix}.operation missing");
            }
            if (!string.IsNullOrWhiteSpace(scenario.Service)
                && !string.IsNullOrWhiteSpace(scenario.Operation)
                && (!profileOperations.TryGetValue(scenario.Service, out var operations)
                    || !operations.Contains(scenario.Operation)))
            {
                err(
                    $"{prefix} operation '{scenario.Service}/{scenario.Operation}' " +
                    "is not declared by the profile");
            }
            if (!SloQualificationValues.EvidenceSources.Contains(scenario.EvidenceSource))
            {
                err($"{prefix}.evidence_source invalid: '{scenario.EvidenceSource}'");
            }
            if (scenario.Completions < 0 || scenario.Failures < 0 || scenario.Skipped < 0)
            {
                err($"{prefix} counts must not be negative");
            }
            if (!double.IsFinite(scenario.DurationSeconds) || scenario.DurationSeconds < 0)
            {
                err($"{prefix}.duration_seconds must be a finite non-negative number");
            }
            if (scenario.CapturedAtUtc == default)
            {
                err($"{prefix}.captured_at_utc missing");
            }
            else if (document.Provenance.WindowStartUtc != default
                     && document.Provenance.WindowEndUtc != default
                     && (scenario.CapturedAtUtc < document.Provenance.WindowStartUtc
                         || scenario.CapturedAtUtc > document.Provenance.WindowEndUtc))
            {
                err($"{prefix}.captured_at_utc must fall within the provenance window");
            }
        }

        if (document.ArtifactKind == "real_azure_workload_qualification")
        {
            ValidateRealAzureQualification(document, nowUtc, err);
        }
    }

    private static void ValidateRealAzureQualification(
        SloQualificationDocument document,
        DateTimeOffset nowUtc,
        Action<string> err)
    {
        var realAzureScenarios = document.Scenarios
            .Where(scenario => scenario.EvidenceSource == "real_azure")
            .ToList();
        if (realAzureScenarios.Count == 0)
        {
            err("real-Azure qualification requires at least one real_azure scenario");
            return;
        }
        if (document.Verdict != "qualified")
        {
            return;
        }

        if (!document.Rules.ZeroCompletionsDisqualify)
        {
            err("qualified real-Azure artifact must set zero_completions_disqualify: true");
        }
        if (!document.Rules.OnlySkippedRealAzureDisqualifies)
        {
            err("qualified real-Azure artifact must set only_skipped_real_azure_disqualifies: true");
        }
        if (HasValidMaxAge(document.Rules)
            && nowUtc - document.Provenance.GeneratedAtUtc.ToUniversalTime()
            > TimeSpan.FromHours(document.Rules.MaxArtifactAgeHours))
        {
            err("qualified real-Azure artifact is stale");
        }

        foreach (var scenario in realAzureScenarios)
        {
            var prefix = $"scenario '{scenario.Id}'";
            if (scenario.Completions == 0)
            {
                err($"{prefix} has zero completions");
            }
            if (scenario.Completions < document.Rules.MinSamplesPerScenario)
            {
                err(
                    $"{prefix} has {scenario.Completions} completions; " +
                    $"minimum is {document.Rules.MinSamplesPerScenario}");
            }
            if (scenario.DurationSeconds < document.Rules.MinDurationSeconds)
            {
                err(
                    $"{prefix} duration is {scenario.DurationSeconds}; " +
                    $"minimum is {document.Rules.MinDurationSeconds}");
            }
            var attempts = (double)scenario.Completions + scenario.Failures;
            var failureRate = attempts == 0 ? 0 : scenario.Failures / attempts;
            if (failureRate > document.Rules.MaxFailureRate)
            {
                err(
                    $"{prefix} failure rate {failureRate:F6} exceeds " +
                    $"{document.Rules.MaxFailureRate:F6}");
            }
            if (HasValidMaxAge(document.Rules)
                && nowUtc
                - scenario.CapturedAtUtc.ToUniversalTime()
                > TimeSpan.FromHours(document.Rules.MaxArtifactAgeHours))
            {
                err($"{prefix} measurement is stale");
            }
        }
    }

    private static bool HasValidMaxAge(SloQualificationRules rules) =>
        rules.MaxArtifactAgeHours > 0
        && rules.MaxArtifactAgeHours <= TimeSpan.MaxValue.TotalHours;

    private static void ValidateFindings(
        SloQualificationDocument document,
        Action<string> err)
    {
        if (document.Verdict is "passed" or "qualified"
            && document.Findings.Any(finding => finding.Disposition == "blocking"))
        {
            err($"verdict '{document.Verdict}' must not contain blocking findings");
        }

        var scenarioIds = new HashSet<string>(
            document.Scenarios.Select(scenario => scenario.Id),
            StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < document.Findings.Count; index++)
        {
            var finding = document.Findings[index];
            var prefix = $"findings[{index}]";
            if (string.IsNullOrWhiteSpace(finding.Code))
            {
                err($"{prefix}.code missing");
            }
            if (!SloQualificationValues.Dispositions.Contains(finding.Disposition))
            {
                err($"{prefix}.disposition invalid: '{finding.Disposition}'");
            }
            if (!string.IsNullOrWhiteSpace(finding.ScenarioId)
                && !scenarioIds.Contains(finding.ScenarioId))
            {
                err($"{prefix} references unknown scenario_id '{finding.ScenarioId}'");
            }
            if (string.IsNullOrWhiteSpace(finding.Message))
            {
                err($"{prefix}.message missing");
            }
        }
    }
}

public static class SloQualificationValues
{
    public static readonly HashSet<string> ArtifactKinds = new(StringComparer.Ordinal)
    {
        "emulator_regression",
        "real_azure_workload_qualification",
        "ab_experiment"
    };

    public static readonly HashSet<string> EmulatorVerdicts = new(StringComparer.Ordinal)
    {
        "passed", "failed", "inconclusive"
    };

    public static readonly HashSet<string> RealAzureVerdicts = new(StringComparer.Ordinal)
    {
        "blocked", "candidate", "qualified", "inconclusive"
    };

    public static readonly HashSet<string> AbVerdicts = new(StringComparer.Ordinal)
    {
        "report_only"
    };

    public static readonly HashSet<string> NoVerdicts = new(StringComparer.Ordinal);

    public static readonly HashSet<string> SignalSources = new(StringComparer.Ordinal)
    {
        "proxy_overhead", "backend_capacity", "network_noise"
    };

    public static readonly HashSet<string> Dispositions = new(StringComparer.Ordinal)
    {
        "blocking", "advisory", "report_only"
    };

    public static readonly HashSet<string> Metrics = new(StringComparer.Ordinal)
    {
        "native_error_rate",
        "p95_ms",
        "p99_ms",
        "throughput_per_sec",
        "throughput_ratio",
        "p50_ratio",
        "p99_ratio",
        "throttle_rate",
        "peak_working_set_mb",
        "alloc_bytes_per_op"
    };

    public static readonly HashSet<string> EvidenceSources = new(StringComparer.Ordinal)
    {
        "emulator", "real_azure", "deterministic"
    };
}
