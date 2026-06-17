using System.Text.Json;
using Xunit;

namespace Aws2Azure.PerfTests;

// AWS2AZURE_PERF_DIR is a process-wide env var; tests must run serially.
[CollectionDefinition(nameof(PerfReportSerialCollection), DisableParallelization = true)]
public sealed class PerfReportSerialCollection { }

[Collection(nameof(PerfReportSerialCollection))]
public sealed class PerfReportMergeTests
{
    [Fact]
    public void Merge_replaces_existing_row_for_same_scenario_in_place()
    {
        using var dir = new TempPerfDir();
        var mdPath = Path.Combine(dir.Path, "baseline-latest.md");

        PerfReport.Append(MakeResult("scenarioA", throughput: 100, p99Us: 1000), notes: "first");
        var afterFirst = File.ReadAllText(mdPath);
        Assert.Contains("scenarioA", afterFirst);
        Assert.Contains("        100.0", afterFirst);

        PerfReport.Append(MakeResult("scenarioB", throughput: 200, p99Us: 2000), notes: "second");
        var afterSecond = File.ReadAllText(mdPath);
        Assert.Contains("scenarioA", afterSecond);
        Assert.Contains("scenarioB", afterSecond);

        PerfReport.Append(MakeResult("scenarioA", throughput: 150, p99Us: 1500), notes: "first-rerun");
        var afterRefresh = File.ReadAllText(mdPath);
        Assert.Contains("scenarioB", afterRefresh);
        Assert.Contains("        150.0", afterRefresh);
        Assert.DoesNotContain("        100.0", afterRefresh);
        var aRows = afterRefresh.Split('\n').Count(l => l.Contains("| scenarioA "));
        Assert.Equal(1, aRows);
    }

    [Fact]
    public void Append_writes_csv_history_cumulatively()
    {
        using var dir = new TempPerfDir();

        PerfReport.Append(MakeResult("scenarioA", 100, 1000));
        PerfReport.Append(MakeResult("scenarioA", 110, 1100));
        PerfReport.Append(MakeResult("scenarioB", 200, 2000));

        var csv = File.ReadAllText(Path.Combine(dir.Path, "history.csv"));
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("timestamp_utc,scenario", lines[0]);
        Assert.Equal(2, lines.Skip(1).Count(l => l.Contains(",scenarioA,")));
        Assert.Single(lines.Skip(1).Where(l => l.Contains(",scenarioB,")));
    }

    [Fact]
    public void Append_writes_header_when_file_missing()
    {
        using var dir = new TempPerfDir();
        PerfReport.Append(MakeResult("only", 50, 500));
        var md = File.ReadAllText(Path.Combine(dir.Path, "baseline-latest.md"));
        Assert.Contains("# aws2azure", md);
        Assert.Contains("| Scenario", md);
        Assert.Contains("| only", md);
    }

    private static PerfResult MakeResult(string scenario, double throughput, long p99Us) =>
        new(
            Scenario: scenario,
            Concurrency: 1,
            ElapsedSeconds: 10.0,
            Completed: 1000,
            Failures: 0,
            ThroughputPerSec: throughput,
            P50Us: 500,
            P95Us: p99Us - 100,
            P99Us: p99Us,
            MaxUs: p99Us + 100,
            FirstFailure: null);

    private sealed class TempPerfDir : IDisposable
    {
        private readonly string? _previous;
        public string Path { get; }

        public TempPerfDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "perfreport-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            _previous = Environment.GetEnvironmentVariable("AWS2AZURE_PERF_DIR");
            Environment.SetEnvironmentVariable("AWS2AZURE_PERF_DIR", Path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("AWS2AZURE_PERF_DIR", _previous);
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}

[Collection(nameof(PerfReportSerialCollection))]
public sealed class PerfRegressionGateTests
{
    [Fact]
    public void Asserts_pass_when_scenario_absent_from_reference()
    {
        var result = MakeResult("not.in.reference.json", throughput: 0.01, p99Us: long.MaxValue);
        result.AssertNoRegression(); // no throw
    }

    [Fact]
    public void Throws_when_throughput_below_floor()
    {
        using var ovr = new ReferenceOverride(new Dictionary<string, (double, double)>
        {
            ["scenarioX"] = (100.0, 1000.0),
        });
        var result = MakeResult("scenarioX", throughput: 50.0, p99Us: 500_000);
        var ex = Assert.Throws<Xunit.Sdk.XunitException>(result.AssertNoRegression);
        Assert.Contains("throughput", ex.Message);
        Assert.Contains("scenarioX", ex.Message);
    }

    [Fact]
    public void Throws_when_p99_above_ceiling()
    {
        using var ovr = new ReferenceOverride(new Dictionary<string, (double, double)>
        {
            ["scenarioY"] = (10.0, 100.0),
        });
        var result = MakeResult("scenarioY", throughput: 200.0, p99Us: 500_000); // 500 ms > 100 ms
        var ex = Assert.Throws<Xunit.Sdk.XunitException>(result.AssertNoRegression);
        Assert.Contains("p99", ex.Message);
    }

    [Fact]
    public void Zero_threshold_opts_out_of_that_half()
    {
        using var ovr = new ReferenceOverride(new Dictionary<string, (double, double)>
        {
            ["scenarioZ"] = (0.0, 100.0), // floor disabled
        });
        var result = MakeResult("scenarioZ", throughput: 0.01, p99Us: 50_000); // tiny throughput, fast p99
        result.AssertNoRegression(); // no throw
    }

    private static PerfResult MakeResult(string scenario, double throughput, long p99Us) =>
        new(scenario, 1, 10.0, 100, 0, throughput, 100, 200, p99Us, p99Us + 100);

    [Fact]
    public void Throws_when_peak_working_set_above_ceiling()
    {
        using var ovr = new ReferenceOverride(new Dictionary<string, PerfBaselineEntry>
        {
            ["memScenario"] = new() { MaxPeakWorkingSetMb = 100.0 },
        });
        var result = MakeResult("memScenario", throughput: 200.0, p99Us: 1000) with
        {
            MemoryMeasured = true,
            PeakWorkingSetBytes = 150L * 1024 * 1024, // 150 MB > 100 MB
        };
        var ex = Assert.Throws<Xunit.Sdk.XunitException>(result.AssertNoRegression);
        Assert.Contains("working set", ex.Message);
    }

    [Fact]
    public void Throws_when_alloc_per_op_above_ceiling()
    {
        using var ovr = new ReferenceOverride(new Dictionary<string, PerfBaselineEntry>
        {
            ["allocScenario"] = new() { MaxAllocBytesPerOp = 1000.0 },
        });
        var result = MakeResult("allocScenario", throughput: 200.0, p99Us: 1000) with
        {
            MemoryMeasured = true,
            Completed = 100,
            AllocatedBytesDelta = 500_000, // 5000 B/op > 1000 B/op
        };
        var ex = Assert.Throws<Xunit.Sdk.XunitException>(result.AssertNoRegression);
        Assert.Contains("alloc", ex.Message);
    }

    [Fact]
    public void Memory_ceiling_skipped_when_memory_not_measured()
    {
        using var ovr = new ReferenceOverride(new Dictionary<string, PerfBaselineEntry>
        {
            ["unmeasured"] = new() { MaxPeakWorkingSetMb = 1.0, MaxAllocBytesPerOp = 1.0 },
        });
        var result = MakeResult("unmeasured", throughput: 200.0, p99Us: 1000) with
        {
            MemoryMeasured = false,
            PeakWorkingSetBytes = long.MaxValue,
            AllocatedBytesDelta = long.MaxValue,
        };
        result.AssertNoRegression(); // no throw — memory dimension opted out
    }

    [Fact]
    public void Resource_only_skips_throughput_and_p99_dimensions()
    {
        // Tier 2 (#420): against real Azure the absolute throughput/p99 floors
        // are network-bound and must not gate — only resource ceilings do.
        using var ovr = new ReferenceOverride(new Dictionary<string, (double, double)>
        {
            ["realScenario"] = (100.0, 100.0), // floor 100 ops/s, ceiling 100 ms
        });
        using var _ = new EnvOverride("AWS2AZURE_PERF_RESOURCE_ONLY", "1");
        // throughput 1 ops/s (< floor) and p99 500 ms (> ceiling) — both would
        // throw without resource-only mode.
        var result = MakeResult("realScenario", throughput: 1.0, p99Us: 500_000);
        result.AssertNoRegression(); // no throw
    }

    [Fact]
    public void Resource_only_still_enforces_alloc_ceiling()
    {
        using var ovr = new ReferenceOverride(new Dictionary<string, PerfBaselineEntry>
        {
            ["realAllocScenario"] = new() { MinThroughputPerSec = 1_000_000, MaxAllocBytesPerOp = 1000.0 },
        });
        using var _ = new EnvOverride("AWS2AZURE_PERF_RESOURCE_ONLY", "1");
        var result = MakeResult("realAllocScenario", throughput: 1.0, p99Us: 500_000) with
        {
            MemoryMeasured = true,
            Completed = 100,
            AllocatedBytesDelta = 500_000, // 5000 B/op > 1000 B/op — resource gate fires
        };
        var ex = Assert.Throws<Xunit.Sdk.XunitException>(result.AssertNoRegression);
        Assert.Contains("alloc", ex.Message);
        Assert.DoesNotContain("throughput", ex.Message); // network dim suppressed
    }

    [Fact]
    public void Reference_falls_back_to_committed_baseline_when_override_dir_has_none()
    {
        // Tier 2 (#420): the real-Azure perf workflow points AWS2AZURE_PERF_DIR
        // at a RESULTS-only temp dir (to keep real-Azure numbers out of the
        // committed emulator baseline). The committed thresholds/pairings must
        // still load from docs/perf/baseline-reference.json — otherwise the
        // scenario resource ceilings AND the relative proxy-vs-SDK gate silently
        // no-op against an empty reference. An override dir without a reference
        // must fall back to the repo's committed reference.
        var emptyDir = Path.Combine(Path.GetTempPath(), "perfref-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            using var _ = new EnvOverride("AWS2AZURE_PERF_DIR", emptyDir);
            PerfReferenceBaseline.ResetForTests();
            Assert.NotEmpty(PerfReferenceBaseline.Pairings);
        }
        finally
        {
            PerfReferenceBaseline.ResetForTests();
            try { Directory.Delete(emptyDir, recursive: true); } catch { }
        }
    }

    private sealed class EnvOverride : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvOverride(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    /// <summary>
    /// Writes a temporary baseline-reference.json under a probed perf dir and
    /// resets the cached singleton so the new file is picked up. Restores on
    /// dispose. Touches a static field via reflection — only used in tests.
    /// </summary>
    private sealed class ReferenceOverride : IDisposable
    {
        private readonly string _tempDir;
        private readonly string? _previousEnv;

        public ReferenceOverride(IReadOnlyDictionary<string, (double Floor, double Ceiling)> entries)
            : this(ToEntries(entries))
        {
        }

        public ReferenceOverride(IReadOnlyDictionary<string, PerfBaselineEntry> entries)
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "perfref-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _previousEnv = Environment.GetEnvironmentVariable("AWS2AZURE_PERF_DIR");
            Environment.SetEnvironmentVariable("AWS2AZURE_PERF_DIR", _tempDir);

            var doc = new PerfBaselineDocument
            {
                Scenarios = new Dictionary<string, PerfBaselineEntry>(entries),
            };
            var json = JsonSerializer.Serialize(doc, PerfBaselineJsonContext.Default.PerfBaselineDocument);
            File.WriteAllText(Path.Combine(_tempDir, "baseline-reference.json"), json);

            PerfReferenceBaseline.ResetForTests();
        }

        private static Dictionary<string, PerfBaselineEntry> ToEntries(
            IReadOnlyDictionary<string, (double Floor, double Ceiling)> entries)
        {
            var dict = new Dictionary<string, PerfBaselineEntry>();
            foreach (var (k, v) in entries)
            {
                dict[k] = new PerfBaselineEntry { MinThroughputPerSec = v.Floor, MaxP99Ms = v.Ceiling };
            }
            return dict;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("AWS2AZURE_PERF_DIR", _previousEnv);
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
            PerfReferenceBaseline.ResetForTests();
        }
    }
}
