using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Kinesis;
using Amazon.S3;
using Amazon.SQS;
using Xunit;

namespace Aws2Azure.IntegrationTests.Fixtures;

/// <summary>
/// Boots the proxy as a real out-of-process Kestrel host pointing at live
/// Azure data planes (Blob, Cosmos DB, Service Bus, Event Hubs) and drives it
/// with the official AWS SDKs. Backs the nightly <c>integration-real-azure</c>
/// workflow (issue #153): emulators are necessary but not sufficient, so a
/// scheduled real-Azure CRUD smoke catches divergences (auth, default ports,
/// throttling) that Azurite / Service Bus emulator hide.
///
/// <para>Each backend is independently optional. A service is enabled in the
/// generated config (and its tagged tests run) only when its credentials are
/// present in the environment; otherwise that service's tests skip cleanly —
/// fork PRs and local <c>dotnet test</c> runs without secrets stay green. When
/// no backend at all is configured the proxy process is never started.</para>
///
/// <para>A real process (not <c>WebApplicationFactory</c>) is required because
/// the AWS SDKs build non-canonicalized request URIs for SigV4 that the
/// in-memory TestServer cannot route. Host-header multiplexing routes each
/// SDK to its module via <c>&lt;service&gt;.127.0.0.1.nip.io</c>.</para>
/// </summary>
public sealed class RealAzureProxyFixture : IAsyncLifetime
{
    public const string AwsAccessKey = "AKIA-REAL-AZURE-NIGHTLY";
    public const string AwsSecret = "real-azure-nightly-secret";

    // A second AWS identity whose AAD-capable backends authenticate via Workload
    // Identity instead of shared keys (issue #307). It lives in the same proxy
    // config as a separate credential entry, so one proxy process serves both the
    // shared-key and the Workload-Identity smoke side by side.
    public const string WiAwsAccessKey = "AKIA-REAL-AZURE-NIGHTLY-WI";
    public const string WiAwsSecret = "real-azure-nightly-wi-secret";

    private const string AuthRegion = "us-east-1";

    private readonly StringBuilder _proxyOutput = new();
    private Process? _proxyProcess;
    private string? _configFile;
    private int _proxyPort;

    // Parsed backend settings, populated from environment in InitializeAsync.
    private string? _blobAccount;
    private string? _blobKey;
    private string? _blobEndpoint;

    private string? _cosmosEndpoint;
    private string? _cosmosKey;
    private string? _cosmosDatabase;

    private string? _sbNamespace;
    private string? _sbSasKeyName;
    private string? _sbSasKey;

    private string? _ehNamespace;
    private string? _ehSasKeyName;
    private string? _ehSasKey;

    // Workload Identity (issue #307): the GitHub Actions OIDC token is minted to
    // a file and these env vars point the proxy's WorkloadIdentityTokenSource at
    // it. AAD-capable backends (Cosmos, Event Hubs) can then authenticate without
    // shared keys.
    private string? _federatedTokenFile;
    private string? _aadTenantId;
    private string? _aadClientId;

    /// <summary>True when at least one backend was configured and the proxy started.</summary>
    public bool ProxyStarted { get; private set; }

    /// <summary>S3 → Azure Blob Storage backend configured.</summary>
    public bool BlobConfigured { get; private set; }

    /// <summary>DynamoDB → Cosmos DB backend configured.</summary>
    public bool CosmosConfigured { get; private set; }

    /// <summary>SQS → Service Bus backend configured.</summary>
    public bool ServiceBusConfigured { get; private set; }

    /// <summary>Kinesis → Event Hubs backend configured.</summary>
    public bool EventHubsConfigured { get; private set; }

    /// <summary>
    /// Workload Identity federation is available (the OIDC token file plus
    /// tenant/client ids are present). Gates the Workload-Identity E2E smoke,
    /// which is testable on GitHub-hosted runners (unlike Managed Identity, which
    /// needs IMDS).
    /// </summary>
    public bool WorkloadIdentityConfigured { get; private set; }

    /// <summary>
    /// DynamoDB → Cosmos DB reachable via the Workload-Identity credential entry
    /// (Cosmos endpoint/database present and federation configured).
    /// </summary>
    public bool CosmosWorkloadIdentityConfigured { get; private set; }

    /// <summary>
    /// Kinesis → Event Hubs reachable via the Workload-Identity credential entry
    /// (namespace + stream present and federation configured).
    /// </summary>
    public bool EventHubsWorkloadIdentityConfigured { get; private set; }

    /// <summary>
    /// Pre-existing Cosmos database (the DynamoDB module creates containers but
    /// not the database). Operators provision it once; the test creates and
    /// deletes tables (containers) inside it.
    /// </summary>
    public string CosmosDatabase => _cosmosDatabase ?? string.Empty;

    /// <summary>Live Cosmos DB account endpoint (<c>AZURE_COSMOS_ENDPOINT</c>),
    /// exposed so tests can issue raw authenticated REST probes (e.g. asserting
    /// real Azure emits CosmosBinary bodies, which the CI emulator does not).</summary>
    public string CosmosEndpoint => _cosmosEndpoint ?? string.Empty;

    /// <summary>Cosmos DB account master key (<c>AZURE_COSMOS_KEY</c>) for raw
    /// REST probes. Empty under Workload-Identity-only runs.</summary>
    public string CosmosMasterKey => _cosmosKey ?? string.Empty;

    /// <summary>Prometheus metrics scrape URL for the running proxy
    /// (<c>/_aws2azure/metrics</c>, path-routed, not host-routed). Lets tests
    /// assert production counters such as the GetItem decode-path metric.</summary>
    public string MetricsUrl => $"http://127.0.0.1:{_proxyPort}/_aws2azure/metrics";

    /// <summary>
    /// Pre-existing Event Hubs entity backing the Kinesis stream (CreateStream
    /// is not implemented; the hub must already exist). The PutRecord smoke
    /// targets this stream name.
    /// </summary>
    public string EventHubStream { get; private set; } = string.Empty;

    public string ProxyOutput => _proxyOutput.ToString();

    public async Task InitializeAsync()
    {
        ReadEnvironment();

        BlobConfigured = !string.IsNullOrWhiteSpace(_blobAccount) && !string.IsNullOrWhiteSpace(_blobKey);
        CosmosConfigured = !string.IsNullOrWhiteSpace(_cosmosEndpoint) && !string.IsNullOrWhiteSpace(_cosmosKey)
            && !string.IsNullOrWhiteSpace(_cosmosDatabase);
        ServiceBusConfigured = !string.IsNullOrWhiteSpace(_sbNamespace) && !string.IsNullOrWhiteSpace(_sbSasKeyName)
            && !string.IsNullOrWhiteSpace(_sbSasKey);
        EventHubsConfigured = !string.IsNullOrWhiteSpace(_ehNamespace) && !string.IsNullOrWhiteSpace(_ehSasKeyName)
            && !string.IsNullOrWhiteSpace(_ehSasKey) && !string.IsNullOrWhiteSpace(EventHubStream);

        WorkloadIdentityConfigured = !string.IsNullOrWhiteSpace(_federatedTokenFile)
            && !string.IsNullOrWhiteSpace(_aadTenantId) && !string.IsNullOrWhiteSpace(_aadClientId);
        CosmosWorkloadIdentityConfigured = WorkloadIdentityConfigured
            && !string.IsNullOrWhiteSpace(_cosmosEndpoint) && !string.IsNullOrWhiteSpace(_cosmosDatabase);
        EventHubsWorkloadIdentityConfigured = WorkloadIdentityConfigured
            && !string.IsNullOrWhiteSpace(_ehNamespace) && !string.IsNullOrWhiteSpace(EventHubStream);

        if (!BlobConfigured && !CosmosConfigured && !ServiceBusConfigured && !EventHubsConfigured
            && !CosmosWorkloadIdentityConfigured && !EventHubsWorkloadIdentityConfigured)
        {
            // No real-Azure backend configured — every tagged test skips.
            return;
        }

        _proxyPort = GetFreePort();
        _configFile = Path.Combine(AppContext.BaseDirectory,
            "real-azure-it-config-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(_configFile, BuildConfigJson()).ConfigureAwait(false);

        try
        {
            _proxyProcess = StartProxyProcess(_proxyPort, _configFile);
            await WaitForProxyAsync(_proxyPort, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            ProxyStarted = true;
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public AmazonS3Client CreateS3Client() => new(
        AwsAccessKey, AwsSecret,
        new AmazonS3Config
        {
            ServiceURL = ServiceUrlFor("s3"),
            ForcePathStyle = true,
            UseHttp = true,
            AuthenticationRegion = AuthRegion,
            // Intentionally left at AWSSDK.S3 4.x defaults so the smoke exercises
            // the modern STREAMING-…-PAYLOAD-TRAILER chunked upload path the proxy
            // now decodes (issue #258).
        });

    public AmazonDynamoDBClient CreateDynamoDbClient() => new(
        AwsAccessKey, AwsSecret,
        new AmazonDynamoDBConfig
        {
            ServiceURL = ServiceUrlFor("dynamodb"),
            UseHttp = true,
            AuthenticationRegion = AuthRegion,
        });

    public AmazonSQSClient CreateSqsClient() => new(
        AwsAccessKey, AwsSecret,
        new AmazonSQSConfig
        {
            ServiceURL = ServiceUrlFor("sqs"),
            UseHttp = true,
            AuthenticationRegion = AuthRegion,
        });

    public AmazonKinesisClient CreateKinesisClient() => new(
        AwsAccessKey, AwsSecret,
        new AmazonKinesisConfig
        {
            ServiceURL = ServiceUrlFor("kinesis"),
            UseHttp = true,
            AuthenticationRegion = AuthRegion,
        });

    /// <summary>
    /// DynamoDB client signing with the Workload-Identity AWS credential, so its
    /// requests resolve to the Cosmos backend that authenticates via federated
    /// token instead of a shared key.
    /// </summary>
    public AmazonDynamoDBClient CreateDynamoDbClientWorkloadIdentity() => new(
        WiAwsAccessKey, WiAwsSecret,
        new AmazonDynamoDBConfig
        {
            ServiceURL = ServiceUrlFor("dynamodb"),
            UseHttp = true,
            AuthenticationRegion = AuthRegion,
        });

    /// <summary>
    /// Kinesis client signing with the Workload-Identity AWS credential, so its
    /// requests resolve to the Event Hubs backend that authenticates via
    /// federated token instead of a SAS key.
    /// </summary>
    public AmazonKinesisClient CreateKinesisClientWorkloadIdentity() => new(
        WiAwsAccessKey, WiAwsSecret,
        new AmazonKinesisConfig
        {
            ServiceURL = ServiceUrlFor("kinesis"),
            UseHttp = true,
            AuthenticationRegion = AuthRegion,
        });

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

    private void ReadEnvironment()
    {
        _blobAccount = Env("AZURE_BLOB_ACCOUNT");
        _blobKey = Env("AZURE_BLOB_KEY");
        _blobEndpoint = Env("AZURE_BLOB_ENDPOINT");

        _cosmosEndpoint = Env("AZURE_COSMOS_ENDPOINT");
        _cosmosKey = Env("AZURE_COSMOS_KEY");
        _cosmosDatabase = Env("AZURE_COSMOS_DATABASE");

        // Service Bus is supplied as a connection string (the same
        // AZURE_SB_CONNSTR secret the SQS real-Azure job already consumes).
        var sbConn = Env("AZURE_SB_CONNSTR");
        if (!string.IsNullOrWhiteSpace(sbConn))
        {
            var (ns, keyName, key) = ParseSasConnectionString(sbConn);
            _sbNamespace = ns;
            _sbSasKeyName = keyName;
            _sbSasKey = key;
        }

        // Event Hubs: prefer a connection string, else discrete fields.
        var ehConn = Env("AZURE_EVENTHUBS_CONNSTR");
        if (!string.IsNullOrWhiteSpace(ehConn))
        {
            var (ns, keyName, key) = ParseSasConnectionString(ehConn);
            _ehNamespace = ns;
            _ehSasKeyName = keyName;
            _ehSasKey = key;
        }
        else
        {
            _ehNamespace = Env("AZURE_EVENTHUBS_NAMESPACE");
            _ehSasKeyName = Env("AZURE_EVENTHUBS_SAS_KEYNAME");
            _ehSasKey = Env("AZURE_EVENTHUBS_SAS_KEY");
        }

        EventHubStream = Env("AZURE_EVENTHUBS_STREAM") ?? string.Empty;

        _federatedTokenFile = Env("AZURE_FEDERATED_TOKEN_FILE");
        _aadTenantId = Env("AZURE_TENANT_ID");
        _aadClientId = Env("AZURE_CLIENT_ID");
    }

    private string BuildConfigJson()
    {
        var services = new StringBuilder();
        var azure = new StringBuilder();
        AppendService(services, "s3", BlobConfigured);
        AppendService(services, "dynamodb", CosmosConfigured || CosmosWorkloadIdentityConfigured);
        AppendService(services, "sqs", ServiceBusConfigured);
        AppendService(services, "kinesis", EventHubsConfigured || EventHubsWorkloadIdentityConfigured);

        if (BlobConfigured)
        {
            var endpoint = string.IsNullOrWhiteSpace(_blobEndpoint)
                ? string.Empty
                : $$""", "serviceEndpoint": "{{JsonEscape(_blobEndpoint!)}}" """;
            AppendAzure(azure, $$"""
                "blob": { "accountName": "{{JsonEscape(_blobAccount!)}}", "accountKey": "{{JsonEscape(_blobKey!)}}"{{endpoint}} }
                """);
        }

        if (CosmosConfigured)
        {
            AppendAzure(azure, $$"""
                "cosmos": { "endpoint": "{{JsonEscape(_cosmosEndpoint!)}}", "primaryKey": "{{JsonEscape(_cosmosKey!)}}", "databaseName": "{{JsonEscape(_cosmosDatabase!)}}" }
                """);
        }

        if (ServiceBusConfigured)
        {
            AppendAzure(azure, $$"""
                "serviceBus": { "namespace": "{{JsonEscape(_sbNamespace!)}}", "sasKeyName": "{{JsonEscape(_sbSasKeyName!)}}", "sasKey": "{{JsonEscape(_sbSasKey!)}}", "transport": "Rest" }
                """);
        }

        if (EventHubsConfigured)
        {
            AppendAzure(azure, $$"""
                "eventHubs": { "namespace": "{{JsonEscape(_ehNamespace!)}}", "sasKeyName": "{{JsonEscape(_ehSasKeyName!)}}", "sasKey": "{{JsonEscape(_ehSasKey!)}}" }
                """);
        }

        // Second credential entry: AAD-capable backends authenticated via Workload
        // Identity (no shared key/SAS — the proxy reads the federated token file).
        var wiAzure = new StringBuilder();
        if (CosmosWorkloadIdentityConfigured)
        {
            AppendAzure(wiAzure, $$"""
                "cosmos": { "endpoint": "{{JsonEscape(_cosmosEndpoint!)}}", "databaseName": "{{JsonEscape(_cosmosDatabase!)}}", "authMode": "workloadIdentity" }
                """);
        }

        if (EventHubsWorkloadIdentityConfigured)
        {
            AppendAzure(wiAzure, $$"""
                "eventHubs": { "namespace": "{{JsonEscape(_ehNamespace!)}}", "authMode": "workloadIdentity" }
                """);
        }

        var credentials = new StringBuilder();
        if (azure.Length > 0)
        {
            AppendCredential(credentials, AwsAccessKey, AwsSecret, azure.ToString());
        }

        if (wiAzure.Length > 0)
        {
            AppendCredential(credentials, WiAwsAccessKey, WiAwsSecret, wiAzure.ToString());
        }

        // Exercise the opt-in CosmosBinary response path (#268/#321) end-to-end
        // against real Azure Cosmos DB, which (unlike the CI Linux emulator) does
        // emit 0x80 CosmosBinary bodies — so the fused GetItem reader is actually
        // driven here rather than silently falling back to the text path.
        var dynamoDbBlock = (CosmosConfigured || CosmosWorkloadIdentityConfigured)
            ? "  \"dynamodb\": { \"cosmosBinaryResponses\": true },\n"
            : string.Empty;

        return $$"""
            {
              "services": {
            {{services}}
              },
            {{dynamoDbBlock}}  "credentials": [
            {{credentials}}
              ]
            }
            """;
    }

    private static void AppendCredential(StringBuilder sb, string accessKey, string secret, string azureBlock)
    {
        if (sb.Length > 0)
        {
            sb.Append(",\n");
        }

        sb.Append($$"""
                {
                  "awsAccessKeyId": "{{accessKey}}",
                  "awsSecretAccessKey": "{{secret}}",
                  "azure": {
            {{azureBlock}}
                  }
                }
            """);
    }

    private static void AppendService(StringBuilder sb, string name, bool enabled)
    {
        if (sb.Length > 0)
        {
            sb.Append(",\n");
        }

        sb.Append($"    \"{name}\": {{ \"enabled\": {(enabled ? "true" : "false")} }}");
    }

    private static void AppendAzure(StringBuilder sb, string block)
    {
        if (sb.Length > 0)
        {
            sb.Append(",\n");
        }

        sb.Append("        ").Append(block.Trim());
    }

    private string ServiceUrlFor(string service) => $"http://{service}.127.0.0.1.nip.io:{_proxyPort}";

    /// <summary>
    /// Parses an Azure SAS connection string
    /// (<c>Endpoint=sb://{ns}.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...</c>)
    /// into the short namespace name plus the SAS rule name and key the proxy
    /// config expects.
    /// </summary>
    internal static (string Namespace, string KeyName, string Key) ParseSasConnectionString(string connectionString)
    {
        string ns = string.Empty;
        string keyName = string.Empty;
        string key = string.Empty;

        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();

            if (name.Equals("Endpoint", StringComparison.OrdinalIgnoreCase))
            {
                // sb://my-ns.servicebus.windows.net/ → my-ns
                var host = value
                    .Replace("sb://", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .TrimEnd('/');
                var dot = host.IndexOf('.', StringComparison.Ordinal);
                ns = dot > 0 ? host[..dot] : host;
            }
            else if (name.Equals("SharedAccessKeyName", StringComparison.OrdinalIgnoreCase))
            {
                keyName = value;
            }
            else if (name.Equals("SharedAccessKey", StringComparison.OrdinalIgnoreCase))
            {
                key = value;
            }
        }

        return (ns, keyName, key);
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string JsonEscape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

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

        throw new InvalidOperationException("Could not locate repo root for real-Azure integration fixture.");
    }
}

[CollectionDefinition(Name)]
public sealed class RealAzureCollection : ICollectionFixture<RealAzureProxyFixture>
{
    public const string Name = "real-azure";
}
