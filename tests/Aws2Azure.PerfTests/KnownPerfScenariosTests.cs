using System.Text.Json;
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
/// </summary>
public sealed class KnownPerfScenariosTests
{
    /// <summary>
    /// Canonical list of every <c>scenario:</c> argument passed to
    /// <see cref="PerfRunner.RunAsync"/> across the perf test project.
    /// Append here whenever a new scenario is introduced.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // S3
        "s3.PutObject (4 KiB)",
        "azure-sdk.Blob.UploadAsync (4 KiB)",

        // SQS
        "sqs.SendMessage (256 B)",
        "sqs.ReceiveMessage+Delete (1)",
        "azure-sdk.ServiceBus.SendMessage (256 B, queue)",
        "azure-sdk.ServiceBus.ReceiveMessage+Complete (1)",

        // SNS
        "sns.Publish (256 B)",
        "azure-sdk.ServiceBusTopics.SendMessage (256 B)",

        // DynamoDB
        "dynamodb.PutItem (small)",
        "azure-sdk.Cosmos.UpsertItem (small)",

        // Kinesis
        "kinesis.PutRecord (256 B)",
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
}
