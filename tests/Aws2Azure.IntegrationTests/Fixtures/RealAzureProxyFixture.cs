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
using Amazon.DynamoDBv2;
using Amazon.Kinesis;
using Amazon.S3;
using Amazon.SQS;
using Aws2Azure.TestSupport.OperationalQualification;
using Azure.Storage;
using Azure.Storage.Queues;
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
    public const string InvalidBackendAwsAccessKey = "AKIA-REAL-AZURE-INVALID";
    public const string InvalidBackendAwsSecret = "real-azure-invalid-secret";

    // A second AWS identity whose AAD-capable backends authenticate via Workload
    // Identity instead of shared keys (issue #307). It lives in the same proxy
    // config as a separate credential entry, so one proxy process serves both the
    // shared-key and the Workload-Identity smoke side by side.
    public const string WiAwsAccessKey = "AKIA-REAL-AZURE-NIGHTLY-WI";
    public const string WiAwsSecret = "real-azure-nightly-wi-secret";

    // The namespace-wide SQS transport default is AMQP (below). This one queue
    // name is pinned to the REST transport via a per-queue override so the SQS
    // real-Azure load runner (issue #626) can produce REST-path evidence
    // alongside the AMQP-default path from the same proxy process, without
    // requiring a second deployment or a second credential entry.
    public const string SqsRestLaneQueueName = "aws2azure-sqs-rest-lane";

    /// <summary>
    /// Every SNS topic name starting with this prefix routes Publish/PublishBatch
    /// to the live Event Grid custom topic via a per-topic backend=EventGrid
    /// override (issue #630); every other topic name keeps using Service Bus
    /// Topics, so this scoping cannot affect the existing Service Bus lifecycle,
    /// pagination, or batch real-Azure coverage.
    /// </summary>
    public const string EventGridTopicNamePrefix = "sns-eg-";

    private const string AuthRegion = "us-east-1";
    private const string InvalidAzureSharedKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string InvalidAzureSasKeyName = "aws2azure-invalid-sas-key";

    // Fixed HMAC key (39 decoded bytes, >= the codec's 32-byte minimum) for
    // Kinesis shard-iterator tokens, so a shard iterator minted before
    // RestartAsync() still verifies after the out-of-process proxy respawns.
    // Without an explicit key, each process falls back to its own ephemeral,
    // randomly generated signing key (ShardIteratorTokenCodecFactory), which
    // would make every pre-restart iterator fail signature verification
    // against the new process.
    private const string KinesisShardIteratorSigningKey =
        "cmVhbC1henVyZS1yZXN0YXJ0LXNoYXJkLWl0ZXJhdG9yLWtleS0h";

    private readonly StringBuilder _proxyOutput = new();
    private readonly List<RuntimeInstance> _additionalInstances = [];
    private Process? _proxyProcess;
    private string? _configFile;
    private string? _privateDirectory;
    private int _proxyPort;
    private SealedRuntimeSelection _runtimeSelection = null!;

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
    private string? _sbConnectionString;

    private string? _ehNamespace;
    private string? _ehSasKeyName;
    private string? _ehSasKey;

    private string? _eventGridTopicEndpoint;
    private string? _eventGridTopicKey;
    private string? _eventGridEvidenceQueueName;

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

    /// <summary>SNS → Service Bus Topics backend configured.</summary>
    public bool SnsConfigured => ServiceBusConfigured;

    /// <summary>
    /// SNS → Event Grid backend configured (issue #630). Layered on top of the
    /// mandatory Service Bus Topics binding: topics beginning with
    /// <see cref="EventGridTopicNamePrefix"/> route Publish/PublishBatch to this
    /// live Event Grid custom topic via a per-topic backend=EventGrid override,
    /// while topic administration (CreateTopic/DeleteTopic) still uses Service
    /// Bus, matching the documented backend contract.
    /// </summary>
    public bool EventGridConfigured { get; private set; }

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

    /// <summary>Live storage account name backing S3 and, for the Event Grid SNS
    /// scenario (issue #630), the delivery-evidence Storage Queue.</summary>
    public string StorageAccountName => _blobAccount ?? string.Empty;

    /// <summary>Live storage account key, reused to read the Event Grid
    /// delivery-evidence queue over the Azure.Storage.Queues SDK.</summary>
    public string StorageAccountKey => _blobKey ?? string.Empty;

    /// <summary>Storage Queue that the live Event Grid custom topic's event
    /// subscription forwards accepted events to (issue #630). Lets the real-Azure
    /// SNS suite assert genuine delivered content, not only HTTP-level publish
    /// acceptance.</summary>
    public string EventGridEvidenceQueueName => _eventGridEvidenceQueueName ?? string.Empty;

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
    public int EventHubPartitionCount { get; private set; }

    public string ProxyOutput
    {
        get
        {
            lock (_proxyOutput)
            {
                return _proxyOutput.ToString();
            }
        }
    }
    public string S3ServiceUrl => ServiceUrlFor("s3");
    public string ProxyConfigDigest { get; private set; } = string.Empty;
    public string BackendIdentityDigest { get; private set; } = string.Empty;
    public string ServiceBusBackendIdentityDigest { get; private set; } = string.Empty;
    public string EventHubsBackendIdentityDigest { get; private set; } = string.Empty;
    public string AwsBindingDigest { get; private set; } = string.Empty;
    public bool SealedCandidateConfigured => _runtimeSelection.IsSealed;
    public bool SealedRollbackConfigured => _runtimeSelection.RequiresRollback;
    public SealedRuntimeIdentity CandidateRuntimeIdentity =>
        _runtimeSelection.GetTarget(SealedRuntimeRole.Candidate).Identity;
    public SealedRuntimeIdentity PriorRuntimeIdentity =>
        _runtimeSelection.GetTarget(SealedRuntimeRole.Prior).Identity;
    public string CandidateRuntimeIdentityDigest =>
        Digest(File.ReadAllBytes(
            _runtimeSelection.GetTarget(SealedRuntimeRole.Candidate).IdentityPath));
    public string PriorRuntimeIdentityDigest =>
        Digest(File.ReadAllBytes(
            _runtimeSelection.GetTarget(SealedRuntimeRole.Prior).IdentityPath));

    public async Task InitializeAsync()
    {
        // The shared multi-backend fixture (S3, DynamoDB, SQS, Kinesis, SNS
        // smoke) is qualified for exactly one profile per real-Azure run, but
        // that profile varies by which producer dispatched
        // integration-real-azure.yml. Read it from the environment (the
        // workflow's "Select qualifying or source-validation mode" step
        // exports it) rather than hardcoding "s3-basic-object-crud" — the
        // s3-basic-object-crud default preserves behavior for source
        // validation / scheduled runs and any environment that predates this
        // variable, where SealedRuntimeSelection.Load never validates a
        // profile identity anyway (source_validation mode returns before any
        // profile check).
        var qualificationProfile = Env("AWS2AZURE_QUALIFICATION_PROFILE") ?? "s3-basic-object-crud";
        _runtimeSelection = SealedRuntimeSelection.Load(qualificationProfile, 1);
        ReadEnvironment();

        BlobConfigured = !string.IsNullOrWhiteSpace(_blobAccount) && !string.IsNullOrWhiteSpace(_blobKey);
        CosmosConfigured = !string.IsNullOrWhiteSpace(_cosmosEndpoint) && !string.IsNullOrWhiteSpace(_cosmosKey)
            && !string.IsNullOrWhiteSpace(_cosmosDatabase);
        ServiceBusConfigured = !string.IsNullOrWhiteSpace(_sbNamespace) && !string.IsNullOrWhiteSpace(_sbSasKeyName)
            && !string.IsNullOrWhiteSpace(_sbSasKey);
        EventHubsConfigured = !string.IsNullOrWhiteSpace(_ehNamespace) && !string.IsNullOrWhiteSpace(_ehSasKeyName)
            && !string.IsNullOrWhiteSpace(_ehSasKey) && !string.IsNullOrWhiteSpace(EventHubStream);
        EventGridConfigured = ServiceBusConfigured
            && !string.IsNullOrWhiteSpace(_eventGridTopicEndpoint)
            && !string.IsNullOrWhiteSpace(_eventGridTopicKey)
            && !string.IsNullOrWhiteSpace(_eventGridEvidenceQueueName);

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
        _privateDirectory = SealedRuntimeLauncher.CreatePrivateDirectory(
            AppContext.BaseDirectory,
            "real-azure-it");
        _configFile = Path.Combine(_privateDirectory, "proxy-config.json");
        var configBytes = Encoding.UTF8.GetBytes(BuildConfigJson());
        await SealedRuntimeLauncher.WritePrivateFileAsync(_configFile, configBytes)
            .ConfigureAwait(false);
        ProxyConfigDigest = Digest(configBytes);
        BackendIdentityDigest = Digest(
            (_blobAccount ?? string.Empty) + "\n" + (_blobEndpoint ?? string.Empty));
        ServiceBusBackendIdentityDigest = Digest(_sbNamespace ?? string.Empty);
        EventHubsBackendIdentityDigest = Digest(
            (_ehNamespace ?? string.Empty) + "\n" + EventHubStream);
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

    public AmazonS3Client CreateS3Client(
        string? serviceUrl = null,
        int maxErrorRetry = 2) => new(
        AwsAccessKey, AwsSecret,
        new AmazonS3Config
        {
            ServiceURL = serviceUrl ?? ServiceUrlFor("s3"),
            ForcePathStyle = true,
            UseHttp = true,
            AuthenticationRegion = AuthRegion,
            MaxErrorRetry = maxErrorRetry,
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

    public AmazonSQSClient CreateSqsClient(int? maxErrorRetry = null) => new(
        AwsAccessKey, AwsSecret,
        new AmazonSQSConfig
        {
            ServiceURL = ServiceUrlFor("sqs"),
            UseHttp = true,
            AuthenticationRegion = AuthRegion,
            MaxErrorRetry = maxErrorRetry ?? new AmazonSQSConfig().MaxErrorRetry,
        });

    public HttpClient CreateSnsClient() => new()
    {
        BaseAddress = new Uri(ServiceUrlFor("sns")),
    };

    public string GetServiceUrl(string service) => ServiceUrlFor(service);

    public string CreateServiceBusConnectionString() => _sbConnectionString ?? string.Empty;

    /// <summary>
    /// Storage Queue client for the queue the live Event Grid custom topic
    /// forwards accepted events to (issue #630). Authenticates with the same
    /// storage account key already fetched for the S3 smoke.
    /// </summary>
    public QueueClient CreateEventGridEvidenceQueueClient() => new(
        new Uri($"https://{StorageAccountName}.queue.core.windows.net/{EventGridEvidenceQueueName}"),
        new StorageSharedKeyCredential(StorageAccountName, StorageAccountKey));

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

    public async Task RestartAsync()
    {
        if (!ProxyStarted || _configFile is null)
        {
            throw new InvalidOperationException("The real-Azure proxy is not running.");
        }

        await StopProxyAsync().ConfigureAwait(false);
        _proxyProcess = StartProxyProcess(
            _proxyPort,
            _configFile,
            SealedRuntimeRole.Candidate);
        await WaitForProxyAsync(_proxyPort, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        ProxyStarted = true;
    }

    public async Task<RuntimeInstance> StartAdditionalRuntimeAsync(
        SealedRuntimeRole role)
    {
        if (_configFile is null || !ProxyStarted)
        {
            throw new InvalidOperationException("The real-Azure proxy is not running.");
        }
        if (role == SealedRuntimeRole.Prior && !_runtimeSelection.RequiresRollback)
        {
            throw new InvalidOperationException("No verified prior runtime is configured.");
        }

        var port = GetFreePort();
        var process = StartProxyProcess(port, _configFile, role);
        var instance = new RuntimeInstance(
            process,
            ServiceUrlFor("s3", port),
            role);
        _additionalInstances.Add(instance);
        try
        {
            await WaitForProxyAsync(process, port, TimeSpan.FromMinutes(2))
                .ConfigureAwait(false);
            return instance;
        }
        catch
        {
            await StopAdditionalRuntimeAsync(instance).ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAdditionalRuntimeAsync(RuntimeInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!_additionalInstances.Remove(instance))
        {
            return;
        }
        await StopProcessAsync(instance.Process).ConfigureAwait(false);
    }

    public async Task StopForRuntimeSwitchAsync()
    {
        if (!ProxyStarted)
        {
            throw new InvalidOperationException("The real-Azure proxy is not running.");
        }
        await StopProxyAsync().ConfigureAwait(false);
    }

    public async Task StartRuntimeAsync(SealedRuntimeRole role)
    {
        if (_configFile is null || _proxyProcess is not null)
        {
            throw new InvalidOperationException("The real-Azure proxy cannot start a runtime now.");
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
        foreach (var instance in _additionalInstances.ToArray())
        {
            await StopAdditionalRuntimeAsync(instance).ConfigureAwait(false);
        }
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
            _sbConnectionString = sbConn;
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
        _ = int.TryParse(
            Env("AZURE_EVENTHUBS_PARTITION_COUNT"),
            out var eventHubPartitionCount);
        EventHubPartitionCount = eventHubPartitionCount;

        _eventGridTopicEndpoint = Env("AZURE_EVENTGRID_TOPIC_ENDPOINT");
        _eventGridTopicKey = Env("AZURE_EVENTGRID_TOPIC_KEY");
        _eventGridEvidenceQueueName = Env("AZURE_EVENTGRID_EVIDENCE_QUEUE_NAME");

        _federatedTokenFile = Env("AZURE_FEDERATED_TOKEN_FILE");
        _aadTenantId = Env("AZURE_TENANT_ID");
        _aadClientId = Env("AZURE_CLIENT_ID");
    }

    private string BuildConfigJson()
    {
        var services = new StringBuilder();
        var azure = new StringBuilder();
        AppendService(services, "s3", BlobConfigured);
        var dynamoDbServiceOptions = (CosmosConfigured || CosmosWorkloadIdentityConfigured)
            ? ", \"cosmosBinaryResponses\": true, \"cosmosBinaryRequests\": true, \"enableGlobalSecondaryIndexQueries\": true, \"enableLocalSecondaryIndexNumericOrdering\": true"
            : string.Empty;
        AppendService(services, "dynamodb", CosmosConfigured || CosmosWorkloadIdentityConfigured, dynamoDbServiceOptions);
        AppendService(services, "sqs", ServiceBusConfigured);
        AppendService(services, "sns", ServiceBusConfigured);
        AppendService(services, "kinesis", EventHubsConfigured || EventHubsWorkloadIdentityConfigured);

        if (BlobConfigured)
        {
            var endpoint = string.IsNullOrWhiteSpace(_blobEndpoint)
                ? string.Empty
                : $$""", "endpoint": "{{JsonEscape(_blobEndpoint!)}}" """;
            AppendAzure(azure, $$"""
                "s3": { "kind": "blob", "target": { "accountName": "{{JsonEscape(_blobAccount!)}}"{{endpoint}} }, "auth": { "mode": "sharedKey", "key": "{{JsonEscape(_blobKey!)}}" } }
                """);
        }

        if (CosmosConfigured)
        {
            AppendAzure(azure, $$"""
                "dynamodb": { "kind": "cosmos", "target": { "endpoint": "{{JsonEscape(_cosmosEndpoint!)}}", "databaseName": "{{JsonEscape(_cosmosDatabase!)}}" }, "auth": { "mode": "sharedKey", "key": "{{JsonEscape(_cosmosKey!)}}" } }
                """);
        }

        if (ServiceBusConfigured)
        {
            AppendAzure(azure, $$"""
                "sqs": { "kind": "serviceBus", "target": { "namespace": "{{JsonEscape(_sbNamespace!)}}", "transport": "Amqp" }, "auth": { "mode": "sas", "keyName": "{{JsonEscape(_sbSasKeyName!)}}", "key": "{{JsonEscape(_sbSasKey!)}}" }, "queues": { "{{JsonEscape(SqsRestLaneQueueName)}}": { "transport": "Rest" } } }
                """);
            var eventGridTopics = EventGridConfigured
                ? $$""", "topics": { "{{JsonEscape(EventGridTopicNamePrefix)}}*": { "backend": "EventGrid", "eventGridTopicEndpoint": "{{JsonEscape(_eventGridTopicEndpoint!)}}", "eventGridAccessKey": "{{JsonEscape(_eventGridTopicKey!)}}" } }"""
                : string.Empty;
            AppendAzure(azure, $$"""
                "sns": { "kind": "serviceBusTopics", "target": { "namespace": "{{JsonEscape(_sbNamespace!)}}" }, "auth": { "mode": "sas", "keyName": "{{JsonEscape(_sbSasKeyName!)}}", "key": "{{JsonEscape(_sbSasKey!)}}" }{{eventGridTopics}} }
                """);
        }

        if (EventHubsConfigured)
        {
            AppendAzure(azure, $$"""
                "kinesis": { "kind": "eventHubs", "target": { "namespace": "{{JsonEscape(_ehNamespace!)}}" }, "auth": { "mode": "sas", "keyName": "{{JsonEscape(_ehSasKeyName!)}}", "key": "{{JsonEscape(_ehSasKey!)}}" }, "shardIteratorSigningKey": "{{KinesisShardIteratorSigningKey}}" }
                """);
        }

        // Second credential entry: AAD-capable backends authenticated via Workload
        // Identity (no shared key/SAS — the proxy reads the federated token file).
        var wiAzure = new StringBuilder();
        if (CosmosWorkloadIdentityConfigured)
        {
            AppendAzure(wiAzure, $$"""
                "dynamodb": { "kind": "cosmos", "target": { "endpoint": "{{JsonEscape(_cosmosEndpoint!)}}", "databaseName": "{{JsonEscape(_cosmosDatabase!)}}" }, "auth": { "mode": "workloadIdentity" } }
                """);
        }

        if (EventHubsWorkloadIdentityConfigured)
        {
            AppendAzure(wiAzure, $$"""
                "kinesis": { "kind": "eventHubs", "target": { "namespace": "{{JsonEscape(_ehNamespace!)}}" }, "auth": { "mode": "workloadIdentity" }, "shardIteratorSigningKey": "{{KinesisShardIteratorSigningKey}}" }
                """);
        }

        // Isolated identity for negative-auth conformance. It targets the same
        // live resources but uses deterministic invalid keys, leaving the
        // successful binding and shared Azure resources untouched.
        var invalidAzure = new StringBuilder();
        if (BlobConfigured)
        {
            var endpoint = string.IsNullOrWhiteSpace(_blobEndpoint)
                ? string.Empty
                : $$""", "endpoint": "{{JsonEscape(_blobEndpoint!)}}" """;
            AppendAzure(invalidAzure, $$"""
                "s3": { "kind": "blob", "target": { "accountName": "{{JsonEscape(_blobAccount!)}}"{{endpoint}} }, "auth": { "mode": "sharedKey", "key": "{{InvalidAzureSharedKey}}" } }
                """);
        }

        if (CosmosConfigured || CosmosWorkloadIdentityConfigured)
        {
            AppendAzure(invalidAzure, $$"""
                "dynamodb": { "kind": "cosmos", "target": { "endpoint": "{{JsonEscape(_cosmosEndpoint!)}}", "databaseName": "{{JsonEscape(_cosmosDatabase!)}}" }, "auth": { "mode": "sharedKey", "key": "{{InvalidAzureSharedKey}}" } }
                """);
        }

        if (ServiceBusConfigured)
        {
            AppendAzure(invalidAzure, $$"""
                "sqs": { "kind": "serviceBus", "target": { "namespace": "{{JsonEscape(_sbNamespace!)}}", "transport": "Amqp" }, "auth": { "mode": "sas", "keyName": "{{InvalidAzureSasKeyName}}", "key": "{{InvalidAzureSharedKey}}" } }
                """);
            AppendAzure(invalidAzure, $$"""
                "sns": { "kind": "serviceBusTopics", "target": { "namespace": "{{JsonEscape(_sbNamespace!)}}" }, "auth": { "mode": "sas", "keyName": "{{InvalidAzureSasKeyName}}", "key": "{{InvalidAzureSharedKey}}" } }
                """);
        }

        if (EventHubsConfigured || EventHubsWorkloadIdentityConfigured)
        {
            AppendAzure(invalidAzure, $$"""
                "kinesis": { "kind": "eventHubs", "target": { "namespace": "{{JsonEscape(_ehNamespace!)}}" }, "auth": { "mode": "sas", "keyName": "{{InvalidAzureSasKeyName}}", "key": "{{InvalidAzureSharedKey}}" } }
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

        if (invalidAzure.Length > 0)
        {
            AppendCredential(
                credentials,
                InvalidBackendAwsAccessKey,
                InvalidBackendAwsSecret,
                invalidAzure.ToString());
        }

        return $$"""
            {
              "services": {
            {{services}}
              },
              "bindings": [
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
                  "aws": {
                    "accessKeyId": "{{accessKey}}",
                    "secretAccessKey": "{{secret}}"
                  },
                  "azure": {
            {{azureBlock}}
                  }
                }
            """);
    }

    private static void AppendService(StringBuilder sb, string name, bool enabled, string extraProperties = "")
    {
        if (sb.Length > 0)
        {
            sb.Append(",\n");
        }

        sb.Append($"    \"{name}\": {{ \"enabled\": {(enabled ? "true" : "false")}{extraProperties} }}");
    }

    private static void AppendAzure(StringBuilder sb, string block)
    {
        if (sb.Length > 0)
        {
            sb.Append(",\n");
        }

        sb.Append("        ").Append(block.Trim());
    }

    private string ServiceUrlFor(string service, int? port = null) =>
        $"http://{service}.127.0.0.1.nip.io:{port ?? _proxyPort}";

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
            new Dictionary<string, string?>
            {
                ["AZURE_TENANT_ID"] = _aadTenantId,
                ["AZURE_CLIENT_ID"] = _aadClientId,
                ["AZURE_FEDERATED_TOKEN_FILE"] = _federatedTokenFile,
                ["AZURE_AUTHORITY_HOST"] =
                    Environment.GetEnvironmentVariable("AZURE_AUTHORITY_HOST"),
            });

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

    private async Task WaitForProxyAsync(int port, TimeSpan timeout) =>
        await WaitForProxyAsync(_proxyProcess!, port, timeout).ConfigureAwait(false);

    private async Task WaitForProxyAsync(
        Process process,
        int port,
        TimeSpan timeout)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    "Proxy process exited before becoming ready:" + Environment.NewLine + ProxyOutput);
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

        throw new TimeoutException($"Timed out waiting for proxy on port {port}.{Environment.NewLine}{ProxyOutput}");
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

    private static string Digest(string value) =>
        Digest(Encoding.UTF8.GetBytes(value));

    private static string Digest(ReadOnlySpan<byte> value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(value));

    public sealed class RuntimeInstance(
        Process process,
        string serviceUrl,
        SealedRuntimeRole runtimeRole)
    {
        internal Process Process { get; } = process;
        public string ServiceUrl { get; } = serviceUrl;
        public SealedRuntimeRole RuntimeRole { get; } = runtimeRole;
    }
}

[CollectionDefinition(Name)]
public sealed class RealAzureCollection : ICollectionFixture<RealAzureProxyFixture>
{
    public const string Name = "real-azure";
}
