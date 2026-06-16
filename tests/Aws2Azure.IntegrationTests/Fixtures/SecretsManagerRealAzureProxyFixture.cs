using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Amazon;
using Amazon.SecretsManager;
using Xunit;

namespace Aws2Azure.IntegrationTests.Fixtures;

/// <summary>
/// Boots the proxy as a real out-of-process Kestrel host pointing at a real
/// Azure Key Vault, then drives it with the official AWS SDK for Secrets
/// Manager. A real process (not <c>WebApplicationFactory</c>) is required
/// because the AWS SDK builds non-canonicalized request URIs for SigV4 that
/// the in-memory TestServer cannot route.
///
/// <para>Inert when the Key Vault URL or GitHub OIDC workload identity
/// environment variables are absent: the fixture skips process startup so the
/// tagged tests skip rather than fail on fork PRs and local runs without
/// real-Azure access.</para>
/// </summary>
public sealed class SecretsManagerRealAzureProxyFixture : IAsyncLifetime
{
    public const string AwsAccessKey = "AKIA-REAL-KEYVAULT";
    public const string AwsSecret = "smoke-secret";

    private const string ProxyHostName = "secretsmanager.127.0.0.1.nip.io";

    private readonly StringBuilder _proxyOutput = new();
    private Process? _proxyProcess;
    private string? _configFile;

    public bool Configured { get; private set; }
    public string? SkipReason { get; private set; }
    public string ProxyServiceUrl { get; private set; } = string.Empty;
    public string ProxyOutput => _proxyOutput.ToString();

    public async Task InitializeAsync()
    {
        var vaultUrl = Environment.GetEnvironmentVariable("AZURE_KEYVAULT_URL");

        if (string.IsNullOrWhiteSpace(vaultUrl)
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_TENANT_ID"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE")))
        {
            SkipReason = "AZURE_KEYVAULT_URL or workload identity env vars not set — skipping real-Azure Secrets Manager smoke.";
            Configured = false;
            return;
        }

        var proxyPort = GetFreePort();
        ProxyServiceUrl = $"http://{ProxyHostName}:{proxyPort}";
        _configFile = Path.Combine(AppContext.BaseDirectory, "secretsmanager-it-config-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(_configFile, $$"""
            {
              "services": {
                "secretsmanager": { "enabled": true }
              },
              "credentials": [
                {
                  "awsAccessKeyId": "{{AwsAccessKey}}",
                  "awsSecretAccessKey": "{{AwsSecret}}",
                  "azure": {
                    "keyVault": {
                      "vaultUrl": "{{vaultUrl}}",
                      "authMode": "WorkloadIdentity"
                    }
                  }
                }
              ]
            }
            """).ConfigureAwait(false);

        try
        {
            _proxyProcess = StartProxyProcess(proxyPort, _configFile);
            await WaitForProxyAsync(proxyPort, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            Configured = true;
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public AmazonSecretsManagerClient CreateSecretsManagerClient()
    {
        return new AmazonSecretsManagerClient(
            AwsAccessKey,
            AwsSecret,
            new AmazonSecretsManagerConfig
            {
                RegionEndpoint = RegionEndpoint.USEast1,
                ServiceURL = ProxyServiceUrl,
                UseHttp = true,
                AuthenticationRegion = "us-east-1",
            });
    }

    public async Task DisposeAsync()
    {
        if (_proxyProcess is not null)
        {
            try
            {
                if (!_proxyProcess.HasExited)
                {
                    _proxyProcess.Kill(entireProcessTree: true);
                    await _proxyProcess.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch
            {
            }

            _proxyProcess.Dispose();
            _proxyProcess = null;
        }

        if (_configFile is not null)
        {
            try { File.Delete(_configFile); } catch { }
            _configFile = null;
        }
    }

    private Process StartProxyProcess(int port, string configFile)
    {
        var repoRoot = FindRepoRoot();
        var startInfo = new ProcessStartInfo("dotnet", "run -c Release --project src/Aws2Azure.Proxy/Aws2Azure.Proxy.csproj --no-build --no-restore --no-launch-profile")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["AWS2AZURE_CONFIG_FILE"] = configFile;
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Testing";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => AppendOutput(args.Data);
        process.ErrorDataReceived += (_, args) => AppendOutput(args.Data);
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start aws2azure proxy process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private void AppendOutput(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        lock (_proxyOutput)
        {
            _proxyOutput.AppendLine(line);
        }
    }

    private async Task WaitForProxyAsync(int port, TimeSpan timeout)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_proxyProcess is { HasExited: true })
            {
                throw new InvalidOperationException(
                    "Proxy process exited before becoming ready:" + Environment.NewLine + _proxyOutput);
            }

            try
            {
                using var response = await client.GetAsync("/_aws2azure/health").ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for proxy on port {port}.{Environment.NewLine}{_proxyOutput}");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "aws2azure.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root for Secrets Manager integration fixture.");
    }
}

[CollectionDefinition(Name)]
public sealed class SecretsManagerRealAzureCollection : ICollectionFixture<SecretsManagerRealAzureProxyFixture>
{
    public const string Name = "secretsmanager-real-azure";
}
