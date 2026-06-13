using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Aws2Azure.FootprintTests;

/// <summary>
/// One running instance of a published proxy binary, bound to a free loopback
/// port with a placeholder config. Owns the process + temp config lifecycle and
/// exposes readiness polling and a resident-set sampler.
/// </summary>
internal sealed class ProxyInstance : IDisposable
{
    private readonly Process _process;
    private readonly string _configFile;
    private readonly int _port;
    private readonly StringBuilder _output = new();

    private ProxyInstance(Process process, string configFile, int port)
    {
        _process = process;
        _configFile = configFile;
        _port = port;
    }

    public string Output { get { lock (_output) return _output.ToString(); } }
    public bool HasExited => _process.HasExited;
    public int Pid => _process.Id;

    /// <summary>
    /// Starts the binary and returns immediately (before readiness). The caller
    /// times <see cref="WaitForHealthyAsync"/> to measure cold start.
    /// </summary>
    public static ProxyInstance Start(string binaryPath, string configJson)
    {
        var port = GetFreePort();
        var configFile = Path.Combine(Path.GetTempPath(),
            "footprint-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(configFile, configJson);

        var startInfo = new ProcessStartInfo(binaryPath)
        {
            WorkingDirectory = Path.GetDirectoryName(binaryPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["AWS2AZURE_CONFIG_FILE"] = configFile;
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var instance = new ProxyInstance(process, configFile, port);
        process.OutputDataReceived += (_, e) => instance.Append(e.Data);
        process.ErrorDataReceived += (_, e) => instance.Append(e.Data);
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start published proxy binary.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return instance;
    }

    /// <summary>Polls <c>/_aws2azure/health</c> until it returns 200 or the timeout elapses.</summary>
    public async Task WaitForHealthyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var url = $"http://127.0.0.1:{_port}/_aws2azure/health";
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"Proxy exited during startup.\n{Output}");
            }
            try
            {
                using var resp = await http.GetAsync(url).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return;
            }
            catch { /* not ready yet */ }
            await Task.Delay(5).ConfigureAwait(false);
        }
        throw new TimeoutException($"Proxy did not become healthy within {timeout}.\n{Output}");
    }

    /// <summary>
    /// Resident set size of the proxy process in bytes. Reads <c>VmRSS</c> from
    /// <c>/proc/&lt;pid&gt;/status</c> on Linux (canonical RSS); falls back to the
    /// process working set elsewhere.
    /// </summary>
    public long ReadRssBytes()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var rss = TryReadProcRss(_process.Id);
            if (rss > 0) return rss;
        }
        try { _process.Refresh(); return _process.WorkingSet64; }
        catch { return 0; }
    }

    private static long TryReadProcRss(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (!line.StartsWith("VmRSS:", StringComparison.Ordinal)) continue;
                var span = line.AsSpan("VmRSS:".Length).Trim();
                var space = span.IndexOf(' ');
                var num = space > 0 ? span[..space] : span;
                if (long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                {
                    return kb * 1024; // status reports kB
                }
            }
        }
        catch { /* process may have exited */ }
        return 0;
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

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch { /* best-effort */ }
        _process.Dispose();
        try { File.Delete(_configFile); } catch { /* best-effort */ }
    }
}
