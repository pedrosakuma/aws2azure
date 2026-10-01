using System.Text.Json;
using Aws2Azure.IntegrationTests.S3;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

[Trait("Category", "RcObservationOffline")]
public sealed class S3CrossoverTests
{
    private const string Candidate = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Prior = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static S3CrossoverReport Report() => new()
    {
        CandidateDigest = Candidate,
        PriorDigest = Prior,
        Binding = new("backend", "config", "binding"),
    };

    private static void Fill(S3CrossoverPhase phase, SealedRuntimeRole role, bool warmup, long iterations = 10)
    {
        phase.StartedIterations = phase.CompletedIterations = iterations;
        var duration = warmup ? S3Crossover.WarmupDuration : S3Crossover.MeasurementDuration;
        phase.Timing = new OperationTimingReport
        {
            Service = "s3", Workload = "s3-basic-object-crud",
            Role = role == SealedRuntimeRole.Candidate ? "candidate" : "prior",
            RuntimeDigest = role == SealedRuntimeRole.Candidate ? Candidate : Prior,
            OperationSchedule = S3Crossover.OperationSchedule,
            Concurrency = 8, WorkersCompleted = true,
            RequestedDurationSeconds = duration.TotalSeconds,
            ElapsedSeconds = duration.TotalSeconds + 1,
            Operations = S3RealAzureRcObservationTests.Operations.Select(operation =>
            {
                var count = operation switch
                {
                    "CreateBucket" or "DeleteBucket" => 8,
                    "HeadObject" or "DeleteObject" => iterations * 2,
                    "GetObject" => iterations * 3,
                    _ => iterations,
                };
                return new OperationTimingSummary
                {
                    Operation = operation, Phase = "lifecycle", Attempts = count, Successes = count,
                };
            }).ToList(),
        };
    }

    [Fact]
    public async Task Exact_ABBA_AA_has_separate_warmup_inventory_and_fixed_quiet_intervals()
    {
        var report = Report();
        var events = new List<string>();
        var publications = new List<string>();
        var clock = new Clock();
        await S3Crossover.RunAsync(report,
            (role, _) => { events.Add("restart:" + role); return Task.CompletedTask; },
            (role, warmup, phase, _) =>
            {
                events.Add(warmup ? "warmup" : "measurement");
                Fill(phase, role, warmup, warmup ? 9999 : role == SealedRuntimeRole.Candidate ? 20 : 10);
                return Task.CompletedTask;
            },
            _ => { events.Add("inventory"); return Task.FromResult(0L); },
            () => report.Binding,
            (duration, token) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(30), duration);
                token.ThrowIfCancellationRequested();
                events.Add("quiet");
                clock.Advance(duration);
                return Task.CompletedTask;
            },
            value =>
            {
                publications.Add(JsonSerializer.Serialize(value, S3CrossoverJsonContext.Default.S3CrossoverReport));
                return Task.CompletedTask;
            }, CancellationToken.None, clock);
        var expected = new[] { "Prior", "Candidate", "Candidate", "Prior", "Prior", "Prior" }
            .SelectMany(role => new[] { "restart:" + role, "inventory", "quiet", "inventory", "warmup", "inventory",
                "inventory", "quiet", "inventory", "measurement", "inventory" });
        Assert.Equal(expected, events);
        Assert.True(report.Completed);
        Assert.Equal("completed", report.Stage);
        Assert.False(report.Promotable);
        Assert.Equal(19, publications.Count);
        Assert.DoesNotContain("\"completed\": true", publications[0].Split("\"slots\"")[0], StringComparison.Ordinal);
        Assert.NotNull(report.Comparison);
        Assert.Equal(2, report.Comparison.PooledRatio);
        Assert.Equal(2, report.Comparison.ForwardRatio);
        Assert.Equal(2, report.Comparison.ReverseRatio);
        Assert.Equal(1, report.Comparison.ControlRatio);
        Assert.Equal(30.0 / 301, report.Comparison.PriorRate);
        Assert.All(report.Slots, slot =>
        {
            Assert.True(slot.Completed && slot.Warmup.Completed && slot.Measurement.Completed);
            Assert.Equal(slot.Role == "candidate" ? Candidate : Prior, slot.RuntimeDigest);
            Assert.Equal(30, slot.Measurement.QuietSeconds);
            Assert.Equal(slot.Index < 4 ? "ABBA" : "AA", slot.Comparison);
        });
    }

    [Theory]
    [InlineData("restart", "runtime_restart")]
    [InlineData("management_settings", "management_settings")]
    [InlineData("blob_settings", "blob_settings")]
    [InlineData("runtime_stop", "runtime_stop")]
    [InlineData("runtime_start", "runtime_start")]
    [InlineData("inventory", "warmup.inventory_before")]
    [InlineData("quiet", "warmup.quiet")]
    [InlineData("warmup", "warmup.workload")]
    [InlineData("measurement", "measurement.workload")]
    [InlineData("binding", "initial_binding")]
    [InlineData("cancel", "initial_binding")]
    public async Task Failures_stop_slots_and_publish_partial_sanitized_report(string failure, string expectedStage)
    {
        var report = Report();
        var original = new TimeoutException("private-key https://private.example/payload");
        var restarts = 0;
        var published = "";
        using var cancellation = new CancellationTokenSource();
        if (failure == "cancel") cancellation.Cancel();
        var error = await Record.ExceptionAsync(() => S3Crossover.RunAsync(report,
            (_, _) =>
            {
                restarts++;
                if (failure == "restart") throw original;
                if (failure is "management_settings" or "blob_settings" or "runtime_stop" or "runtime_start")
                {
                    report.Stage = failure;
                    throw original;
                }
                return Task.CompletedTask;
            },
            (role, warmup, phase, _) =>
            {
                Fill(phase, role, warmup);
                if (failure == (warmup ? "warmup" : "measurement")) throw original;
                return Task.CompletedTask;
            },
            _ => failure == "inventory" ? throw original : Task.FromResult(0L),
            () => failure == "binding" ? report.Binding with { Config = "changed" } : report.Binding,
            (_, _) => failure == "quiet" ? throw original : Task.CompletedTask,
            value =>
            {
                published = JsonSerializer.Serialize(value, S3CrossoverJsonContext.Default.S3CrossoverReport);
                return Task.CompletedTask;
            }, cancellation.Token));
        Assert.NotNull(error);
        Assert.InRange(restarts, 0, 1);
        Assert.False(report.Completed);
        Assert.Null(report.Comparison);
        Assert.NotNull(report.Failure);
        Assert.Equal(expectedStage, report.Stage);
        Assert.Contains($"\"stage\": \"{expectedStage}\"", published, StringComparison.Ordinal);
        Assert.All(report.Slots, slot => Assert.False(slot.Completed));
        Assert.DoesNotContain("private-key", published, StringComparison.Ordinal);
        Assert.DoesNotContain("private.example", published, StringComparison.Ordinal);
        if (failure == "measurement") Assert.NotNull(report.Slots[0].Measurement.Timing);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Dirty_inventory_at_each_phase_boundary_stops_comparison(int dirtyRead)
    {
        var report = Report();
        var reads = 0;
        var phases = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => S3Crossover.RunAsync(report,
            (_, _) => Task.CompletedTask,
            (role, warmup, phase, _) => { phases++; Fill(phase, role, warmup); return Task.CompletedTask; },
            _ => Task.FromResult(++reads == dirtyRead ? 1L : 0L),
            () => report.Binding, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(dirtyRead == 3 ? 1 : 0, phases);
        Assert.Single(report.Slots);
        Assert.False(report.Completed);
        Assert.Equal(dirtyRead switch
        {
            1 => "warmup.inventory_before",
            2 => "warmup.inventory_after_quiet",
            _ => "warmup.inventory_after",
        }, report.Stage);
    }

    [Fact]
    public async Task Binding_drift_after_warmup_blocks_measurement()
    {
        var report = Report();
        var drift = false;
        var phases = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => S3Crossover.RunAsync(report,
            (_, _) => Task.CompletedTask,
            (role, warmup, phase, _) =>
            {
                phases++; Fill(phase, role, warmup); drift = true; return Task.CompletedTask;
            },
            _ => Task.FromResult(0L),
            () => drift ? report.Binding with { Backend = "changed" } : report.Binding,
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(1, phases);
        Assert.Equal("warmup.binding_after", report.Stage);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("malformed")]
    public async Task Invalid_runtime_pair_never_starts(string kind)
    {
        var report = Report();
        report.PriorDigest = kind == "same" ? Candidate : "bad";
        await Assert.ThrowsAsync<InvalidDataException>(() => S3Crossover.RunAsync(report,
            (_, _) => throw new Exception("must not restart"),
            (_, _, _, _) => throw new Exception("must not measure"),
            _ => throw new Exception("must not inspect"),
            () => report.Binding, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Empty(report.Slots);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("short")]
    [InlineData("timeout")]
    [InlineData("nan")]
    [InlineData("digest")]
    [InlineData("role")]
    [InlineData("incomplete")]
    [InlineData("schedule")]
    [InlineData("double-delete")]
    [InlineData("bucket-count")]
    [InlineData("attempts")]
    [InlineData("error")]
    [InlineData("throttle")]
    [InlineData("repair")]
    [InlineData("duplicate")]
    [InlineData("started")]
    public void Invalid_measurement_is_not_a_comparison(string kind)
    {
        var phase = new S3CrossoverPhase();
        Fill(phase, SealedRuntimeRole.Candidate, false);
        var t = phase.Timing!;
        switch (kind)
        {
            case "empty": phase.CompletedIterations = 0; break;
            case "short": t.ElapsedSeconds = 299; break;
            case "timeout": t.ElapsedSeconds = 361; break;
            case "nan": t.ElapsedSeconds = double.NaN; break;
            case "digest": t.RuntimeDigest = Prior; break;
            case "role": t.Role = "prior"; break;
            case "incomplete": t.WorkersCompleted = false; break;
            case "schedule": t.OperationSchedule = S3RealAzureRcObservationTests.LifecycleOperationSchedule; break;
            case "double-delete": t.Operations.Single(r => r.Operation == "DeleteObject").Successes = 10; break;
            case "bucket-count": t.Operations.Single(r => r.Operation == "CreateBucket").Successes = 7; break;
            case "attempts": t.Operations[0].Attempts++; break;
            case "error": t.Operations[0].Errors++; break;
            case "throttle": t.Operations[0].Throttles++; break;
            case "repair": t.Operations.Add(new() { Phase = "cleanup", Attempts = 1 }); break;
            case "duplicate": t.Operations.Add(t.Operations[0]); break;
            case "started": phase.StartedIterations++; break;
        }
        Assert.Throws<InvalidDataException>(() =>
            S3Crossover.ValidatePhase(phase, Candidate, "candidate", S3Crossover.MeasurementDuration));
    }

    [Fact]
    public void Windows_are_not_double_counted_and_pooled_rates_use_actual_durations()
    {
        var phase = new S3CrossoverPhase();
        Fill(phase, SealedRuntimeRole.Candidate, false);
        phase.Timing!.Operations.Add(new()
        {
            Operation = "GetObject", Phase = "window-0", Successes = 30, Attempts = 30,
        });
        S3Crossover.ValidatePhase(phase, Candidate, "candidate", S3Crossover.MeasurementDuration);
        Assert.Equal(30.0 / 301, phase.GetObjectPerSecond);
        var slots = Enumerable.Range(0, 6).Select(_ => new S3CrossoverSlot()).ToArray();
        for (var i = 0; i < slots.Length; i++)
        {
            Fill(slots[i].Measurement, i is 1 or 2 ? SealedRuntimeRole.Candidate : SealedRuntimeRole.Prior, false);
            slots[i].Measurement.GetObjectPerSecond = 30.0 / 301;
        }
        slots[3].Measurement.Timing!.ElapsedSeconds = 350;
        var comparison = S3Crossover.Compare(slots);
        Assert.Equal(60.0 / 651, comparison.PriorRate);
        Assert.Equal(60.0 / 602, comparison.CandidateRate);
    }

    [Fact]
    public async Task Cancellation_during_inventory_stops_before_workload_and_publishes_failure()
    {
        var report = Report();
        using var cancellation = new CancellationTokenSource();
        var published = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => S3Crossover.RunAsync(report,
            (_, _) => Task.CompletedTask,
            (_, _, _, _) => throw new Exception("must not measure"),
            async token =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0L;
            },
            () => report.Binding, (_, _) => Task.CompletedTask,
            _ => { published = true; return Task.CompletedTask; }, cancellation.Token));
        Assert.True(published);
        Assert.False(report.Completed);
        Assert.Null(report.Comparison);
    }

    [Fact]
    public async Task Publication_failure_retains_original_failure()
    {
        var report = Report();
        var worker = new TimeoutException("worker");
        var publish = new IOException("publish");
        var error = await Assert.ThrowsAsync<AggregateException>(() => S3Crossover.RunAsync(report,
            (_, _) => throw worker, (_, _, _, _) => Task.CompletedTask,
            _ => Task.FromResult(0L), () => report.Binding, (_, _) => Task.CompletedTask,
            _ => throw publish, CancellationToken.None));
        Assert.Equal(new Exception[] { worker, publish }, error.InnerExceptions);
    }

    [Fact]
    public void Workflow_is_manual_only_pins_binaries_and_refreshes_login_before_always_cleanup()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "aws2azure.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var workflow = File.ReadAllText(Path.Combine(root.FullName, ".github/workflows/s3-controlled-crossover.yml"));
        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("schedule:", workflow, StringComparison.Ordinal);
        Assert.Contains("[ \"$REF\" != refs/heads/main ]", workflow, StringComparison.Ordinal);
        Assert.Contains("[ \"$REF_PROTECTED\" != true ]", workflow, StringComparison.Ordinal);
        Assert.Contains("[ \"$BUDGET_CONFIRMED\" != true ]", workflow, StringComparison.Ordinal);
        Assert.Contains("group: integration-real-azure", workflow, StringComparison.Ordinal);
        Assert.Contains("cancel-in-progress: false", workflow, StringComparison.Ordinal);
        Assert.Contains("--rollback-target", workflow, StringComparison.Ordinal);
        Assert.Contains("deploy/realazure/s3-load.bicep", workflow, StringComparison.Ordinal);
        Assert.Contains("Category=S3ControlledCrossover", workflow, StringComparison.Ordinal);
        Assert.Contains("> \"$PRIVATE_DIR/harness.log\" 2>&1", workflow, StringComparison.Ordinal);
        Assert.Contains("path: artifacts/s3-controlled-crossover/*.json", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("generate-rc-observation", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("AWS2AZURE_LOAD_EVIDENCE_PATH", workflow, StringComparison.Ordinal);
        var resolve = workflow.IndexOf("Resolve and pin both exact sealed runtimes", StringComparison.Ordinal);
        var login = workflow.IndexOf("name: Azure login (OIDC)", StringComparison.Ordinal);
        var refresh = workflow.IndexOf("name: Refresh Azure login for cleanup (OIDC)", StringComparison.Ordinal);
        var cleanup = workflow.IndexOf("name: Deallocate the owned backend", StringComparison.Ordinal);
        Assert.True(resolve < login && login < refresh && refresh < cleanup);
        Assert.Contains("if: always() && steps.azure.outcome == 'success'", workflow[refresh..cleanup], StringComparison.Ordinal);
        Assert.Contains("if: always() && steps.azure.outcome == 'success'", workflow[cleanup..], StringComparison.Ordinal);
        Assert.Contains("cleanup-real-azure-resource-groups.sh \"$RG_NAME\"", workflow, StringComparison.Ordinal);
        Assert.Contains("az group exists -n \"$RG_NAME\"", workflow, StringComparison.Ordinal);
        var deployment = File.ReadAllText(Path.Combine(root.FullName, "deploy/realazure/s3-load.bicep"));
        Assert.Contains("isVersioningEnabled: false", deployment, StringComparison.Ordinal);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan interval) => _ticks += interval.Ticks;
    }
}
