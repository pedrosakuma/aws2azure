using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    public const string InvalidBackendAwsAccessKey = "AKIA-REAL-KEYVAULT-INVALID";
    public const string InvalidBackendAwsSecret = "invalid-backend-secret";

    private const string ProxyHostName = "secretsmanager.127.0.0.1.nip.io";

    private readonly List<ProxyInstance> _instances = [];
    private readonly StringBuilder _completedOutput = new();
    private string? _configFile;
    private ProxyInstance? _defaultInstance;

    public bool Configured { get; private set; }
    public string? SkipReason { get; private set; }
    public string ProxyServiceUrl => _defaultInstance?.ServiceUrl ?? string.Empty;
    public string ProxyOutput
    {
        get
        {
            lock (_completedOutput)
            {
                return _completedOutput.ToString()
                       + string.Join(
                           Environment.NewLine,
                           _instances.Select(instance => instance.Output));
            }
        }
    }
    public string ProxyConfigDigest { get; private set; } = string.Empty;
    public ProxyInstance DefaultInstance => _defaultInstance
        ?? throw new InvalidOperationException("The default real-Azure proxy is not running.");

    public async Task InitializeAsync()
    {
        var vaultUrl = Environment.GetEnvironmentVariable("AZURE_KEYVAULT_URL");
        var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");

        if (string.IsNullOrWhiteSpace(vaultUrl)
            || string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE")))
        {
            SkipReason = "AZURE_KEYVAULT_URL or workload identity env vars not set — skipping real-Azure Secrets Manager smoke.";
            Configured = false;
            return;
        }

        _configFile = Path.Combine(AppContext.BaseDirectory, "secretsmanager-it-config-" + Guid.NewGuid().ToString("N") + ".json");
        var configBytes = Encoding.UTF8.GetBytes($$"""
            {
              "services": {
                "secretsmanager": { "enabled": true }
              },
              "bindings": [
                {
                  "aws": {
                    "accessKeyId": "{{AwsAccessKey}}",
                    "secretAccessKey": "{{AwsSecret}}"
                  },
                  "azure": {
                    "secretsmanager": {
                      "kind": "keyVault",
                      "target": {
                        "vaultUrl": "{{vaultUrl}}"
                      },
                      "auth": {
                        "mode": "WorkloadIdentity"
                      }
                    }
                  }
                },
                {
                  "aws": {
                    "accessKeyId": "{{InvalidBackendAwsAccessKey}}",
                    "secretAccessKey": "{{InvalidBackendAwsSecret}}"
                  },
                  "azure": {
                    "secretsmanager": {
                      "kind": "keyVault",
                      "target": {
                        "vaultUrl": "{{vaultUrl}}"
                      },
                      "auth": {
                        "mode": "clientSecret",
                        "tenantId": "{{tenantId}}",
                        "clientId": "{{clientId}}",
                        "clientSecret": "aws2azure-deterministic-invalid-client-secret"
                      }
                    }
                  }
                }
              ]
            }
            """);
        await File.WriteAllBytesAsync(_configFile, configBytes).ConfigureAwait(false);
        ProxyConfigDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(configBytes));

        try
        {
            _defaultInstance = await StartProxyInstanceAsync(
                clientId,
                Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE")!)
                .ConfigureAwait(false);
            Configured = true;
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public AmazonSecretsManagerClient CreateSecretsManagerClient(
        string? serviceUrl = null,
        int maxErrorRetry = 2)
    {
        return new AmazonSecretsManagerClient(
            AwsAccessKey,
            AwsSecret,
            new AmazonSecretsManagerConfig
            {
                RegionEndpoint = RegionEndpoint.USEast1,
                ServiceURL = serviceUrl ?? ProxyServiceUrl,
                UseHttp = true,
                AuthenticationRegion = "us-east-1",
                MaxErrorRetry = maxErrorRetry,
            });
    }

    public async Task<ProxyInstance> StartProxyInstanceAsync(
        string clientId,
        string federatedTokenFile)
    {
        if (_configFile is null)
        {
            throw new InvalidOperationException("The real-Azure Secrets Manager proxy is not configured.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(federatedTokenFile);

        var port = GetFreePort();
        var instance = new ProxyInstance(
            StartProxyProcess(port, _configFile, clientId, federatedTokenFile),
            $"http://{ProxyHostName}:{port}");
        _instances.Add(instance);
        try
        {
            await WaitForProxyAsync(instance, port, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            return instance;
        }
        catch
        {
            await StopProxyInstanceAsync(instance).ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopProxyInstanceAsync(ProxyInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        await StopProcessAsync(instance.Process).ConfigureAwait(false);
        lock (_completedOutput)
        {
            _completedOutput.Append(instance.Output);
        }
        _instances.Remove(instance);
        if (ReferenceEquals(_defaultInstance, instance))
        {
            _defaultInstance = null;
        }
    }

    public void PromoteToDefault(ProxyInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!_instances.Contains(instance))
        {
            throw new InvalidOperationException("Only a running fixture instance can become default.");
        }
        _defaultInstance = instance;
    }

    public bool IsDefault(ProxyInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return ReferenceEquals(_defaultInstance, instance);
    }

    public async Task RestartAsync()
    {
        if (!Configured || _configFile is null)
        {
            throw new InvalidOperationException("The real-Azure Secrets Manager proxy is not running.");
        }

        var current = _defaultInstance
            ?? throw new InvalidOperationException("The default real-Azure proxy is not running.");
        var port = new Uri(current.ServiceUrl).Port;
        var clientId = RequiredEnvironment("AZURE_CLIENT_ID");
        var tokenFile = RequiredEnvironment("AZURE_FEDERATED_TOKEN_FILE");
        await StopProxyInstanceAsync(current).ConfigureAwait(false);
        var replacement = new ProxyInstance(
            StartProxyProcess(port, _configFile, clientId, tokenFile),
            $"http://{ProxyHostName}:{port}");
        _instances.Add(replacement);
        _defaultInstance = replacement;
        await WaitForProxyAsync(replacement, port, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        foreach (var instance in _instances.ToArray())
        {
            await StopProxyInstanceAsync(instance).ConfigureAwait(false);
        }

        if (_configFile is not null)
        {
            try { File.Delete(_configFile); } catch { }
            _configFile = null;
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch
        {
        }

        process.Dispose();
    }

    private static Process StartProxyProcess(
        int port,
        string configFile,
        string clientId,
        string federatedTokenFile)
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
        startInfo.Environment["AZURE_CLIENT_ID"] = clientId;
        startInfo.Environment["AZURE_FEDERATED_TOKEN_FILE"] = federatedTokenFile;
        startInfo.Environment.Remove("ACTIONS_ID_TOKEN_REQUEST_TOKEN");
        startInfo.Environment.Remove("ACTIONS_ID_TOKEN_REQUEST_URL");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start aws2azure proxy process.");
        }

        return process;
    }

    private static async Task WaitForProxyAsync(
        ProxyInstance instance,
        int port,
        TimeSpan timeout)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (instance.Process.HasExited)
            {
                throw new InvalidOperationException(
                    "Proxy process exited before becoming ready:" + Environment.NewLine + instance.Output);
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

        throw new TimeoutException($"Timed out waiting for proxy on port {port}.{Environment.NewLine}{instance.Output}");
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

    private static string RequiredEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} is required.")
            : value;
    }

    public sealed class ProxyInstance
    {
        private readonly StringBuilder _output = new();

        internal ProxyInstance(Process process, string serviceUrl)
        {
            Process = process;
            ServiceUrl = serviceUrl;
            process.OutputDataReceived += (_, args) => AppendOutput(args.Data);
            process.ErrorDataReceived += (_, args) => AppendOutput(args.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        internal Process Process { get; }
        public string ServiceUrl { get; }
        public string Output
        {
            get
            {
                lock (_output)
                {
                    return _output.ToString();
                }
            }
        }

        private void AppendOutput(string? line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }
            lock (_output)
            {
                _output.AppendLine(line);
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SecretsManagerRealAzureCollection : ICollectionFixture<SecretsManagerRealAzureProxyFixture>
{
    public const string Name = "secretsmanager-real-azure";
}
