using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class EmulatorQualificationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-16T17:00:00Z");

    [Fact]
    public void Generate_passes_fresh_absolute_and_relative_proxy_regression_gates()
    {
        using var fixture = Fixture(
            Reference(),
            Latest(
                ProxyRow(completed: 1000, failures: 0, throughput: 100, p99: 50),
                BaselineRow(throughput: 120, p99: 40)));

        var document = fixture.Generate();

        Assert.Equal("passed", document.Verdict);
        Assert.All(document.Signals, signal => Assert.Equal("proxy_overhead", signal.Source));
        Assert.Contains(document.Signals, signal => signal.Metric == "throughput_ratio");
        Assert.Empty(document.Findings);
        Assert.Empty(SloQualificationValidator.Validate(document, Now));
    }

    [Fact]
    public void Generate_fails_zero_completions_and_blocking_threshold_breaches()
    {
        using var fixture = Fixture(
            Reference(),
            Latest(
                ProxyRow(completed: 0, failures: 0, throughput: 0, p99: 500),
                BaselineRow(throughput: 120, p99: 40)));

        var document = fixture.Generate();

        Assert.Equal("failed", document.Verdict);
        Assert.Contains(document.Findings, finding => finding.Code == "zero_completions");
        Assert.Contains(
            document.Signals,
            signal => signal.Metric == "p99_ms" && signal.MeasuredValue == 500);
    }

    [Fact]
    public void Generate_marks_missing_stale_and_untracked_rows_inconclusive()
    {
        const string latest = """
            {
              "scenarios": {
                "proxy.Put": {
                  "elapsedSeconds": 20,
                  "completed": 1000,
                  "failures": 0,
                  "throughputPerSec": 100,
                  "p50Ms": 10,
                  "p95Ms": 20,
                  "p99Ms": 50,
                  "memoryMeasured": false,
                  "peakWorkingSetMb": 0,
                  "allocBytesPerOp": 0,
                  "capturedAtUtc": "2026-07-16T10:00:00Z"
                },
                "untracked.List": {
                  "elapsedSeconds": 20,
                  "completed": 100,
                  "failures": 0,
                  "throughputPerSec": 10,
                  "p50Ms": 10,
                  "p95Ms": 20,
                  "p99Ms": 50,
                  "memoryMeasured": false,
                  "peakWorkingSetMb": 0,
                  "allocBytesPerOp": 0,
                  "capturedAtUtc": "2026-07-16T16:59:00Z"
                }
              }
            }
            """;
        using var fixture = Fixture(Reference(), latest);

        var document = fixture.Generate();

        Assert.Equal("inconclusive", document.Verdict);
        Assert.Contains(document.Findings, finding => finding.Code == "stale_scenario");
        Assert.Contains(document.Findings, finding => finding.Code == "missing_scenario");
        Assert.Contains(document.Findings, finding => finding.Code == "untracked_scenario");
    }

    [Fact]
    public void Generate_keeps_low_nonzero_failure_rate_advisory()
    {
        using var fixture = Fixture(
            Reference(),
            Latest(
                ProxyRow(completed: 990, failures: 10, throughput: 100, p99: 50),
                BaselineRow(throughput: 120, p99: 40)));

        var document = fixture.Generate();

        Assert.Equal("passed", document.Verdict);
        var finding = Assert.Single(document.Findings, finding => finding.Code == "nonzero_failures");
        Assert.Equal("advisory", finding.Disposition);
    }

    [Fact]
    public void Generate_marks_nonpositive_relative_baseline_inconclusive()
    {
        using var fixture = Fixture(
            Reference(),
            Latest(
                ProxyRow(completed: 1000, failures: 0, throughput: 100, p99: 50),
                BaselineRow(throughput: 0, p99: 0)));

        var document = fixture.Generate();

        Assert.Equal("inconclusive", document.Verdict);
        Assert.DoesNotContain(
            document.Signals,
            signal => signal.Metric is "throughput_ratio" or "p99_ratio");
        Assert.Equal(
            2,
            document.Findings.Count(finding => finding.Code == "relative_baseline_not_positive"));
        Assert.Empty(SloQualificationValidator.Validate(document, Now));
    }

    [Fact]
    public void Generate_rejects_null_latest_scenario()
    {
        using var fixture = Fixture(
            Reference(),
            """
            {
              "scenarios": {
                "proxy.Put": null
              }
            }
            """);

        var exception = Assert.Throws<InvalidDataException>(() => fixture.Generate());

        Assert.Contains("proxy.Put", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderYaml_round_trips_through_qualification_validator()
    {
        using var fixture = Fixture(
            Reference(),
            Latest(
                ProxyRow(completed: 1000, failures: 0, throughput: 100, p99: 50),
                BaselineRow(throughput: 120, p99: 40)));
        var output = Path.Combine(AppContext.BaseDirectory, $"emulator-qualification-{Guid.NewGuid():N}.yaml");

        try
        {
            EmulatorQualificationGenerator.RenderYaml(fixture.Generate(), output);
            var loaded = SloQualificationLoader.Load(output);

            Assert.Empty(SloQualificationValidator.Validate(loaded, Now));
            Assert.Equal("emulator_regression", loaded.ArtifactKind);
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static TempFixture Fixture(string reference, string latest) =>
        new(reference, latest);

    private static string Reference() =>
        """
        {
          "scenarios": {
            "proxy.Put": {
              "minThroughputPerSec": 50,
              "maxP99Ms": 100,
              "maxPeakWorkingSetMb": 0,
              "maxAllocBytesPerOp": 0
            },
            "azure-sdk.Put": {
              "minThroughputPerSec": 0,
              "maxP99Ms": 0,
              "maxPeakWorkingSetMb": 0,
              "maxAllocBytesPerOp": 0
            }
          },
          "pairings": {
            "proxy.Put": {
              "baseline": "azure-sdk.Put",
              "minThroughputRatio": 0.5,
              "maxP50Ratio": 0,
              "maxP99Ratio": 3
            }
          }
        }
        """;

    private static string Latest(string proxy, string baseline) =>
        $$"""
        {
          "scenarios": {
            "proxy.Put": {{proxy}},
            "azure-sdk.Put": {{baseline}}
          }
        }
        """;

    private static string ProxyRow(long completed, long failures, double throughput, double p99) =>
        Row(completed, failures, throughput, p99, "2026-07-16T16:59:00Z");

    private static string BaselineRow(double throughput, double p99) =>
        Row(1000, 0, throughput, p99, "2026-07-16T16:59:30Z");

    private static string Row(
        long completed,
        long failures,
        double throughput,
        double p99,
        string capturedAtUtc) =>
        $$"""
        {
          "elapsedSeconds": 20,
          "completed": {{completed}},
          "failures": {{failures}},
          "throughputPerSec": {{throughput}},
          "p50Ms": 10,
          "p95Ms": 20,
          "p99Ms": {{p99}},
          "memoryMeasured": false,
          "peakWorkingSetMb": 0,
          "allocBytesPerOp": 0,
          "capturedAtUtc": "{{capturedAtUtc}}"
        }
        """;

    private sealed class TempFixture : IDisposable
    {
        private readonly string _directory;

        public TempFixture(string reference, string latest)
        {
            _directory = Path.Combine(AppContext.BaseDirectory, $"emulator-fixture-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            ReferencePath = Path.Combine(_directory, "reference.json");
            LatestPath = Path.Combine(_directory, "latest.json");
            File.WriteAllText(ReferencePath, reference);
            File.WriteAllText(LatestPath, latest);
        }

        public string ReferencePath { get; }
        public string LatestPath { get; }

        public SloQualificationDocument Generate() =>
            EmulatorQualificationGenerator.Generate(
                ReferencePath,
                LatestPath,
                new EmulatorQualificationMetadata
                {
                    RunId = "123",
                    RunUrl = "https://github.com/example/repo/actions/runs/123",
                    GitSha = "abcdef",
                    ArtifactDigest = "sha256:artifact",
                    ConfigDigest = "sha256:config",
                    GeneratedAtUtc = Now,
                    MaxRowAgeHours = 2
                });

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
