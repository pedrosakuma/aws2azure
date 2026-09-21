using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aws2Azure.PerfTests.DynamoDb;

internal sealed record BatchWriteRuntime(string Commit, string Executable, Dictionary<string, string> Files, string? BuildMode = null)
{
    public static BatchWriteRuntime Verify(string directory)
    {
        var identity = JsonSerializer.Deserialize<BatchWriteRuntime>(
            File.ReadAllText(Path.Combine(directory, "runtime-identity.json")))
            ?? throw new InvalidOperationException("Missing runtime identity.");
        if (identity.Commit.Length != 40 || !identity.Commit.All(Uri.IsHexDigit)
            || identity.Executable != "Aws2Azure.Proxy")
            throw new InvalidOperationException("Invalid source-pinned runtime identity.");
        var actual = HashFiles(Path.Combine(directory, "app"));
        if (actual.Count != identity.Files.Count || actual.Any(pair =>
            !identity.Files.TryGetValue(pair.Key, out var hash) || hash != pair.Value)
            || !actual.ContainsKey(identity.Executable))
            throw new InvalidOperationException("Runtime files differ from the source-pinned build manifest.");
        return identity;
    }

    public static Dictionary<string, string> HashFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToDictionary(
                path => Path.GetRelativePath(directory, path).Replace('\\', '/'),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                StringComparer.Ordinal);
}

internal sealed record BatchWriteProcessSample(
    DateTimeOffset CapturedAtUtc, double CpuSeconds, long WorkingSetBytes,
    long LifetimePeakWorkingSetBytes, int Threads, long? AllocatedBytes, int? Gen0, int? Gen1, int? Gen2)
{
    public static BatchWriteProcessSample Capture(int pid, bool currentProcess = false)
    {
        using var process = Process.GetProcessById(pid);
        return new(DateTimeOffset.UtcNow, process.TotalProcessorTime.TotalSeconds,
            process.WorkingSet64, process.PeakWorkingSet64, process.Threads.Count,
            currentProcess ? GC.GetTotalAllocatedBytes(false) : null,
            currentProcess ? GC.CollectionCount(0) : null,
            currentProcess ? GC.CollectionCount(1) : null,
            currentProcess ? GC.CollectionCount(2) : null);
    }

    public static object? Delta(BatchWriteProcessSample? before, BatchWriteProcessSample? after) =>
        before is null || after is null ? null : new
        {
            seconds = (after.CapturedAtUtc - before.CapturedAtUtc).TotalSeconds,
            cpuSeconds = after.CpuSeconds - before.CpuSeconds,
            allocatedBytes = after.AllocatedBytes - before.AllocatedBytes,
            gen0 = after.Gen0 - before.Gen0, gen1 = after.Gen1 - before.Gen1, gen2 = after.Gen2 - before.Gen2,
            before, after,
        };
}

internal sealed record BatchWriteFinalSnapshots(
    BatchWriteProcessSample? Proxy, BatchWriteProcessSample? Driver,
    Dictionary<string, double>? Stages, RuntimeMemorySnapshot? Memory, string[] Unavailable)
{
    public static async Task<BatchWriteFinalSnapshots> CaptureAsync(int proxyPid, string endpoint)
    {
        var unavailable = new List<string>();
        BatchWriteProcessSample? proxy = null, driver = null;
        Dictionary<string, double>? stages = null;
        RuntimeMemorySnapshot? memory = null;
        try { proxy = BatchWriteProcessSample.Capture(proxyPid); }
        catch (Exception ex) { unavailable.Add("proxy-process:" + BatchWriteExperimentRelay.SafeExceptionType(ex)); }
        try { driver = BatchWriteProcessSample.Capture(Environment.ProcessId, currentProcess: true); }
        catch (Exception ex) { unavailable.Add("driver-process:" + BatchWriteExperimentRelay.SafeExceptionType(ex)); }
        try
        {
            stages = await BatchWriteStageTelemetry.ScrapeAsync(endpoint);
            if (stages is null) unavailable.Add("stages:missing");
        }
        catch (Exception ex) { unavailable.Add("stages:" + BatchWriteExperimentRelay.SafeExceptionType(ex)); }
        try
        {
            using var probe = new ProxyMemoryProbe(endpoint);
            memory = await probe.SampleAsync();
            if (memory is null) unavailable.Add("memory:missing");
        }
        catch (Exception ex) { unavailable.Add("memory:" + BatchWriteExperimentRelay.SafeExceptionType(ex)); }
        return new(proxy, driver, stages, memory, unavailable.ToArray());
    }
}

internal sealed class BatchWriteResourceWindow : IAsyncDisposable
{
    internal const int MaxSamples = 160;
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private readonly List<Sample> _samples = new(MaxSamples);
    private readonly CancellationTokenSource _stop = new();
    private readonly int _proxyPid;
    private readonly long _started = Stopwatch.GetTimestamp();
    private Task? _sampling;
    private int _dropped, _captureFailures;

    internal sealed record Sample(double ElapsedSeconds, BatchWriteProcessSample Proxy, BatchWriteProcessSample Driver);

    public BatchWriteResourceWindow(int proxyPid) => _proxyPid = proxyPid;

    public void Start()
    {
        if (_sampling is not null) throw new InvalidOperationException("Resource window already started.");
        _sampling = RunAsync();
    }

    internal void Add(Sample sample)
    {
        if (_samples.Count == MaxSamples) { _dropped++; return; }
        if (sample.ElapsedSeconds < 0 || !double.IsFinite(sample.ElapsedSeconds)
            || (_samples.Count > 0 && sample.ElapsedSeconds <= _samples[^1].ElapsedSeconds))
            throw new ArgumentException("Samples must have increasing monotonic timestamps.");
        _samples.Add(sample);
    }

    private void Capture()
    {
        try
        {
            Add(new(Stopwatch.GetElapsedTime(_started).TotalSeconds,
                BatchWriteProcessSample.Capture(_proxyPid),
                BatchWriteProcessSample.Capture(Environment.ProcessId, currentProcess: true)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            _captureFailures++;
        }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            Capture();
            while (_samples.Count < MaxSamples
                && Stopwatch.GetElapsedTime(_started) < BatchWriteExperimentPlan.DispatchDuration + BatchWriteExperimentPlan.DrainTimeout
                && await timer.WaitForNextTickAsync(_stop.Token))
                Capture();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        if (_sampling is not null) await _sampling;
        Capture();
    }

    public object Snapshot() => new
    {
        intervalMilliseconds = Interval.TotalMilliseconds, maxSamples = MaxSamples,
        sampleCount = _samples.Count, droppedSamples = _dropped, captureFailures = _captureFailures,
        maxObservedProxyWorkingSetBytes = _samples.Count == 0 ? (long?)null : _samples.Max(x => x.Proxy.WorkingSetBytes),
        maxObservedDriverWorkingSetBytes = _samples.Count == 0 ? (long?)null : _samples.Max(x => x.Driver.WorkingSetBytes),
        samples = _samples.ToArray(),
        scope = "250ms best-effort process samples during dispatch/drain, bounded to 160 and 35 seconds. Sampled maxima are lower bounds, not true peaks. CPU is cumulative process CPU; timestamps are monotonic elapsed seconds. Driver includes sampling/relay/SDK/xUnit. No backend CPU or allocation attribution.",
    };

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_sampling is not null) await _sampling;
        _stop.Dispose();
    }
}

internal static class BatchWriteStageTelemetry
{
    public static readonly string[] Stages =
    [
        "envelope_parse", "metadata_read", "entry_parse", "put_validation", "delete_validation",
        "document_encode", "semaphore_wait", "item_downstream", "response_write",
    ];
    private const string Prefix = "aws2azure_batch_write_stage_seconds";

    public static async Task<Dictionary<string, double>?> ScrapeAsync(string endpoint)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        try { return Parse(await http.GetStringAsync(endpoint + "/_aws2azure/metrics")); }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }

    public static Dictionary<string, double> Parse(string text)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            foreach (var stage in Stages)
            {
                foreach (var suffix in new[] { "_sum", "_count" })
                {
                    var key = $"{Prefix}{suffix}{{stage=\"{stage}\"}}";
                    if (line.StartsWith(key + " ", StringComparison.Ordinal)
                        && double.TryParse(line.AsSpan(key.Length + 1), CultureInfo.InvariantCulture, out var value)
                        && double.IsFinite(value) && value >= 0)
                        result[key] = value;
                }
            }
        }
        return result;
    }

    public static object[] Delta(Dictionary<string, double>? before, Dictionary<string, double>? after) =>
        Stages.Select(stage =>
        {
            double? Difference(string suffix)
            {
                var key = $"{Prefix}{suffix}{{stage=\"{stage}\"}}";
                // No warmup series means unsupported/not exercised, never an invented zero.
                return before is not null && after is not null
                    && before.TryGetValue(key, out var first) && after.TryGetValue(key, out var last)
                    && last >= first ? last - first : null;
            }
            var seconds = Difference("_sum");
            var count = Difference("_count");
            return (object)new { stage, seconds, count, meanSeconds = count > 0 ? seconds / count : null,
                availability = seconds.HasValue && count.HasValue ? "measured" : "missing-or-not-exercised" };
        }).ToArray();
}
