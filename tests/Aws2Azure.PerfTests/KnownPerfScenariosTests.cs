using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Drift guard: every scenario passed to <c>PerfRunner.RunAsync</c> must have
/// an entry in <c>docs/perf/baseline-reference.json</c>. The list below is
/// hand-maintained — when a new perf scenario is added, the matching string
/// MUST be appended here AND a threshold MUST be added to the reference JSON
/// (use <c>{0, 0}</c> to opt out of gating during initial bring-up). Both
/// halves are required so that an absent reference entry surfaces here as a
/// failing unit test instead of silently passing the regression gate at
/// runtime.
///
/// <para><b>Exception:</b> the concurrency-sweep scenarios produced by
/// <see cref="PerfSweep"/> (<c>… (sweep c=N)</c> / <c>… (sweep knee)</c>, issue
/// #420 Tier 2) are <i>dynamically</i> named per ladder rung and intentionally
/// <b>not</b> gated — the knee is regime-dependent, so they carry no absolute
/// floor/ceiling. They are deliberately excluded from <see cref="All"/> and the
/// reference JSON; their A/B verdict comes from diffing the two real-Azure
/// passes' knee rows in the artifacts, not from this guard.</para>
/// </summary>
public sealed partial class KnownPerfScenariosTests
{
    /// <summary>
    /// Canonical list of every <c>scenario:</c> argument passed to
    /// <see cref="PerfRunner.RunAsync"/> across the perf test project.
    /// Append here whenever a new scenario is introduced.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Core (in-process microbenchmarks)
        "entra.GetToken (cache hit, 1 key, c=64)",
        "entra.GetToken (cache hit, 64 keys, c=64)",

        // S3
        "s3.PutObject (1 KiB)",
        "s3.PutObject (4 KiB)",
        "s3.PutObject (1 MiB)",
        "s3.PutObject (10 MiB)",
        "s3.GetObject (64 KiB)",
        "s3.ListObjectsV2 (500 keys)",
        "s3.CopyObject (4 KiB)",
        "s3.DeleteObject (idempotent)",
        "s3.DeleteObjects (100 keys)",
        "azure-sdk.Blob.UploadAsync (4 KiB)",

        // SQS
        "sqs.SendMessage (256 B)",
        "sqs.ReceiveMessage+Delete (1)",
        "sqs.ReceiveMessage+ChangeVisibility (0)",
        "sqs.ReceiveMessage+DeleteMessageBatch (10)",
        "azure-sdk.ServiceBus.SendMessage (256 B, queue)",
        "azure-sdk.ServiceBus.ReceiveMessage+Complete (1)",

        // SNS
        "sns.Publish (256 B)",
        "sns.Subscribe+Unsubscribe",
        "sns.ListSubscriptionsByTopic (20 subs)",
        "azure-sdk.ServiceBusTopics.SendMessage (256 B)",

        // DynamoDB
        "dynamodb.PutItem (small)",
        "dynamodb.PutItem (large)",
        "dynamodb.GetItem (small)",
        "dynamodb.GetItem (large)",
        "dynamodb.Query (pushable filter)",
        "dynamodb.Query (large items)",
        "dynamodb.Query LSI numeric (ordered)",
        "dynamodb.Query LSI numeric (selective)",
        "dynamodb.Scan (pushable filter)",
        "dynamodb.BatchWriteItem (25 items)",
        "dynamodb.BatchGetItem (25 items)",
        "dynamodb.BatchGetItem (large items)",
        "dynamodb.UpdateItem (SET expression)",
        "dynamodb.DeleteItem (idempotent)",
        "dynamodb.CosmosJsonParse (synthetic page)",
        "dynamodb.CosmosBinaryDecode (synthetic page)",
        "azure-sdk.Cosmos.UpsertItem (small)",
        "azure-sdk.Cosmos.ReadItem (small)",
        "azure-sdk.Cosmos.ReadManyItems (25 keys)",

        // Kinesis
        "kinesis.PutRecord (256 B)",
        "kinesis.PutRecords (25×256 B)",
        "azure-sdk.EventHubs.SendAsync (256 B, c=1)",
        "kinesis.GetRecords (256 B records)",
        "azure-sdk.EventHubs.ReceiveBatchAsync (256 B records)",
    };

    [Fact]
    public void Every_known_scenario_has_a_reference_entry()
    {
        var path = FindRepoPath("docs", "perf", "baseline-reference.json");
        Assert.True(File.Exists(path), $"baseline-reference.json not found at {path}");
        var doc = JsonSerializer.Deserialize(File.ReadAllText(path), PerfBaselineJsonContext.Default.PerfBaselineDocument);
        Assert.NotNull(doc?.Scenarios);

        var missing = All.Where(s => !doc!.Scenarios!.ContainsKey(s)).ToArray();
        Assert.True(missing.Length == 0,
            "Scenarios missing from docs/perf/baseline-reference.json:\n  - " +
            string.Join("\n  - ", missing));
    }

    [Fact]
    public void Every_literal_perf_runner_scenario_is_known()
    {
        var projectRoot = FindRepoPath("tests", "Aws2Azure.PerfTests");
        var discovered = Directory
            .EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => LiteralPerfRunnerScenarioRegex()
                .Matches(File.ReadAllText(path))
                .Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        var known = new HashSet<string>(All, StringComparer.Ordinal);
        var missing = discovered.Where(scenario => !known.Contains(scenario)).Order().ToArray();

        Assert.True(
            missing.Length == 0,
            "Literal PerfRunner scenarios missing from KnownPerfScenariosTests.All:\n  - " +
            string.Join("\n  - ", missing));
    }

    [Fact]
    public void Every_reference_entry_matches_a_known_scenario()
    {
        var path = FindRepoPath("docs", "perf", "baseline-reference.json");
        var doc = JsonSerializer.Deserialize(File.ReadAllText(path), PerfBaselineJsonContext.Default.PerfBaselineDocument);
        Assert.NotNull(doc?.Scenarios);
        var known = new HashSet<string>(All);
        var stale = doc!.Scenarios!.Keys.Where(k => !known.Contains(k)).ToArray();
        Assert.True(stale.Length == 0,
            "baseline-reference.json contains entries for scenarios no longer present in KnownPerfScenariosTests.All:\n  - " +
            string.Join("\n  - ", stale));
    }

    [Fact]
    public void Every_pairing_references_known_scenarios()
    {
        var path = FindRepoPath("docs", "perf", "baseline-reference.json");
        var doc = JsonSerializer.Deserialize(File.ReadAllText(path), PerfBaselineJsonContext.Default.PerfBaselineDocument);
        var pairings = doc?.Pairings;
        if (pairings is null || pairings.Count == 0) return; // pairings are optional

        var known = new HashSet<string>(All);
        var problems = new List<string>();
        foreach (var (proxy, pairing) in pairings)
        {
            if (!known.Contains(proxy))
                problems.Add($"pairing key '{proxy}' is not a known scenario");
            if (string.IsNullOrWhiteSpace(pairing.Baseline))
                problems.Add($"pairing '{proxy}' has no baseline");
            else if (!known.Contains(pairing.Baseline))
                problems.Add($"pairing '{proxy}' references unknown baseline '{pairing.Baseline}'");
            if (string.Equals(proxy, pairing.Baseline, StringComparison.Ordinal))
                problems.Add($"pairing '{proxy}' is paired with itself");
        }
        Assert.True(problems.Count == 0,
            "Invalid pairings in baseline-reference.json:\n  - " + string.Join("\n  - ", problems));
    }

    private static string FindRepoPath(params string[] segments)
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "docs")) &&
                Directory.Exists(Path.Combine(dir, "src")))
            {
                return Path.Combine(new[] { dir }.Concat(segments).ToArray());
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate repo root.");
    }

    [GeneratedRegex("PerfRunner\\.RunAsync\\(\\s*scenario:\\s*\"([^\"]+)\"")]
    private static partial Regex LiteralPerfRunnerScenarioRegex();
}
