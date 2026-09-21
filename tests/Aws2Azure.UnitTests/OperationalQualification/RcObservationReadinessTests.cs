using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;

namespace Aws2Azure.UnitTests.OperationalQualification;

public sealed class RcObservationReadinessTests
{
    [Fact]
    public async Task Prepared_live_instance_waits_for_bound_release_and_records_actual_start()
    {
        using var fixture = new Fixture();
        fixture.OnDelay = () => fixture.Release();
        var start = await fixture.Wait();
        Assert.Equal(fixture.Initial.AddSeconds(120), start.ScheduledAtUtc);
        Assert.Equal(start.ScheduledAtUtc, fixture.Now);
        RcObservationReadiness.RecordMeasurementStart(fixture.Directory, "candidate", start, fixture.Now.AddSeconds(2));
        var recorded = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Directory, "started.json")))!;
        Assert.Equal(2, recorded["lateness_seconds"]!.GetValue<double>());
        Assert.Throws<IOException>(() =>
            RcObservationReadiness.RecordMeasurementStart(fixture.Directory, "candidate", start, fixture.Now));
    }

    [Theory]
    [InlineData("foreign-context")]
    [InlineData("wrong-ready-instance")]
    [InlineData("missing-peer")]
    [InlineData("wrong-lead")]
    public async Task Invalid_release_cannot_start_measurement(string failure)
    {
        using var fixture = new Fixture();
        fixture.OnDelay = () => fixture.Release(failure);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Wait());
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "started.json")));
    }

    [Fact]
    public async Task Missing_coordinator_has_a_bounded_fake_clock_deadline()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Wait());
        Assert.Equal(fixture.Initial.AddMinutes(10), fixture.Now);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("proxy-exit")]
    [InlineData("abort")]
    public async Task Cancellation_and_process_failure_are_not_release(string failure)
    {
        using var fixture = new Fixture();
        fixture.OnDelay = () =>
        {
            if (failure == "cancel") fixture.Cancellation.Cancel();
            else if (failure == "proxy-exit") fixture.Alive = false;
            else File.WriteAllText(Path.Combine(fixture.Directory, "abort"), "abort");
        };
        if (failure == "proxy-exit")
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Wait());
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Wait());
    }

    [Fact]
    public async Task Late_release_and_late_actual_start_fail_loud()
    {
        using var fixture = new Fixture();
        fixture.OnDelay = () => { fixture.Release(); fixture.Now = fixture.Initial.AddSeconds(181); };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Wait());
        var start = new RcObservationReadiness.Start(fixture.Initial, fixture.Initial, "context", "digest");
        Assert.Throws<InvalidDataException>(() =>
            RcObservationReadiness.RecordMeasurementStart(fixture.Directory, "candidate", start, fixture.Initial.AddSeconds(61)));
    }

    [Fact]
    public async Task Unselected_runtime_or_dead_proxy_never_publishes_readiness()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RcObservationReadiness.WaitAsync(fixture.Directory, "candidate", "wrong", "runtime",
                () => true, default, () => fixture.Now));
        fixture.Alive = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Wait());
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "ready.json")));
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "readiness-" + Guid.NewGuid().ToString("N"));
        public DateTimeOffset Initial { get; } = new(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Now;
        public bool Alive = true;
        public Action? OnDelay;
        public CancellationTokenSource Cancellation { get; } = new();

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Now = Initial;
            File.WriteAllText(Path.Combine(Directory, "context.json"), new JsonObject
            {
                ["context_id"] = "context",
                ["readiness_deadline_utc"] = Initial.AddMinutes(10).ToString("O"),
                ["runtimes"] = new JsonObject
                {
                    ["candidate"] = new JsonObject { ["identity_digest"] = "identity", ["runtime_digest"] = "runtime" },
                },
            }.ToJsonString());
        }

        public Task<RcObservationReadiness.Start> Wait() => RcObservationReadiness.WaitAsync(
            Directory, "candidate", "identity", "runtime", () => Alive, Cancellation.Token,
            () => Now, (duration, token) =>
            {
                token.ThrowIfCancellationRequested();
                Now += duration;
                var action = OnDelay;
                OnDelay = null;
                action?.Invoke();
                return Task.CompletedTask;
            });

        public void Release(string? failure = null)
        {
            var content = RcObservationReadiness.Digest(File.ReadAllBytes(Path.Combine(Directory, "ready.json")));
            var members = new JsonObject
            {
                ["candidate"] = new JsonObject { ["content_digest"] = failure == "wrong-ready-instance" ? "other" : content },
                ["stable"] = new JsonObject { ["content_digest"] = "peer" },
            };
            if (failure == "missing-peer") members.Remove("stable");
            File.WriteAllText(Path.Combine(Directory, "release.json"), new JsonObject
            {
                ["schema_version"] = 1,
                ["context_id"] = failure == "foreign-context" ? "other" : "context",
                ["released_at_utc"] = Initial.ToString("O"),
                ["scheduled_at_utc"] = Initial.AddSeconds(failure == "wrong-lead" ? 1 : 120).ToString("O"),
                ["ready"] = members,
            }.ToJsonString());
        }

        public void Dispose()
        {
            Cancellation.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
