using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Starts <c>Aws2Azure.Proxy</c> as an out-of-process Kestrel server bound
/// to a free local port so AWS SDK clients (which require a real
/// <c>ServiceURL</c>) can hit it. Mirrors the pattern used by
/// <c>Aws2Azure.IntegrationTests.Fixtures.KinesisEmulatorProxyFixture</c>.
///
/// <para>The caller supplies the rendered <c>appsettings.json</c> body —
/// this class only owns the file lifecycle, the process, and readiness.</para>
/// </summary>
internal sealed class PerfProxyProcess : IAsyncDisposable
{
    private readonly StringBuilder _output = new();
    private Process? _process;
    private string? _configFile;

    public string ServiceUrl { get; private set; } = string.Empty;
    public string Output => _output.ToString();

    /// <summary>
    /// Returns the proxy URL with the loopback IP replaced by a per-service
    /// nip.io subdomain, e.g. <c>http://sqs.127.0.0.1.nip.io:&lt;port&gt;</c>.
    /// Routes through the proxy's host-header matcher so each service module
    /// is selected by name. Same pattern the Kinesis IT fixture uses.
    /// </summary>
    public string ServiceUrlForHost(string hostPrefix)
        => ServiceUrl.Replace("127.0.0.1", $"{hostPrefix}.127.0.0.1.nip.io", StringComparison.Ordinal);

    /// <summary>
    /// Creates a memory probe bound to this proxy's loopback base URL (#274).
    /// The runtime gauges are served from a host-agnostic top-level endpoint, so
    /// the raw <see cref="ServiceUrl"/> is used rather than a nip.io host alias.
    /// Caller owns disposal.
    /// </summary>
    public ProxyMemoryProbe CreateMemoryProbe() => new(ServiceUrl);

    public async Task StartAsync(string configJson, TimeSpan readinessTimeout)
    {
        var port = GetFreePort();
        ServiceUrl = $"http://127.0.0.1:{port}";

        _configFile = Path.Combine(AppContext.BaseDirectory,
            "perf-proxy-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(_configFile, configJson).ConfigureAwait(false);

        var repoRoot = FindRepoRoot();

        // Optionally pin the proxy process tree to a fixed number of logical
        // cores (AWS2AZURE_PERF_PROXY_CPUS=N), modelling a CPU-constrained
        // sidecar so a real-Azure run can reach a CPU-bound regime at modest
        // concurrency (issue #420 Tier 2). taskset affinity is inherited by the
        // dotnet child that actually hosts Kestrel. Linux-only; ignored when
        // unset or unparsable so the default (emulator/local) path is untouched.
        var fileName = "dotnet";
        var arguments =
            "run -c Release --project src/Aws2Azure.Proxy/Aws2Azure.Proxy.csproj --no-launch-profile";
        var cpus = Environment.GetEnvironmentVariable("AWS2AZURE_PERF_PROXY_CPUS");
        if (OperatingSystem.IsLinux()
            && int.TryParse(cpus, out var coreCount) && coreCount > 0)
        {
            var coreList = coreCount == 1 ? "0" : $"0-{coreCount - 1}";
            fileName = "taskset";
            arguments = $"-c {coreList} dotnet {arguments}";
        }

        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["AWS2AZURE_CONFIG_FILE"] = _configFile;
        startInfo.Environment["ASPNETCORE_URLS"] = ServiceUrl;
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Perf";

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => Append(e.Data);
        _process.ErrorDataReceived += (_, e) => Append(e.Data);
        if (!_process.Start())
        {
            throw new InvalidOperationException("Failed to start aws2azure proxy process.");
        }
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitForReadyAsync(port, readinessTimeout).ConfigureAwait(false);
    }

    private async Task WaitForReadyAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"Proxy exited during startup. Output:\n{Output}");
            }
            try
            {
                using var resp = await http.GetAsync($"http://127.0.0.1:{port}/_aws2azure/health")
                    .ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return;
            }
            catch { /* not ready yet */ }
            await Task.Delay(250).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Proxy did not become ready on {ServiceUrl} within {timeout}. Output:\n{Output}");
    }

    private void Append(string? line)
    {
        if (line is null) return;
        lock (_output) _output.AppendLine(line);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "src")) &&
                File.Exists(Path.Combine(dir, "aws2azure.slnx")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate aws2azure repo root from " + AppContext.BaseDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch { /* best-effort */ }
            _process.Dispose();
            _process = null;
        }
        if (_configFile is not null)
        {
            try { File.Delete(_configFile); } catch { /* best-effort */ }
            _configFile = null;
        }
    }
}
