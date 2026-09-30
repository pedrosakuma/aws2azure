using System.Net;
using System.Text.Json;
using Aws2Azure.IntegrationTests.SecretsManager;
using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

[Trait("Category", "RcObservationOffline")]
public sealed class SecretsManagerConcurrencyDiagnosticTests
{
    private static SecretsConcurrencyReport Report() => new()
    {
        CandidateDigest = "candidate-digest",
        Binding = new("backend", "configuration", "binding"),
    };

    private static OperationTimingReport Timing(int concurrency, bool warmup, long iterations = 100) => new()
    {
        RuntimeDigest = "candidate-digest",
        Concurrency = concurrency,
        RequestedDurationSeconds = warmup ? 30 : 300,
        ElapsedSeconds = warmup ? 30.5 : 301,
        WorkersCompleted = true,
        Operations = SecretsManagerRealAzureRcObservationTests.Operations.Select(operation =>
            new OperationTimingSummary
            {
                Operation = operation,
                Phase = "lifecycle",
                Attempts = iterations * (operation == "GetSecretValue" ? 3 : 1),
                Successes = iterations * (operation == "GetSecretValue" ? 3 : 1),
            }).ToList(),
    };

    [Fact]
    public async Task Fixed_shape_sequence_keeps_one_runtime_and_checks_cleanliness_around_every_phase()
    {
        var report = Report();
        var clock = new Clock();
        var events = new List<string>();
        var publications = new List<string>();
        await SecretsManagerConcurrencyDiagnostic.RunAsync(report,
            _ => { events.Add("restart"); return Task.CompletedTask; },
            (_, concurrency, warmup, phase, _) =>
            {
                events.Add(warmup ? "warmup" : "measurement");
                phase.Timing = Timing(concurrency, warmup, concurrency * 100);
                return Task.CompletedTask;
            },
            _ => { events.Add("inventory"); return Task.FromResult(new VaultInventory(0, 0)); },
            _ => { events.Add("probe"); return Task.FromResult(new[] { 1.0 }); },
            () => report.Binding,
            (duration, token) =>
            {
                Assert.Contains(duration, new[] { TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5) });
                events.Add(duration.TotalSeconds == 60 ? "quiet" : "settle");
                return clock.Delay(duration, token);
            },
            value =>
            {
                publications.Add(JsonSerializer.Serialize(value, SecretsConcurrencyJsonContext.Default.SecretsConcurrencyReport));
                return Task.CompletedTask;
            }, CancellationToken.None, clock);

        Assert.Equal(new[] { 5, 8, 8, 5, 5, 5 }, report.Slots.Select(slot => slot.Concurrency));
        var barrier = new[] { "inventory" }.Concat(
            Enumerable.Range(0, 12).SelectMany(_ => new[] { "settle", "inventory" })).ToArray();
        var perSlot = new[] { "restart", "probe", "inventory", "quiet", "inventory", "warmup" }
            .Concat(barrier)
            .Concat(["inventory", "quiet", "inventory", "measurement"])
            .Concat(barrier)
            .Concat(["probe"]);
        Assert.Equal(new[] { "inventory" }.Concat(Enumerable.Range(0, 6).SelectMany(_ => perSlot)), events);
        Assert.True(report.Completed);
        Assert.False(report.Promotable);
        Assert.Equal(19, publications.Count);
        var final = JsonSerializer.Deserialize(publications[^1], SecretsConcurrencyJsonContext.Default.SecretsConcurrencyReport)!;
        Assert.True(final.Completed);
        Assert.NotNull(final.Comparison);
        Assert.Equal(1.6, final.Comparison.PooledRatio, 10);
        Assert.Equal(1, final.Comparison.ScalingEfficiency, 10);
        Assert.Equal(1, final.Comparison.ControlRatio, 10);
        Assert.All(final.Slots, slot =>
        {
            Assert.True(slot.Completed);
            Assert.True(slot.Warmup.Completed);
            Assert.True(slot.Measurement.Completed);
            Assert.Equal(new VaultInventory(0, 0), slot.Measurement.After);
            Assert.Equal("candidate-digest", slot.Measurement.Timing!.RuntimeDigest);
            Assert.Equal(60, slot.Measurement.BarrierEmptySeconds);
            Assert.Equal(13, slot.Measurement.BarrierSamples.Count);
            Assert.Equal(60, slot.Measurement.BarrierSeconds);
            Assert.Equal(60, slot.Measurement.QuietSeconds);
        });
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("warmup")]
    [InlineData("measurement")]
    [InlineData("probe")]
    [InlineData("inventory")]
    [InlineData("dirty-initial")]
    [InlineData("dirty-after-quiet")]
    [InlineData("drift")]
    [InlineData("cancel")]
    public async Task Failure_aborts_later_slots_and_preserves_sanitized_partial_report(string failure)
    {
        var report = Report();
        var clock = new Clock();
        using var cts = new CancellationTokenSource();
        if (failure == "cancel") cts.Cancel();
        var calls = 0;
        var dirtyAfterQuiet = false;
        var drift = false;
        var published = "";
        var original = new InvalidOperationException("credential-secret https://vault-private.example/value");
        var error = await Record.ExceptionAsync(() => SecretsManagerConcurrencyDiagnostic.RunAsync(report,
            _ =>
            {
                if (failure == "restart") throw original;
                return Task.CompletedTask;
            },
            (_, concurrency, warmup, phase, _) =>
            {
                calls++;
                phase.Timing = Timing(concurrency, warmup);
                if (failure == (warmup ? "warmup" : "measurement")) throw original;
                if (failure == "drift") drift = true;
                return Task.CompletedTask;
            },
            _ =>
            {
                if (failure == "inventory") throw original;
                return Task.FromResult(new VaultInventory(
                    failure == "dirty-initial" || dirtyAfterQuiet ? 1 : 0, 0));
            },
            _ => failure == "probe" ? throw original : Task.FromResult(Array.Empty<double>()),
            () => drift ? report.Binding with { Config = "changed" } : report.Binding,
            (duration, token) =>
            {
                dirtyAfterQuiet = failure == "dirty-after-quiet";
                return clock.Delay(duration, token);
            },
            value =>
            {
                published = JsonSerializer.Serialize(value, SecretsConcurrencyJsonContext.Default.SecretsConcurrencyReport);
                return Task.CompletedTask;
            }, cts.Token, clock));
        Assert.NotNull(error);
        Assert.InRange(report.Slots.Count, 0, 1);
        Assert.InRange(calls, 0, 2);
        Assert.False(report.Completed);
        Assert.Null(report.Comparison);
        Assert.NotNull(report.Failure);
        Assert.All(report.Slots, slot => Assert.False(slot.Completed));
        Assert.DoesNotContain("credential-secret", published, StringComparison.Ordinal);
        Assert.DoesNotContain("vault-private", published, StringComparison.Ordinal);
        Assert.NotEmpty(published);
        if (failure == "measurement")
            Assert.NotNull(report.Slots[0].Measurement.Timing);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(14, true)]
    public async Task Empty_then_deleted_transition_recovers_only_inside_the_barrier(int dirtyRead, bool afterBarrier)
    {
        var report = Report();
        var clock = new Clock();
        var postWarmupReads = 0;
        var warmupFinished = false;
        var measurementStarted = false;
        var task = SecretsManagerConcurrencyDiagnostic.RunAsync(report,
                _ => Task.CompletedTask,
                (_, concurrency, warmup, phase, _) =>
                {
                    measurementStarted |= !warmup;
                    phase.Timing = Timing(concurrency, warmup, 114);
                    warmupFinished = true;
                    return Task.CompletedTask;
                },
                _ => Task.FromResult(new VaultInventory(0,
                    warmupFinished && ++postWarmupReads == dirtyRead ? 2 : 0)),
                _ => Task.FromResult(Array.Empty<double>()),
                () => report.Binding,
                clock.Delay,
                _ => Task.CompletedTask,
                CancellationToken.None, clock);

        if (afterBarrier)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => task);
            Assert.False(measurementStarted);
            var partial = Assert.Single(report.Slots);
            Assert.True(partial.Warmup.Completed);
            Assert.Equal(60, partial.Warmup.BarrierEmptySeconds);
            Assert.Equal(new VaultInventory(0, 2), partial.Measurement.Before);
            Assert.Null(report.Comparison);
            return;
        }

        await task;
        Assert.True(measurementStarted);
        var slot = report.Slots[0];
        Assert.True(slot.Warmup.Completed);
        Assert.Equal(114, slot.Warmup.CompletedIterations);
        Assert.Equal(15, slot.Warmup.BarrierPolls);
        Assert.Equal(new VaultInventorySample(0, 0, 0), slot.Warmup.BarrierSamples[0]);
        Assert.Equal(new VaultInventorySample(5, 0, 2), slot.Warmup.BarrierSamples[1]);
        Assert.Equal(new VaultInventorySample(70, 0, 0), slot.Warmup.BarrierSamples[^1]);
        Assert.Equal(60, slot.Warmup.BarrierEmptySeconds);
        Assert.Equal(new VaultInventory(0, 0), slot.Warmup.After);
        Assert.Equal(new VaultInventory(0, 0), slot.Measurement.Before);
        Assert.True(slot.Measurement.Completed);
        Assert.True(report.Completed);
        Assert.NotNull(report.Comparison);
    }

    [Fact]
    public async Task Publication_failure_preserves_original_failure()
    {
        var report = Report();
        var original = new TimeoutException("worker");
        var publication = new IOException("disk");
        var result = await Assert.ThrowsAsync<AggregateException>(() =>
            SecretsManagerConcurrencyDiagnostic.RunAsync(report,
                _ => throw original, (_, _, _, _, _) => Task.CompletedTask,
                _ => Task.FromResult(new VaultInventory(0, 0)), _ => Task.FromResult(Array.Empty<double>()),
                () => report.Binding, (_, _) => Task.CompletedTask, _ => throw publication, CancellationToken.None));
        Assert.Equal(new Exception[] { original, publication }, result.InnerExceptions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Purge_barrier_is_bounded_and_does_not_clean_or_ignore_deleted_inventory(bool converges)
    {
        var phase = new SecretsConcurrencyPhase();
        var clock = new Clock();
        var reads = 0;
        var delays = 0;
        var task = SecretsManagerConcurrencyDiagnostic.WaitForStableEmptyAsync(phase,
            _ =>
            {
                reads++;
                return Task.FromResult(new VaultInventory(0, converges && reads > 2 ? 0 : 1));
            },
            (duration, token) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(5), duration);
                delays++;
                return clock.Delay(duration, token);
            }, CancellationToken.None, clock);
        if (converges)
        {
            await task;
            Assert.Equal(15, phase.BarrierPolls);
            Assert.Equal(60, phase.BarrierEmptySeconds);
            Assert.Equal(70, phase.BarrierSeconds);
            Assert.Equal(new VaultInventory(0, 0), phase.After);
        }
        else
        {
            await Assert.ThrowsAsync<TimeoutException>(() => task);
            Assert.Equal(25, phase.BarrierPolls);
            Assert.Equal(24, delays);
            Assert.Equal(120, phase.BarrierSeconds);
            Assert.Equal(0, phase.BarrierEmptySeconds);
            Assert.Equal(new VaultInventory(0, 1), phase.After);
        }
        Assert.Equal(reads, phase.BarrierSamples.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reappearing_active_or_deleted_inventory_resets_stability_not_total_deadline(bool active)
    {
        var phase = new SecretsConcurrencyPhase();
        var clock = new Clock();
        var reads = 0;
        await Assert.ThrowsAsync<TimeoutException>(() =>
            SecretsManagerConcurrencyDiagnostic.WaitForStableEmptyAsync(phase,
                _ =>
                {
                    var dirty = ++reads == 13;
                    return Task.FromResult(new VaultInventory(dirty && active ? 1 : 0, dirty && !active ? 2 : 0));
                }, clock.Delay, CancellationToken.None, clock));
        Assert.Equal(25, reads);
        Assert.Equal(120, phase.BarrierSeconds);
        Assert.Equal(55, phase.BarrierEmptySeconds);
        Assert.Equal(25, phase.BarrierSamples.Count);
        Assert.False(phase.Completed);
    }

    [Fact]
    public async Task Full_empty_window_completed_exactly_at_deadline_is_accepted()
    {
        var phase = new SecretsConcurrencyPhase();
        var clock = new Clock();
        var reads = 0;
        await SecretsManagerConcurrencyDiagnostic.WaitForStableEmptyAsync(phase,
            _ => Task.FromResult(new VaultInventory(0, ++reads <= 12 ? 1 : 0)),
            clock.Delay, CancellationToken.None, clock);
        Assert.Equal(25, reads);
        Assert.Equal(120, phase.BarrierSeconds);
        Assert.Equal(60, phase.BarrierEmptySeconds);
    }

    [Fact]
    public async Task Empty_reads_without_elapsed_time_cannot_satisfy_stability()
    {
        var phase = new SecretsConcurrencyPhase();
        var clock = new Clock();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            SecretsManagerConcurrencyDiagnostic.WaitForStableEmptyAsync(phase,
                _ => Task.FromResult(new VaultInventory(0, 0)),
                (_, _) => Task.CompletedTask, CancellationToken.None, clock));
        Assert.Equal(25, phase.BarrierPolls);
        Assert.Equal(0, phase.BarrierEmptySeconds);
    }

    [Fact]
    public async Task Slow_final_empty_read_cannot_succeed_after_the_total_deadline()
    {
        var phase = new SecretsConcurrencyPhase();
        var clock = new Clock();
        var reads = 0;
        await Assert.ThrowsAsync<TimeoutException>(() =>
            SecretsManagerConcurrencyDiagnostic.WaitForStableEmptyAsync(phase,
                _ =>
                {
                    if (++reads == 2) clock.Advance(TimeSpan.FromSeconds(120));
                    return Task.FromResult(new VaultInventory(0, 0));
                }, clock.Delay, CancellationToken.None, clock));
        Assert.Equal(2, reads);
        Assert.Equal(125, phase.BarrierSeconds);
        Assert.False(phase.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inventory_failure_or_cancellation_is_not_retried_or_hidden(bool cancel)
    {
        var phase = new SecretsConcurrencyPhase();
        var clock = new Clock();
        using var cts = new CancellationTokenSource();
        var reads = 0;
        var failure = new HttpRequestException("private inventory failure", null, HttpStatusCode.Forbidden);
        var result = await Record.ExceptionAsync(() =>
            SecretsManagerConcurrencyDiagnostic.WaitForStableEmptyAsync(phase,
                _ =>
                {
                    if (++reads == 2)
                    {
                        if (!cancel) throw failure;
                        cts.Cancel();
                    }
                    return Task.FromResult(new VaultInventory(0, 0));
                }, clock.Delay, cts.Token, clock));
        if (cancel)
            Assert.IsAssignableFrom<OperationCanceledException>(result);
        else
            Assert.Same(failure, result);
        Assert.Equal(2, reads);
        Assert.Single(phase.BarrierSamples);
        Assert.False(phase.Completed);
    }

    [Fact]
    public async Task Failed_stability_barrier_preserves_samples_and_never_starts_measurement()
    {
        var report = Report();
        var clock = new Clock();
        var measures = 0;
        var reads = 0;
        var published = "";
        await Assert.ThrowsAsync<TimeoutException>(() =>
            SecretsManagerConcurrencyDiagnostic.RunAsync(report,
                _ => Task.CompletedTask,
                (_, concurrency, warmup, phase, _) =>
                {
                    measures++;
                    Assert.True(warmup);
                    phase.Timing = Timing(concurrency, warmup);
                    return Task.CompletedTask;
                },
                _ => Task.FromResult(new VaultInventory(0, measures > 0 && ++reads % 2 == 0 ? 2 : 0)),
                _ => Task.FromResult(Array.Empty<double>()), () => report.Binding,
                clock.Delay,
                value =>
                {
                    published = JsonSerializer.Serialize(value, SecretsConcurrencyJsonContext.Default.SecretsConcurrencyReport);
                    return Task.CompletedTask;
                }, CancellationToken.None, clock));

        var retained = JsonSerializer.Deserialize(published, SecretsConcurrencyJsonContext.Default.SecretsConcurrencyReport)!;
        Assert.False(retained.Completed);
        Assert.Null(retained.Comparison);
        Assert.NotNull(retained.Failure);
        var slot = Assert.Single(retained.Slots);
        Assert.False(slot.Warmup.Completed);
        Assert.Equal(25, slot.Warmup.BarrierSamples.Count);
        Assert.Equal(120, slot.Warmup.BarrierSeconds);
        Assert.Null(slot.Measurement.Before);
        Assert.Equal(1, measures);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("throttle")]
    [InlineData("ratio")]
    [InlineData("zero")]
    [InlineData("missing-operation")]
    [InlineData("incomplete")]
    [InlineData("wrong-runtime")]
    [InlineData("wrong-shape")]
    [InlineData("short")]
    [InlineData("unbounded")]
    [InlineData("nan")]
    [InlineData("repair")]
    public void Invalid_phase_never_becomes_success(string fault)
    {
        var timing = Timing(5, false);
        switch (fault)
        {
            case "error": timing.Operations[0].Errors++; break;
            case "throttle": timing.Operations[0].Throttles++; break;
            case "ratio": timing.Operations[0].Successes--; break;
            case "zero": timing = Timing(5, false, 0); break;
            case "missing-operation": timing.Operations.RemoveAt(0); break;
            case "incomplete": timing.WorkersCompleted = false; break;
            case "wrong-runtime": timing.RuntimeDigest = "prior"; break;
            case "wrong-shape": timing.Concurrency = 8; break;
            case "short": timing.ElapsedSeconds = 299; break;
            case "unbounded": timing.ElapsedSeconds = 421; break;
            case "nan": timing.ElapsedSeconds = double.NaN; break;
            case "repair": timing.Operations.Add(new() { Phase = "cleanup", Attempts = 1 }); break;
        }
        var phase = new SecretsConcurrencyPhase { Timing = timing };
        Assert.Throws<InvalidDataException>(() =>
            SecretsManagerConcurrencyDiagnostic.ValidatePhase(phase, 5, "candidate-digest", TimeSpan.FromMinutes(5)));
        Assert.False(phase.Completed);
    }

    [Fact]
    public void Pooled_rate_uses_actual_durations_not_mean_of_rates_or_aa_samples()
    {
        var slots = Enumerable.Range(0, 6).Select(i => new SecretsConcurrencySlot
        {
            Measurement = new()
            {
                Timing = new() { ElapsedSeconds = i == 0 ? 300 : 400 },
                CompletedIterations = i >= 4 ? 10000 : 100,
                GetSecretValuePerSecond = i == 0 ? 1 : 0.75,
            },
        }).ToList();
        var result = SecretsManagerConcurrencyDiagnostic.Compare(slots);
        Assert.Equal(600.0 / 700, result.FiveWorkerRate);
        Assert.Equal(600.0 / 800, result.EightWorkerRate);
    }

    [Fact]
    public async Task Oidc_refresh_failure_cancels_work_and_is_not_silent()
    {
        var original = new IOException("secret");
        var result = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SecretsManagerRealAzureConcurrencyTests.RunWithRefreshAsync(
                token => Task.Delay(Timeout.InfiniteTimeSpan, token),
                _ => throw original, Task.Delay, CancellationToken.None));
        Assert.Same(original, result.InnerException);
    }

    [Fact]
    public async Task Normal_work_completion_stops_refresh_without_failure()
    {
        var refreshed = 0;
        await SecretsManagerRealAzureConcurrencyTests.RunWithRefreshAsync(
            _ => Task.CompletedTask, _ => { refreshed++; return Task.CompletedTask; },
            Task.Delay, CancellationToken.None);
        Assert.Equal(1, refreshed);
    }

    [Fact]
    public async Task Work_failure_is_preserved_when_refresh_is_cancelled()
    {
        var failure = new InvalidDataException("work");
        var result = await Assert.ThrowsAsync<InvalidDataException>(() =>
            SecretsManagerRealAzureConcurrencyTests.RunWithRefreshAsync(
                _ => throw failure, _ => Task.CompletedTask, Task.Delay, CancellationToken.None));
        Assert.Same(failure, result);
    }

    [Fact]
    public async Task Concurrent_work_and_refresh_failures_are_both_preserved()
    {
        var trigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workFailure = new InvalidDataException("work");
        var refreshFailure = new IOException("refresh");
        var result = await Assert.ThrowsAsync<AggregateException>(() =>
            SecretsManagerRealAzureConcurrencyTests.RunWithRefreshAsync(
                _ =>
                {
                    trigger.SetResult();
                    throw workFailure;
                },
                async _ =>
                {
                    await trigger.Task;
                    throw refreshFailure;
                }, Task.Delay, CancellationToken.None));
        Assert.Equal(new Exception[] { workFailure, refreshFailure }, result.InnerExceptions);
    }

    [Fact]
    public async Task Inventory_follows_both_collections_and_keeps_only_counts()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            requests.Add(request.RequestUri!.PathAndQuery);
            return Json(request.RequestUri.Query.Contains("skiptoken", StringComparison.Ordinal)
                ? """{"value":[{"id":"private-secret"}]}"""
                : $$"""{"value":[{"id":"private-secret"}],"nextLink":"https://test.vault.azure.net{{request.RequestUri.AbsolutePath}}?api-version=7.6&skiptoken=page2"}""");
        }));
        var inventory = new DiagnosticKeyVaultInventory(http, new("https://test.vault.azure.net/"),
            _ => ValueTask.FromResult("private-bearer"));
        Assert.Equal(new VaultInventory(2, 2), await inventory.ReadAsync(CancellationToken.None));
        Assert.Equal(4, requests.Count);
        Assert.Contains("/deletedsecrets?api-version=7.6&maxresults=25", requests);
    }

    [Theory]
    [InlineData("""{"value":[],"nextLink":"https://other.vault.azure.net/secrets?api-version=7.6"}""")]
    [InlineData("""{"value":[],"nextLink":"http://test.vault.azure.net/secrets?api-version=7.6"}""")]
    [InlineData("""{"value":[],"nextLink":"https://test.vault.azure.net/secrets/private?api-version=7.6"}""")]
    [InlineData("""{"value":[],"nextLink":"https://test.vault.azure.net/secrets?api-version=7.6&maxresults=25"}""")]
    [InlineData("""{"value":[],"nextLink":false}""")]
    [InlineData("""{"value":{}}""")]
    public async Task Inventory_rejects_invalid_cross_host_or_looping_pages(string body)
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(_ => { requests++; return Json(body); }));
        var inventory = new DiagnosticKeyVaultInventory(http, new("https://test.vault.azure.net/"),
            _ => ValueTask.FromResult("private-bearer"));
        await Assert.ThrowsAsync<InvalidDataException>(() => inventory.ReadAsync(CancellationToken.None));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Inventory_never_treats_http_failure_as_an_empty_vault(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(_ => new(status)));
        var inventory = new DiagnosticKeyVaultInventory(http, new("https://test.vault.azure.net/"),
            _ => ValueTask.FromResult("token"));
        await Assert.ThrowsAsync<HttpRequestException>(() => inventory.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Probes_are_anonymous_and_require_authentication_denial()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            calls++;
            return new(HttpStatusCode.Unauthorized);
        }));
        var inventory = new DiagnosticKeyVaultInventory(http, new("https://test.vault.azure.net/"),
            _ => throw new InvalidOperationException("Must not acquire bearer for probe"));
        Assert.Equal(12, (await inventory.ProbeAsync(CancellationToken.None)).Length);
        Assert.Equal(12, calls);
    }

    [Fact]
    public void Workflow_is_manual_budget_guarded_single_candidate_and_always_cleans_owned_resources()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "aws2azure.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var workflow = File.ReadAllText(Path.Combine(root.FullName,
            ".github", "workflows", "secretsmanager-concurrency-diagnostic.yml"));
        foreach (var required in new[]
        {
            "workflow_dispatch:", "[ \"$REF\" != refs/heads/main ]", "[ \"$REF_PROTECTED\" != true ]",
            "[ \"$BUDGET_CONFIRMED\" != true ]", "default: false", "group: integration-real-azure",
            "cancel-in-progress: false", "purpose=aws2azure-nightly", "AWS2AZURE_SEALED_RUNTIME_MODE: candidate",
            "deployStableVault=false", "export-approved-runtime", "--role candidate",
            "--github-env \"$GITHUB_ENV\"", "Category=SecretsManagerConcurrencyDiagnostic",
            "if: always() && steps.azure.outcome == 'success'",
            "cleanup-real-azure-resource-groups.sh \"$RG_NAME\"", "az keyvault list-deleted",
            "path: artifacts/secretsmanager-concurrency/*.json",
            ".promotable == false and .completed == true",
            "([.slots[].concurrency] == [5,8,8,5,5,5])",
            "> \"$PRIVATE_DIR/harness.log\" 2>&1",
        })
            Assert.Contains(required, workflow, StringComparison.Ordinal);
        foreach (var prohibited in new[]
        {
            "pull_request:", "schedule:", "--role prior", "--rollback-target",
            "AWS2AZURE_LOAD_EVIDENCE_PATH", "generate-rc-observation", "release-promotion",
            "--allow-pending-deletion",
        })
            Assert.DoesNotContain(prohibited, workflow, StringComparison.Ordinal);
        Assert.True(workflow.IndexOf("Resolve and pin the exact sealed candidate", StringComparison.Ordinal)
            < workflow.IndexOf("Azure login (OIDC)", StringComparison.Ordinal));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;

        internal Task Delay(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Advance(duration);
            return Task.CompletedTask;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }
}
