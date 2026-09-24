using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed partial class RcObservationGenerationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Actual_merger_preserves_independent_windows_through_canonical_generation(
        bool candidateFirst, bool stableAfterRestoration, bool rollback)
    {
        var data = CreateData();
        if (rollback)
            data.Capture.Metrics[0].CandidateValue = 1;
        var root = Path.Combine(FindRepoRoot(), "artifacts", "rc-merge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var candidateEnd = data.Capture.Observation.MeasurementEndedAtUtc.AddTicks(4372054);
            var stableEnd = stableAfterRestoration ? candidateEnd.AddMinutes(3)
                : candidateFirst ? candidateEnd.AddTicks(1127219) : candidateEnd.AddTicks(-1127219);
            WriteSplitCapture(data, root, candidateEnd, stableEnd);
            var (exitCode, output) = await RunMerger(root);
            Assert.True(exitCode == 0, output);
            data.Capture = RcObservationCaptureLoader.Load(Path.Combine(root, "combined", "capture.json"));
            Assert.Equal(2, data.Capture.SchemaVersion);
            Assert.Equal(candidateEnd, data.Capture.Cohorts[0].MeasurementEndedAtUtc);
            Assert.Equal(stableEnd, data.Capture.Cohorts[1].MeasurementEndedAtUtc);
            Assert.Equal(candidateEnd.AddTicks(4), data.Capture.Restoration!.StartedAtUtc);
            var result = Generate(data);
            Assert.Equal(4, result.Evidence.SchemaVersion);
            Assert.Empty(RcObservationValidator.Validate(result.Evidence, result.Binding, Now));
            var rendered = Path.Combine(root, "evidence.yaml");
            RcObservationRenderer.Render(result.Evidence, rendered);
            var loaded = RcObservationLoader.Load(rendered);
            Assert.Equal(candidateEnd, loaded.Cohorts[0].MeasurementEndedAtUtc);
            Assert.Equal(stableEnd, loaded.Cohorts[1].MeasurementEndedAtUtc);
            Assert.Empty(RcObservationValidator.Validate(loaded, result.Binding, Now));
            var tampered = loaded with
            {
                Cohorts = loaded.Cohorts.Select(cohort => cohort.Role == "candidate"
                    ? cohort with { MeasurementEndedAtUtc = candidateEnd.AddTicks(1) } : cohort).ToArray(),
            };
            Assert.NotEqual(loaded.EvidenceDigest, RcObservationIntegrity.ComputePayloadDigest(tampered));
            Assert.NotEmpty(RcObservationValidator.Validate(tampered, result.Binding, Now));

            data.Capture.Restoration = data.Capture.Restoration with { StartedAtUtc = candidateEnd.AddTicks(-1) };
            Assert.Throws<InvalidDataException>(() => Generate(data));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Legacy_capture_still_requires_restoration_after_the_aggregate_measurement_end()
    {
        var data = CreateData();
        data.Capture.Restoration = data.Capture.Restoration! with
        {
            StartedAtUtc = data.Capture.Observation.MeasurementEndedAtUtc.AddTicks(-1),
        };
        Assert.Throws<InvalidDataException>(() => Generate(data));
    }

    [Theory]
    [InlineData("early-restore")]
    [InlineData("short-stable")]
    [InlineData("metric-time")]
    [InlineData("cohort-window")]
    public async Task Actual_merger_rejects_local_window_tampering(string fault)
    {
        var data = CreateData();
        var root = Path.Combine(FindRepoRoot(), "artifacts", "rc-merge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var end = data.Capture.Observation.MeasurementEndedAtUtc.AddTicks(4372054);
            WriteSplitCapture(data, root, end, end.AddTicks(1127219));
            var path = Path.Combine(root, fault == "short-stable" ? "stable" : "candidate", "cohort-capture.json");
            var capture = JsonNode.Parse(File.ReadAllText(path))!;
            switch (fault)
            {
                case "early-restore":
                    capture["restoration"]!["started_at_utc"] = Stamp(end.AddTicks(-1));
                    break;
                case "short-stable":
                    capture["observation"]!["measurement_ended_at_utc"] =
                        Stamp(data.Capture.Observation.StartedAtUtc.AddMinutes(60).AddTicks(-1));
                    break;
                case "metric-time":
                    capture["metrics"]![0]!["captured_at_utc"] = Stamp(end.AddTicks(1));
                    break;
                case "cohort-window":
                    capture["cohort"]!["observed_until_utc"] = Stamp(end.AddMinutes(-1));
                    break;
            }
            File.WriteAllText(path, capture.ToJsonString());
            var (exitCode, _) = await RunMerger(root);
            Assert.NotEqual(0, exitCode);
            Assert.False(File.Exists(Path.Combine(root, "combined", "capture.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing-end")]
    [InlineData("aggregate-end")]
    [InlineData("metric-end")]
    [InlineData("short-cohort")]
    [InlineData("downgrade")]
    public async Task Canonical_generator_rejects_ambiguous_or_tampered_split_captures(string fault)
    {
        var data = CreateData();
        var root = Path.Combine(FindRepoRoot(), "artifacts", "rc-merge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var end = data.Capture.Observation.MeasurementEndedAtUtc.AddTicks(4372054);
            WriteSplitCapture(data, root, end, end.AddTicks(1127219));
            var (exitCode, output) = await RunMerger(root);
            Assert.True(exitCode == 0, output);
            data.Capture = RcObservationCaptureLoader.Load(Path.Combine(root, "combined", "capture.json"));
            switch (fault)
            {
                case "missing-end":
                    data.Capture.Cohorts[0] = data.Capture.Cohorts[0] with { MeasurementEndedAtUtc = null };
                    break;
                case "aggregate-end":
                    data.Capture.Observation.MeasurementEndedAtUtc = end;
                    break;
                case "metric-end":
                    data.Capture.Metrics[0].CandidateCapturedAtUtc = end.AddTicks(1);
                    break;
                case "short-cohort":
                    data.Capture.Cohorts[1] = data.Capture.Cohorts[1] with
                    { MeasurementEndedAtUtc = data.Capture.Observation.StartedAtUtc.AddMinutes(60).AddTicks(-1) };
                    break;
                case "downgrade":
                    data.Capture.SchemaVersion = 1;
                    break;
            }
            Assert.Throws<InvalidDataException>(() => Generate(data));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static void WriteSplitCapture(
        TestData data, string root, DateTimeOffset candidateEnd, DateTimeOffset stableEnd)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        var source = JsonSerializer.SerializeToNode(data.Capture, options)!;
        var readyDigests = new Dictionary<string, string>();
        var start = data.Capture.Observation.StartedAtUtc;
        foreach (var (role, index, measured) in new[] { ("candidate", 0, candidateEnd), ("stable", 1, stableEnd) })
        {
            var directory = Path.Combine(root, role);
            Directory.CreateDirectory(Path.Combine(directory, "readiness"));
            var ended = role == "candidate" ? candidateEnd.AddMinutes(2).AddTicks(1) : measured.AddTicks(1);
            var cohort = source["cohorts"]![index]!.DeepClone();
            cohort["observed_until_utc"] = Stamp(ended);
            var capture = new JsonObject
            {
                ["schema_version"] = 1,
                ["profile"] = source["profile"]!.DeepClone(),
                ["azure"] = source["azure"]!.DeepClone(),
                ["load_shape"] = source["load_shape"]!.DeepClone(),
                ["observation"] = new JsonObject
                {
                    ["started_at_utc"] = Stamp(start), ["measurement_ended_at_utc"] = Stamp(measured),
                    ["ended_at_utc"] = Stamp(ended), ["requested_window_minutes"] = 60,
                },
                ["cohort"] = cohort,
                ["metrics"] = new JsonArray(data.Capture.Metrics.Select(metric => (JsonNode)new JsonObject
                {
                    ["id"] = metric.Id, ["unit"] = metric.Unit,
                    ["value"] = role == "candidate" ? metric.CandidateValue : metric.StableValue,
                    ["samples"] = role == "candidate" ? metric.CandidateSamples : metric.StableSamples,
                    ["captured_at_utc"] = Stamp(measured),
                }).ToArray()),
            };
            if (role == "candidate")
            {
                capture["restoration"] = source["restoration"]!.DeepClone();
                capture["restoration"]!["started_at_utc"] = Stamp(candidateEnd.AddTicks(4));
                capture["restoration"]!["verified_at_utc"] = Stamp(candidateEnd.AddMinutes(2));
            }
            File.WriteAllText(Path.Combine(directory, "cohort-capture.json"), capture.ToJsonString());
            var ready = new JsonObject
            {
                ["schema_version"] = 1, ["context_id"] = "offline", ["cohort"] = role,
                ["runtime_digest"] = cohort["runtime_digest"]!.DeepClone(),
                ["runtime_identity_digest"] = cohort["runtime_identity_digest"]!.DeepClone(),
            }.ToJsonString();
            var readyPath = Path.Combine(directory, "readiness", "ready.json");
            File.WriteAllText(readyPath, ready);
            readyDigests[role] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(readyPath)));
        }
        foreach (var role in new[] { "candidate", "stable" })
        {
            var readiness = Path.Combine(root, role, "readiness");
            File.WriteAllText(Path.Combine(readiness, "started.json"), new JsonObject
            {
                ["schema_version"] = 1, ["context_id"] = "offline", ["cohort"] = role,
                ["ready_content_digest"] = readyDigests[role],
                ["scheduled_at_utc"] = Stamp(start), ["actual_at_utc"] = Stamp(start),
            }.ToJsonString());
            File.WriteAllText(Path.Combine(readiness, "release-artifact.json"), new JsonObject
            {
                ["document"] = new JsonObject
                {
                    ["context_id"] = "offline", ["scheduled_at_utc"] = Stamp(start),
                    ["ready"] = new JsonObject
                    {
                        ["candidate"] = new JsonObject { ["content_digest"] = readyDigests["candidate"] },
                        ["stable"] = new JsonObject { ["content_digest"] = readyDigests["stable"] },
                    },
                },
            }.ToJsonString());
        }
    }

    private static async Task<(int ExitCode, string Output)> RunMerger(string root)
    {
        var info = new ProcessStartInfo("python3")
        {
            WorkingDirectory = FindRepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { ".github/scripts/merge-rc-observation-cohorts.py",
                     Path.Combine(root, "candidate"), Path.Combine(root, "stable"), Path.Combine(root, "combined") })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }
}
