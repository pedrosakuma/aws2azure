using System.Text.Json;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

[Trait("Category", "RcObservationOffline")]
public sealed class DynamoDbCrossoverTests
{
    private static DynamoDbCrossoverReport Report() => new()
    {
        CandidateDigest = "candidate-bytes",
        PriorDigest = "prior-bytes",
        Binding = new("backend-hash", "config-hash", "aws-hash"),
    };

    [Fact]
    public async Task Runs_exact_ABBA_then_AA_with_restart_warmup_and_probes_outside_each_measurement()
    {
        var report = Report();
        var events = new List<string>();
        var publications = new List<string>();
        await DynamoDbCrossover.RunAsync(report,
            (role, _) => { events.Add("restart:" + role); return Task.CompletedTask; },
            (index, role, warmup, duration, _) =>
            {
                events.Add(warmup ? "warmup" : "measurement");
                Assert.Equal(warmup ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5), duration);
                return Task.FromResult(new OperationTimingReport { WorkersCompleted = true });
            },
            _ => { events.Add("probe"); return Task.FromResult(new double[] { 10, 20 }); },
            () => report.Binding,
            value =>
            {
                publications.Add(JsonSerializer.Serialize(value,
                    DynamoDbCrossoverJsonContext.Default.DynamoDbCrossoverReport));
                return Task.CompletedTask;
            }, CancellationToken.None);

        Assert.Equal(
            new[] { "Prior", "Candidate", "Candidate", "Prior", "Prior", "Prior" }
                .SelectMany(role => new[] { "restart:" + role, "probe", "warmup", "measurement", "probe" }),
            events);
        Assert.Equal(7, publications.Count);
        Assert.False(JsonSerializer.Deserialize(publications[0],
            DynamoDbCrossoverJsonContext.Default.DynamoDbCrossoverReport)!.Completed);
        var final = JsonSerializer.Deserialize(publications[^1],
            DynamoDbCrossoverJsonContext.Default.DynamoDbCrossoverReport)!;
        Assert.True(final.Completed);
        Assert.False(final.Promotable);
        Assert.Equal(8, final.Concurrency);
        Assert.Equal(6, final.Slots.Count);
        Assert.All(final.Slots, slot =>
        {
            Assert.True(slot.Completed);
            Assert.Equal(slot.Index < 4 ? "ABBA" : "AA", slot.Comparison);
            Assert.Equal(slot.Role == "candidate" ? "candidate-bytes" : "prior-bytes", slot.RuntimeDigest);
            Assert.NotNull(slot.Warmup);
            Assert.NotNull(slot.Measurement);
        });
        Assert.False(final.Slots[^1].Measurement!.Promotable);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("probe")]
    [InlineData("warmup")]
    [InlineData("measurement")]
    [InlineData("binding")]
    [InlineData("cancel")]
    public async Task Failure_stops_subsequent_slots_and_preserves_partial_sanitized_diagnostics(string phase)
    {
        var report = Report();
        var original = new TimeoutException("secret-key https://private.example/payload");
        using var cancellation = new CancellationTokenSource();
        var measurements = 0;
        var restarts = 0;
        var published = "";
        if (phase == "cancel") cancellation.Cancel();
        var error = await Record.ExceptionAsync(() => DynamoDbCrossover.RunAsync(report,
            (_, _) =>
            {
                restarts++;
                if (phase == "restart") throw original;
                return Task.CompletedTask;
            },
            (_, _, warmup, _, _) =>
            {
                measurements++;
                if (phase == (warmup ? "warmup" : "measurement")) throw original;
                return Task.FromResult(new OperationTimingReport());
            },
            _ =>
            {
                if (phase == "probe") throw original;
                return Task.FromResult(Array.Empty<double>());
            },
            () => phase == "binding" ? report.Binding with { Config = "changed" } : report.Binding,
            value =>
            {
                published = JsonSerializer.Serialize(value,
                    DynamoDbCrossoverJsonContext.Default.DynamoDbCrossoverReport);
                return Task.CompletedTask;
            }, cancellation.Token));
        Assert.NotNull(error);
        if (phase is not ("binding" or "cancel")) Assert.Same(original, error);
        Assert.InRange(restarts, 0, 1);
        Assert.InRange(measurements, 0, 2);
        Assert.False(report.Completed);
        Assert.All(report.Slots, slot => Assert.False(slot.Completed));
        Assert.NotNull(report.Failure);
        Assert.DoesNotContain("secret-key", published, StringComparison.Ordinal);
        Assert.DoesNotContain("private.example", published, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(published));
    }

    [Fact]
    public async Task Binding_drift_after_warmup_prevents_measurement()
    {
        var report = Report();
        var drift = false;
        var calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => DynamoDbCrossover.RunAsync(report,
            (_, _) => Task.CompletedTask,
            (_, _, warmup, _, _) =>
            {
                Assert.True(warmup);
                calls++;
                drift = true;
                return Task.FromResult(new OperationTimingReport());
            },
            _ => Task.FromResult(Array.Empty<double>()),
            () => drift ? report.Binding with { Backend = "changed" } : report.Binding,
            _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.False(report.Completed);
    }

    [Fact]
    public async Task Publication_failure_preserves_original_worker_failure()
    {
        var report = Report();
        var worker = new TimeoutException("worker");
        var io = new IOException("publication");
        var error = await Assert.ThrowsAsync<AggregateException>(() => DynamoDbCrossover.RunAsync(report,
            (_, _) => throw worker,
            (_, _, _, _, _) => throw new InvalidOperationException("Must not measure"),
            _ => throw new InvalidOperationException("Must not probe"),
            () => report.Binding,
            _ => throw io, CancellationToken.None));
        Assert.Equal(new Exception[] { worker, io }, error.InnerExceptions);
    }

    [Fact]
    public async Task Identical_runtimes_are_not_misrepresented_as_AB()
    {
        var report = Report();
        report.PriorDigest = report.CandidateDigest;
        await Assert.ThrowsAsync<InvalidDataException>(() => DynamoDbCrossover.RunAsync(report,
            (_, _) => throw new InvalidOperationException("Must not start"),
            (_, _, _, _, _) => throw new InvalidOperationException("Must not measure"),
            _ => throw new InvalidOperationException("Must not probe"),
            () => report.Binding,
            _ => Task.CompletedTask, CancellationToken.None));
        Assert.Empty(report.Slots);
        Assert.False(report.Completed);
    }

    [Fact]
    public void Manual_workflow_keeps_diagnostics_separate_and_deallocates_owned_backend()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "aws2azure.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var workflow = File.ReadAllText(Path.Combine(root.FullName,
            ".github", "workflows", "dynamodb-controlled-crossover.yml"));
        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("schedule:", workflow, StringComparison.Ordinal);
        Assert.Contains("[ \"$REF\" != refs/heads/main ]", workflow, StringComparison.Ordinal);
        Assert.Contains("[ \"$REF_PROTECTED\" != true ]", workflow, StringComparison.Ordinal);
        Assert.Contains("[ \"$BUDGET_CONFIRMED\" != true ]", workflow, StringComparison.Ordinal);
        Assert.Contains("group: integration-real-azure", workflow, StringComparison.Ordinal);
        Assert.Contains("cancel-in-progress: false", workflow, StringComparison.Ordinal);
        Assert.Contains("purpose=aws2azure-nightly", workflow, StringComparison.Ordinal);
        Assert.Contains("deploy/realazure/dynamodb-load.bicep", workflow, StringComparison.Ordinal);
        Assert.Contains("Category=DynamoDbControlledCrossover", workflow, StringComparison.Ordinal);
        Assert.Contains("--rollback-target", workflow, StringComparison.Ordinal);
        Assert.Contains("--github-env \"$GITHUB_ENV\"", workflow, StringComparison.Ordinal);
        Assert.Contains("if: always() && steps.azure.outcome == 'success'", workflow, StringComparison.Ordinal);
        Assert.Contains("cleanup-real-azure-resource-groups.sh \"$RG_NAME\"", workflow, StringComparison.Ordinal);
        Assert.Contains("path: artifacts/dynamodb-controlled-crossover/*.json", workflow, StringComparison.Ordinal);
        Assert.Contains(".promotable == false and .completed == true", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("generate-rc-observation", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("AWS2AZURE_LOAD_EVIDENCE_PATH", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("release-promotion", workflow, StringComparison.Ordinal);
        Assert.True(workflow.IndexOf("Resolve and pin both exact sealed runtimes", StringComparison.Ordinal)
            < workflow.IndexOf("Azure login (OIDC)", StringComparison.Ordinal));
    }
}
