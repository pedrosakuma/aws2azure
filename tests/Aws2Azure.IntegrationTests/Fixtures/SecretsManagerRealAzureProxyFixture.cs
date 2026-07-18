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
using Aws2Azure.TestSupport.OperationalQualification;
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
    private string? _privateDirectory;
    private ProxyInstance? _defaultInstance;
    private SealedRuntimeSelection _runtimeSelection = null!;
    private string? _switchClientId;
    private string? _switchFederatedTokenFile;
    private int _switchPort;

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
    public string BackendIdentityDigest { get; private set; } = string.Empty;
    public string AwsBindingDigest { get; private set; } = string.Empty;
    public bool SealedCandidateConfigured => _runtimeSelection.IsSealed;
    public bool SealedRollbackConfigured => _runtimeSelection.RequiresRollback;
    public bool HasDefaultInstance => _defaultInstance is not null;
    public SealedRuntimeIdentity CandidateRuntimeIdentity =>
        _runtimeSelection.GetTarget(SealedRuntimeRole.Candidate).Identity;
    public SealedRuntimeIdentity PriorRuntimeIdentity =>
        _runtimeSelection.GetTarget(SealedRuntimeRole.Prior).Identity;
    public ProxyInstance DefaultInstance => _defaultInstance
        ?? throw new InvalidOperationException("The default real-Azure proxy is not running.");

    public async Task InitializeAsync()
    {
        _runtimeSelection = SealedRuntimeSelection.Load(
            "secretsmanager-basic-lifecycle",
            1);
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

        _privateDirectory = SealedRuntimeLauncher.CreatePrivateDirectory(
            AppContext.BaseDirectory,
            "secretsmanager-it");
        _configFile = Path.Combine(_privateDirectory, "proxy-config.json");
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
        await SealedRuntimeLauncher.WritePrivateFileAsync(_configFile, configBytes)
            .ConfigureAwait(false);
        ProxyConfigDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(configBytes));
        BackendIdentityDigest = Digest(vaultUrl);
        AwsBindingDigest = Digest(AwsAccessKey + "\n" + AwsSecret);

        try
        {
            _defaultInstance = await StartProxyInstanceCoreAsync(
                clientId,
                Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE")!,
                SealedRuntimeRole.Candidate,
                port: null)
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
        return await StartProxyInstanceCoreAsync(
            clientId,
            federatedTokenFile,
            SealedRuntimeRole.Candidate,
            port: null).ConfigureAwait(false);
    }

    private async Task<ProxyInstance> StartProxyInstanceCoreAsync(
        string clientId,
        string federatedTokenFile,
        SealedRuntimeRole runtimeRole,
        int? port)
    {
        if (_configFile is null)
        {
            throw new InvalidOperationException("The real-Azure Secrets Manager proxy is not configured.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(federatedTokenFile);

        var selectedPort = port ?? GetFreePort();
        var instance = new ProxyInstance(
            StartProxyProcess(
                selectedPort,
                _configFile,
                clientId,
                federatedTokenFile,
                runtimeRole),
            $"http://{ProxyHostName}:{selectedPort}",
            clientId,
            federatedTokenFile,
            runtimeRole);
        _instances.Add(instance);
        try
        {
            await WaitForProxyAsync(instance, selectedPort, TimeSpan.FromMinutes(2))
                .ConfigureAwait(false);
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
        var replacement = await StartProxyInstanceCoreAsync(
            clientId,
            tokenFile,
            SealedRuntimeRole.Candidate,
            port).ConfigureAwait(false);
        _defaultInstance = replacement;
    }

    public async Task StopForRuntimeSwitchAsync()
    {
        var current = _defaultInstance
            ?? throw new InvalidOperationException("The default real-Azure proxy is not running.");
        _switchClientId = current.ClientId;
        _switchFederatedTokenFile = current.FederatedTokenFile;
        _switchPort = new Uri(current.ServiceUrl).Port;
        await StopProxyInstanceAsync(current).ConfigureAwait(false);
    }

    public async Task StartRuntimeAsync(SealedRuntimeRole role)
    {
        if (_defaultInstance is not null
            || string.IsNullOrWhiteSpace(_switchClientId)
            || string.IsNullOrWhiteSpace(_switchFederatedTokenFile)
            || _switchPort <= 0)
        {
            throw new InvalidOperationException(
                "The Secrets Manager fixture has no stopped runtime to replace.");
        }
        if (role == SealedRuntimeRole.Prior && !_runtimeSelection.RequiresRollback)
        {
            throw new InvalidOperationException("No verified prior runtime is configured.");
        }

        _defaultInstance = await StartProxyInstanceCoreAsync(
            _switchClientId,
            _switchFederatedTokenFile,
            role,
            _switchPort).ConfigureAwait(false);
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
        if (_privateDirectory is not null)
        {
            try { Directory.Delete(_privateDirectory, recursive: true); } catch { }
            _privateDirectory = null;
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

    private Process StartProxyProcess(
        int port,
        string configFile,
        string clientId,
        string federatedTokenFile,
        SealedRuntimeRole runtimeRole)
    {
        var repoRoot = FindRepoRoot();
        var startInfo = SealedRuntimeLauncher.CreateStartInfo(
            _runtimeSelection,
            runtimeRole,
            repoRoot,
            port,
            configFile,
            new Dictionary<string, string?>
            {
                ["AZURE_TENANT_ID"] = RequiredEnvironment("AZURE_TENANT_ID"),
                ["AZURE_CLIENT_ID"] = clientId,
                ["AZURE_FEDERATED_TOKEN_FILE"] = federatedTokenFile,
                ["AZURE_AUTHORITY_HOST"] =
                    Environment.GetEnvironmentVariable("AZURE_AUTHORITY_HOST"),
            });

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

    private static string Digest(string value) =>
        "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public sealed class ProxyInstance
    {
        private readonly StringBuilder _output = new();

        internal ProxyInstance(
            Process process,
            string serviceUrl,
            string clientId,
            string federatedTokenFile,
            SealedRuntimeRole runtimeRole)
        {
            Process = process;
            ServiceUrl = serviceUrl;
            ClientId = clientId;
            FederatedTokenFile = federatedTokenFile;
            RuntimeRole = runtimeRole;
            process.OutputDataReceived += (_, args) => AppendOutput(args.Data);
            process.ErrorDataReceived += (_, args) => AppendOutput(args.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        internal Process Process { get; }
        internal string ClientId { get; }
        internal string FederatedTokenFile { get; }
        public SealedRuntimeRole RuntimeRole { get; }
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
