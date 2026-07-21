using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;

namespace Aws2Azure.IntegrationTests.Fixtures;

/// <summary>
/// Boots the proxy as a real out-of-process Kestrel host pointing at a live,
/// isolated Azure Cosmos DB account, then drives it with the official AWS SDK
/// for DynamoDB. A dedicated (Cosmos-only) fixture — rather than the shared
/// multi-backend <see cref="RealAzureProxyFixture"/> used by the nightly
/// smoke — because sealed-runtime rollback qualification (issue #627) needs
/// its own <c>dynamodb-basic-crud</c> profile identity, independent of the
/// <c>s3-basic-object-crud</c> profile the shared fixture is pinned to.
///
/// <para>A real process (not <c>WebApplicationFactory</c>) is required
/// because the AWS SDK builds non-canonicalized request URIs for SigV4 that
/// the in-memory TestServer cannot route.</para>
///
/// <para>Inert when <c>AZURE_COSMOS_ENDPOINT/KEY/DATABASE</c> are absent: the
/// fixture skips process startup so the tagged tests skip rather than fail on
/// fork PRs and local runs without real-Azure access.</para>
/// </summary>
public sealed class DynamoDbRealAzureProxyFixture : IAsyncLifetime
{
    public const string AwsAccessKey = "AKIA-REAL-COSMOS-LOAD";
    public const string AwsSecret = "real-cosmos-load-secret";

    private const string AuthRegion = "us-east-1";
    private const string ProxyHostName = "dynamodb.127.0.0.1.nip.io";

    private readonly StringBuilder _proxyOutput = new();
    private Process? _proxyProcess;
    private string? _configFile;
    private string? _privateDirectory;
    private int _proxyPort;
    private SealedRuntimeSelection _runtimeSelection = null!;

    private string? _cosmosEndpoint;
    private string? _cosmosKey;
    private string? _cosmosDatabase;

    public bool CosmosConfigured { get; private set; }
    public bool ProxyStarted { get; private set; }
    public string ProxyServiceUrl => $"http://{ProxyHostName}:{_proxyPort}";
    public string ProxyOutput => _proxyOutput.ToString();
    public string CosmosEndpoint => _cosmosEndpoint ?? string.Empty;
    public string ProxyConfigDigest { get; private set; } = string.Empty;
    public string BackendIdentityDigest { get; private set; } = string.Empty;
    public string AwsBindingDigest { get; private set; } = string.Empty;
    public bool SealedCandidateConfigured => _runtimeSelection.IsSealed;
    public bool SealedRollbackConfigured => _runtimeSelection.RequiresRollback;
    public SealedRuntimeIdentity CandidateRuntimeIdentity =>
        _runtimeSelection.GetTarget(SealedRuntimeRole.Candidate).Identity;
    public SealedRuntimeIdentity PriorRuntimeIdentity =>
        _runtimeSelection.GetTarget(SealedRuntimeRole.Prior).Identity;

    public async Task InitializeAsync()
    {
        _runtimeSelection = SealedRuntimeSelection.Load("dynamodb-basic-crud", 1);
        _cosmosEndpoint = Env("AZURE_COSMOS_ENDPOINT");
        _cosmosKey = Env("AZURE_COSMOS_KEY");
        _cosmosDatabase = Env("AZURE_COSMOS_DATABASE");

        CosmosConfigured = !string.IsNullOrWhiteSpace(_cosmosEndpoint)
            && !string.IsNullOrWhiteSpace(_cosmosKey)
            && !string.IsNullOrWhiteSpace(_cosmosDatabase);
        if (!CosmosConfigured)
        {
            // No real-Azure Cosmos backend configured — every tagged test skips.
            return;
        }

        _proxyPort = GetFreePort();
        _privateDirectory = SealedRuntimeLauncher.CreatePrivateDirectory(
            AppContext.BaseDirectory,
            "dynamodb-load-it");
        _configFile = Path.Combine(_privateDirectory, "proxy-config.json");
        var configBytes = Encoding.UTF8.GetBytes(BuildConfigJson());
        await SealedRuntimeLauncher.WritePrivateFileAsync(_configFile, configBytes)
            .ConfigureAwait(false);
        ProxyConfigDigest = Digest(configBytes);
        BackendIdentityDigest = Digest(_cosmosEndpoint! + "\n" + _cosmosDatabase);
        AwsBindingDigest = Digest(AwsAccessKey + "\n" + AwsSecret);

        try
        {
            _proxyProcess = StartProxyProcess(
                _proxyPort,
                _configFile,
                SealedRuntimeRole.Candidate);
            await WaitForProxyAsync(_proxyPort, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            ProxyStarted = true;
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public AmazonDynamoDBClient CreateDynamoDbClient(int maxErrorRetry = 2) => new(
        AwsAccessKey, AwsSecret,
        new AmazonDynamoDBConfig
        {
            ServiceURL = ProxyServiceUrl,
            UseHttp = true,
            AuthenticationRegion = AuthRegion,
            MaxErrorRetry = maxErrorRetry,
        });

    public async Task RestartAsync()
    {
        if (!ProxyStarted || _configFile is null)
        {
            throw new InvalidOperationException("The real-Azure DynamoDB proxy is not running.");
        }

        await StopProxyAsync().ConfigureAwait(false);
        _proxyProcess = StartProxyProcess(
            _proxyPort,
            _configFile,
            SealedRuntimeRole.Candidate);
        await WaitForProxyAsync(_proxyPort, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        ProxyStarted = true;
    }

    public async Task StopForRuntimeSwitchAsync()
    {
        if (!ProxyStarted)
        {
            throw new InvalidOperationException("The real-Azure DynamoDB proxy is not running.");
        }
        await StopProxyAsync().ConfigureAwait(false);
    }

    public async Task StartRuntimeAsync(SealedRuntimeRole role)
    {
        if (_configFile is null || _proxyProcess is not null)
        {
            throw new InvalidOperationException("The real-Azure DynamoDB proxy cannot start a runtime now.");
        }
        if (role == SealedRuntimeRole.Prior && !_runtimeSelection.RequiresRollback)
        {
            throw new InvalidOperationException("No verified prior runtime is configured.");
        }

        _proxyProcess = StartProxyProcess(_proxyPort, _configFile, role);
        try
        {
            await WaitForProxyAsync(_proxyPort, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            ProxyStarted = true;
        }
        catch
        {
            await StopProxyAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        await StopProxyAsync().ConfigureAwait(false);

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

    private async Task StopProxyAsync()
    {
        if (_proxyProcess is null)
        {
            return;
        }

        await StopProcessAsync(_proxyProcess).ConfigureAwait(false);
        _proxyProcess = null;
        ProxyStarted = false;
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

    private string BuildConfigJson()
    {
        return $$"""
            {
              "services": {
                "dynamodb": { "enabled": true, "cosmosBinaryResponses": true, "cosmosBinaryRequests": true }
              },
              "bindings": [
                {
                  "aws": {
                    "accessKeyId": "{{AwsAccessKey}}",
                    "secretAccessKey": "{{AwsSecret}}"
                  },
                  "azure": {
                    "dynamodb": { "kind": "cosmos", "target": { "endpoint": "{{JsonEscape(_cosmosEndpoint!)}}", "databaseName": "{{JsonEscape(_cosmosDatabase!)}}" }, "auth": { "mode": "sharedKey", "key": "{{JsonEscape(_cosmosKey!)}}" } }
                  }
                }
              ]
            }
            """;
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string JsonEscape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private Process StartProxyProcess(
        int port,
        string configFile,
        SealedRuntimeRole runtimeRole)
    {
        var repoRoot = FindRepoRoot();
        var startInfo = SealedRuntimeLauncher.CreateStartInfo(
            _runtimeSelection,
            runtimeRole,
            repoRoot,
            port,
            configFile,
            new Dictionary<string, string?>());

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
            if (_proxyProcess!.HasExited)
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

        throw new InvalidOperationException("Could not locate repo root for real-Azure DynamoDB load fixture.");
    }

    private static string Digest(string value) =>
        Digest(Encoding.UTF8.GetBytes(value));

    private static string Digest(ReadOnlySpan<byte> value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(value));
}

[CollectionDefinition(Name)]
public sealed class DynamoDbRealAzureLoadCollection : ICollectionFixture<DynamoDbRealAzureProxyFixture>
{
    public const string Name = "dynamodb-real-azure-load";
}
