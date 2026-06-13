using System.Text.Json;
using Xunit;

namespace Aws2Azure.FootprintTests;

/// <summary>
/// Drift guard mirroring <c>Aws2Azure.PerfTests.KnownPerfScenariosTests</c>:
/// every footprint scenario must have an entry in
/// <c>docs/perf/footprint-reference.json</c> and vice versa. This makes an
/// absent ceiling fail here (as a fast unit test) instead of silently passing
/// the budget gate at runtime. These two checks do NOT require
/// <c>AWS2AZURE_FOOTPRINT=1</c> — they only read files.
/// </summary>
public sealed class KnownFootprintScenariosTests
{
    [Fact]
    public void Every_known_scenario_has_a_reference_entry()
    {
        var doc = LoadReference();
        var missing = FootprintTests.AllScenarios
            .Where(s => !doc.ContainsKey(s))
            .ToArray();
        Assert.True(missing.Length == 0,
            "Scenarios missing from docs/perf/footprint-reference.json:\n  - " +
            string.Join("\n  - ", missing));
    }

    [Fact]
    public void Every_reference_entry_matches_a_known_scenario()
    {
        var doc = LoadReference();
        var known = new HashSet<string>(FootprintTests.AllScenarios);
        var stale = doc.Keys.Where(k => !known.Contains(k)).ToArray();
        Assert.True(stale.Length == 0,
            "footprint-reference.json contains entries for scenarios no longer present in FootprintTests.AllScenarios:\n  - " +
            string.Join("\n  - ", stale));
    }

    private static Dictionary<string, FootprintBaselineEntry> LoadReference()
    {
        var path = FootprintReferenceBaseline.ReferencePath();
        Assert.True(File.Exists(path), $"footprint-reference.json not found at {path}");
        var doc = JsonSerializer.Deserialize(File.ReadAllText(path),
            FootprintBaselineJsonContext.Default.FootprintBaselineDocument);
        Assert.NotNull(doc?.Scenarios);
        return doc!.Scenarios!;
    }
}
