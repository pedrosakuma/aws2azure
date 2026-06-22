using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Tier 0 alloc/op regression gate (issue #459) — see <see cref="TranslationAllocGate"/>.
/// Runs in the fast <c>ci.yml</c> unit-test leg on every PR (no backend, no
/// emulator, sub-second), failing when any backendless DDB→Cosmos translation
/// path allocates more than its committed ceiling in
/// <c>docs/perf/microbench-reference.json</c>.
/// </summary>
public sealed class TranslationAllocGateTests
{
    private readonly ITestOutputHelper _output;

    public TranslationAllocGateTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Translation_paths_stay_within_committed_alloc_ceilings()
    {
        var inv = CultureInfo.InvariantCulture;
        var scenarios = TranslationAllocGate.BuildScenarios();
        var failures = new List<string>();

        foreach (var scenario in scenarios)
        {
            double bytesPerOp = TranslationAllocGate.MeasureMinBytesPerOp(scenario.Run);
            var entry = MicrobenchAllocReference.TryGet(scenario.Name);
            double ceiling = entry?.MaxAllocBytesPerOp ?? 0.0;

            _output.WriteLine(string.Format(
                inv,
                "{0,-48} {1,8:F0} B/op   ceiling {2,8:F0}",
                scenario.Name,
                bytesPerOp,
                ceiling));

            // 0 (or absent) opts out of the absolute ceiling — the coverage
            // drift guard below still requires an entry to exist.
            if (ceiling > 0 && bytesPerOp > ceiling)
            {
                failures.Add(string.Format(
                    inv,
                    "{0}: {1:F0} B/op > ceiling {2:F0} B/op",
                    scenario.Name,
                    bytesPerOp,
                    ceiling));
            }
        }

        Assert.True(
            failures.Count == 0,
            "Translation alloc/op regression — a backendless hot path allocates more than its " +
            "committed floor in docs/perf/microbench-reference.json. Investigate the regression; " +
            "only bump the ceiling deliberately when a change is expected to raise allocation " +
            "(see docs/perf/README.md):\n  - " + string.Join("\n  - ", failures));
    }

    [Fact]
    public void Every_gate_scenario_has_a_reference_entry()
    {
        var reference = MicrobenchAllocReference.LoadAll();
        var missing = TranslationAllocGate.BuildScenarios()
            .Select(s => s.Name)
            .Where(name => !reference.ContainsKey(name))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Scenarios missing from docs/perf/microbench-reference.json (add an entry; use " +
            "maxAllocBytesPerOp 0 to opt out of gating):\n  - " + string.Join("\n  - ", missing));
    }

    [Fact]
    public void Every_reference_entry_matches_a_gate_scenario()
    {
        var known = new HashSet<string>(TranslationAllocGate.BuildScenarios().Select(s => s.Name));
        var stale = MicrobenchAllocReference.LoadAll().Keys
            .Where(name => !known.Contains(name))
            .ToArray();

        Assert.True(
            stale.Length == 0,
            "microbench-reference.json contains entries for scenarios no longer produced by " +
            "TranslationAllocGate.BuildScenarios():\n  - " + string.Join("\n  - ", stale));
    }
}
