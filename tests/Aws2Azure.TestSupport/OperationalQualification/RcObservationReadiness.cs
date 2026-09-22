using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aws2Azure.TestSupport.OperationalQualification;

/// <summary>Local, live-harness half of the immutable Actions artifact rendezvous.</summary>
public static class RcObservationReadiness
{
    public static readonly TimeSpan MaximumWait = TimeSpan.FromMinutes(45);
    public static readonly TimeSpan MaximumLateness = TimeSpan.FromSeconds(60);

    public sealed record Start(DateTimeOffset ReadyAtUtc, DateTimeOffset ScheduledAtUtc, string ContextId, string ReadyDigest);

    public static async Task<Start> WaitAsync(
        string directory,
        string role,
        string runtimeIdentityDigest,
        string runtimeDigest,
        Func<bool> isProxyAlive,
        CancellationToken cancellationToken,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        utcNow ??= () => DateTimeOffset.UtcNow;
        delay ??= Task.Delay;
        if (role is not ("candidate" or "stable"))
            throw new InvalidDataException("Unknown observation cohort.");
        var context = ReadObject(Path.Combine(directory, "context.json"));
        var contextId = Required(context, "context_id");
        var expected = context["runtimes"]?[role]?.AsObject()
            ?? throw new InvalidDataException("Missing selected runtime.");
        if (Required(expected, "identity_digest") != runtimeIdentityDigest
            || Required(expected, "runtime_digest") != runtimeDigest)
            throw new InvalidDataException("The prepared runtime differs from the selected sealed identity.");
        var deadline = Timestamp(context, "readiness_deadline_utc");
        var readyAt = utcNow();
        if (deadline <= readyAt || deadline - readyAt > MaximumWait)
            throw new InvalidDataException("Readiness deadline is expired or outside its bounded budget.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!isProxyAlive()) throw new InvalidOperationException("Prepared proxy is not alive.");
        var ready = new JsonObject
        {
            ["schema_version"] = 1,
            ["context_id"] = contextId,
            ["cohort"] = role,
            ["instance"] = Guid.NewGuid().ToString("N"),
            ["harness_pid"] = Environment.ProcessId,
            ["stage"] = "sealed-runtime-and-canary-verified",
            ["runtime_identity_digest"] = runtimeIdentityDigest,
            ["runtime_digest"] = runtimeDigest,
            ["ready_at_utc"] = readyAt.ToString("O", CultureInfo.InvariantCulture),
            ["readiness_deadline_utc"] = deadline.ToString("O", CultureInfo.InvariantCulture),
        };
        var readyPath = Path.Combine(directory, "ready.json");
        Publish(readyPath, ready);
        var readyDigest = Digest(File.ReadAllBytes(readyPath));
        var elapsed = Stopwatch.StartNew();
        DateTimeOffset scheduled;
        while (true)
        {
            Check();
            var releasePath = Path.Combine(directory, "release.json");
            if (File.Exists(releasePath))
            {
                var release = ReadObject(releasePath);
                if (Required(release, "context_id") != contextId
                    || release["schema_version"]?.GetValue<int>() != 1
                    || release["ready"] is not JsonObject members || members.Count != 2
                    || !members.ContainsKey("candidate") || !members.ContainsKey("stable")
                    || release["ready"]?[role]?["content_digest"]?.GetValue<string>() != readyDigest)
                    throw new InvalidDataException("Release does not bind this exact ready instance.");
                scheduled = Timestamp(release, "scheduled_at_utc");
                var releasedAt = Timestamp(release, "released_at_utc");
                if (releasedAt < readyAt - MaximumLateness
                    || scheduled - releasedAt != TimeSpan.FromSeconds(120)
                    || scheduled > deadline)
                    throw new InvalidDataException("Release schedule is invalid or outside the readiness budget.");
                break;
            }
            await delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        while (utcNow() < scheduled)
        {
            Check();
            await delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        Check();
        var actual = utcNow();
        if (actual < scheduled || actual - scheduled > MaximumLateness)
            throw new InvalidDataException("The released observation start was missed.");
        return new Start(readyAt, scheduled, contextId, readyDigest);

        void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(directory, "abort")))
                throw new OperationCanceledException("Observation coordinator aborted readiness.");
            if (!isProxyAlive()) throw new InvalidOperationException("Prepared proxy exited while awaiting release.");
            if (utcNow() >= deadline || elapsed.Elapsed >= MaximumWait)
                throw new TimeoutException("Observation readiness deadline expired.");
        }
    }

    public static void RecordMeasurementStart(string directory, string role, Start start, DateTimeOffset actual)
    {
        if (actual < start.ScheduledAtUtc || actual - start.ScheduledAtUtc > MaximumLateness)
            throw new InvalidDataException("The released measurement start was missed.");
        Publish(Path.Combine(directory, "started.json"), new JsonObject
        {
            ["schema_version"] = 1,
            ["context_id"] = start.ContextId,
            ["cohort"] = role,
            ["ready_content_digest"] = start.ReadyDigest,
            ["ready_at_utc"] = start.ReadyAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["scheduled_at_utc"] = start.ScheduledAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["actual_at_utc"] = actual.ToString("O", CultureInfo.InvariantCulture),
            ["lateness_seconds"] = (actual - start.ScheduledAtUtc).TotalSeconds,
        });
    }

    public static string Digest(byte[] bytes) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static JsonObject ReadObject(string path)
    {
        if (new FileInfo(path).Length > 32768)
            throw new InvalidDataException("Readiness metadata exceeds its bounded size.");
        return JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException("Missing readiness metadata.");
    }

    private static string Required(JsonObject value, string name) =>
        value[name]?.GetValue<string>() is { Length: > 0 } text
            ? text : throw new InvalidDataException($"Missing readiness field {name}.");

    private static DateTimeOffset Timestamp(JsonObject value, string name) =>
        DateTimeOffset.TryParse(Required(value, name), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var timestamp) && timestamp.Offset == TimeSpan.Zero
            ? timestamp : throw new InvalidDataException($"Invalid UTC readiness field {name}.");

    private static void Publish(string path, JsonObject value)
    {
        var pending = path + ".pending";
        using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value);
            stream.Flush(flushToDisk: true);
        }
        File.Move(pending, path, overwrite: false);
    }
}
