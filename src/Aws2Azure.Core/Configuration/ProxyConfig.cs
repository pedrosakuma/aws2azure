using System.Text.Json.Serialization;

namespace Aws2Azure.Core.Configuration;

/// <summary>
/// Root configuration POCO. Mirrors <c>appsettings.json</c> and the
/// <c>AWS2AZURE__*</c> env-var hierarchy.
/// </summary>
public sealed class ProxyConfig
{
    public Dictionary<string, ServiceToggle> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public SnsSettings Sns { get; set; } = new();
    public DynamoDbSettings DynamoDb { get; set; } = new();
    public List<CredentialEntry> Credentials { get; set; } = new();
}

public sealed class ServiceToggle
{
    public bool Enabled { get; set; }
}

public sealed class SnsSettings
{
    public SnsTopicBackend DefaultBackend { get; set; } = SnsTopicBackend.ServiceBusTopics;
}

/// <summary>
/// DynamoDB module-level settings.
/// </summary>
public sealed class DynamoDbSettings
{
    /// <summary>
    /// Controls whether stored procedures are used for atomic conditional writes.
    /// <list type="bullet">
    ///   <item><c>Disabled</c> (default) — GET→PUT with ETag/If-Match (current behavior)</item>
    ///   <item><c>Preferred</c> — use sprocs when available, fallback to GET→PUT if sproc missing</item>
    ///   <item><c>Required</c> — sprocs must exist; fail startup if missing and cannot create</item>
    /// </list>
    /// </summary>
    public StoredProcedureMode UseStoredProcedures { get; set; } = StoredProcedureMode.Disabled;

    /// <summary>
    /// Startup probe of each configured Cosmos account's default consistency
    /// level versus what DynamoDB <c>ConsistentRead</c> / read-your-write
    /// require (Strong). Cosmos only relaxes consistency
    /// per request, never strengthens it, so on a Bounded Staleness / Session /
    /// Consistent Prefix / Eventual account the proxy's
    /// <c>x-ms-consistency-level: Strong</c> header
    /// is silently ignored and <c>ConsistentRead</c> is not honored (#204).
    /// <list type="bullet">
    ///   <item><c>Disabled</c> (default) — no probe; no startup network call.</item>
    ///   <item><c>Warn</c> — probe each account at startup; log a warning when below Strong.</item>
    ///   <item><c>Required</c> — probe each account at startup; fail startup when below Strong (or when the probe cannot determine the level).</item>
    /// </list>
    /// </summary>
    public ConsistencyCheckMode ConsistencyCheck { get; set; } = ConsistencyCheckMode.Disabled;
}

/// <summary>
/// Startup validation mode for the Cosmos account default consistency level
/// versus DynamoDB <c>ConsistentRead</c> semantics (#204).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConsistencyCheckMode>))]
public enum ConsistencyCheckMode
{
    /// <summary>Do not probe account consistency at startup (no boot network call).</summary>
    Disabled = 0,

    /// <summary>Probe at startup; log a warning when an account cannot honor ConsistentRead.</summary>
    Warn = 1,

    /// <summary>Probe at startup; fail startup when an account cannot honor ConsistentRead.</summary>
    Required = 2,
}

/// <summary>
/// Stored procedure usage mode for DynamoDB conditional writes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StoredProcedureMode>))]
public enum StoredProcedureMode
{
    /// <summary>Do not use stored procedures; use GET→PUT with optimistic concurrency.</summary>
    Disabled = 0,

    /// <summary>Use stored procedures when available; fallback to GET→PUT if sproc missing.</summary>
    Preferred = 1,

    /// <summary>Require stored procedures; fail startup/operation if sproc cannot be created.</summary>
    Required = 2,
}

public sealed class CredentialEntry
{
    public string AwsAccessKeyId { get; set; } = string.Empty;
    public string AwsSecretAccessKey { get; set; } = string.Empty;
    public AzureCredentials Azure { get; set; } = new();
}

public sealed class AzureCredentials
{
    public BlobCredentials? Blob { get; set; }
    public ServiceBusCredentials? ServiceBus { get; set; }
    public ServiceBusTopicsCredentials? ServiceBusTopics { get; set; }
    public CosmosCredentials? Cosmos { get; set; }
    public EventHubsCredentials? EventHubs { get; set; }
    public EventGridCredentials? EventGrid { get; set; }
    public KeyVaultCredentials? KeyVault { get; set; }
}

public sealed class BlobCredentials
{
    public string AccountName { get; set; } = string.Empty;
    public string AccountKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional absolute Blob service endpoint. When empty, the canonical
    /// formula <c>https://{AccountName}.blob.core.windows.net</c> is used.
    /// Override for Azurite (<c>http://127.0.0.1:10000/devstoreaccount1</c>)
    /// or sovereign clouds (e.g. <c>blob.core.usgovcloudapi.net</c>).
    /// </summary>
    public string? ServiceEndpoint { get; set; }
}

public sealed class ServiceBusCredentials
{
    public string Namespace { get; set; } = string.Empty;
    public string SasKeyName { get; set; } = string.Empty;
    public string SasKey { get; set; } = string.Empty;

    /// <summary>
    /// Namespace-wide default transport for the SQS module: REST over
    /// HTTPS or native AMQP 1.0. Per-queue overrides via
    /// <see cref="Queues"/> take precedence. Defaults to
    /// <see cref="SqsTransport.Rest"/> for backward compatibility with
    /// the original handlers.
    /// </summary>
    public SqsTransport Transport { get; set; } = SqsTransport.Rest;

    /// <summary>
    /// Optional per-queue settings. Key is the canonical Service Bus
    /// queue name (lowercase, as it appears in the AWS queue URL). When
    /// a queue is absent from this map, the namespace defaults apply.
    /// </summary>
    public Dictionary<string, SqsQueueSettings>? Queues { get; set; }
}

/// <summary>
/// Wire transport used by the SQS module to talk to Service Bus on
/// behalf of a queue. <see cref="Rest"/> uses the REST proxy
/// (POST <c>/{queue}/messages</c> &amp; peek-lock); <see cref="Amqp"/>
/// uses the in-process AMQP 1.0 client built in Phase 2.5.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SqsTransport>))]
public enum SqsTransport
{
    Rest = 0,
    Amqp = 1,
}

/// <summary>
/// Per-queue overrides for the SQS module. Currently carries only the
/// transport selector; future knobs (prefetch, idle-timeout) can be
/// added without breaking existing configs.
/// </summary>
public sealed class SqsQueueSettings
{
    /// <summary>
    /// Transport for this queue. When <c>null</c>, the namespace
    /// default from <see cref="ServiceBusCredentials.Transport"/> wins.
    /// </summary>
    public SqsTransport? Transport { get; set; }
}

/// <summary>
/// Azure Service Bus Topics credentials used by the SNS module.
/// Kept separate from <see cref="ServiceBusCredentials"/> so queue- and
/// topic-specific settings can evolve independently.
/// </summary>
public sealed class ServiceBusTopicsCredentials
{
    public string Namespace { get; set; } = string.Empty;

    /// <summary>
    /// Optional AMQP endpoint override. Use <c>http://host:5672/</c> for the
    /// Service Bus emulator's plain-TCP listener or a sovereign-cloud custom host.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Optional management REST endpoint override. When empty, the proxy reuses
    /// <see cref="Endpoint"/> (if it is HTTP/S) or derives the canonical cloud
    /// namespace host from <see cref="Namespace"/>.
    /// </summary>
    public string? ManagementEndpoint { get; set; }

    public string SasKeyName { get; set; } = string.Empty;
    public string SasKey { get; set; } = string.Empty;

    /// <summary>
    /// Entra token auth mode. Defaults to client-secret credentials; set to
    /// managed identity or workload identity to replace the secret-based AAD
    /// flow while leaving SAS auth as a separate mutually-exclusive shape.
    /// </summary>
    public AzureAuthMode AuthMode { get; set; } = AzureAuthMode.ClientSecret;

    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public Dictionary<string, SnsTopicSettings>? Topics { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<SnsTopicBackend>))]
public enum SnsTopicBackend
{
    ServiceBusTopics = 0,
    EventGrid = 1,
}

public sealed class SnsTopicSettings
{
    public SnsTopicBackend Backend { get; set; } = SnsTopicBackend.ServiceBusTopics;
    public string? ServiceBusTopicName { get; set; }

    /// <summary>
    /// Optional absolute Event Grid publish endpoint override for this SNS topic
    /// pattern (for example <c>https://orders.eastus-1.eventgrid.azure.net/api/events</c>).
    /// </summary>
    public string? EventGridTopicEndpoint { get; set; }

    /// <summary>
    /// Optional per-topic Event Grid access key override. When omitted, the
    /// credential-level <see cref="EventGridCredentials.AccessKey"/> or AAD
    /// settings are used.
    /// </summary>
    public string? EventGridAccessKey { get; set; }
}

public sealed class KeyVaultCredentials
{
    public string VaultUrl { get; set; } = string.Empty;

    /// <summary>
    /// Entra token auth mode. Defaults to client-secret credentials; set to
    /// managed identity or workload identity to acquire the Key Vault token
    /// without storing a client secret in proxy configuration.
    /// </summary>
    public AzureAuthMode AuthMode { get; set; } = AzureAuthMode.ClientSecret;

    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

public sealed class EventGridCredentials
{
    /// <summary>
    /// Optional absolute Event Grid publish endpoint. When empty, the proxy
    /// derives <c>https://{TopicName}.{Namespace}/api/events</c> from
    /// <see cref="Namespace"/> and <see cref="TopicName"/>.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Event Grid hostname suffix used to derive the endpoint when
    /// <see cref="Endpoint"/> is empty (for example <c>eastus-1.eventgrid.azure.net</c>).
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// Event Grid topic host prefix used alongside <see cref="Namespace"/> when
    /// <see cref="Endpoint"/> is empty.
    /// </summary>
    public string? TopicName { get; set; }

    public string? AccessKey { get; set; }

    /// <summary>
    /// Entra token auth mode. Defaults to client-secret credentials; set to
    /// managed identity or workload identity to replace the secret-based AAD
    /// flow when no Event Grid access key is configured.
    /// </summary>
    public AzureAuthMode AuthMode { get; set; } = AzureAuthMode.ClientSecret;

    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

public sealed class CosmosCredentials
{
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Cosmos master key (primary or secondary). Mutually exclusive with
    /// the AAD shape: when populated the proxy uses master-key HMAC
    /// signing; when left empty the AAD fields are consulted instead.
    /// </summary>
    public string PrimaryKey { get; set; } = string.Empty;

    /// <summary>
    /// Entra token auth mode. Defaults to client-secret credentials; set to
    /// managed identity or workload identity to replace the secret-based AAD
    /// flow when no Cosmos primary key is configured.
    /// </summary>
    public AzureAuthMode AuthMode { get; set; } = AzureAuthMode.ClientSecret;

    /// <summary>
    /// Logical Cosmos database that maps 1:1 to the AWS account's
    /// DynamoDB namespace. All DynamoDB tables for this credential live
    /// underneath <c>/dbs/{DatabaseName}</c> as Cosmos containers.
    /// Required.
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Entra ID tenant for AAD auth. Required when <see cref="PrimaryKey"/>
    /// is empty.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Entra application (client) id for AAD auth.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Entra client secret for AAD auth. Future managed-identity
    /// support can drop this in favour of a token-source abstraction.
    /// </summary>
    public string? ClientSecret { get; set; }
}

/// <summary>
/// Azure Event Hubs credentials used by the Kinesis module. Mirrors the
/// dual-shape pattern of <see cref="ServiceBusCredentials"/> /
/// <see cref="CosmosCredentials"/>: SAS-key for shared-access auth
/// (per Event Hubs namespace) or AAD for managed identity / SP auth.
/// Exactly one shape must be populated.
/// </summary>
public sealed class EventHubsCredentials
{
    /// <summary>
    /// Event Hubs namespace short name (e.g. <c>mynamespace</c>). The
    /// canonical FQDN <c>{Namespace}.servicebus.windows.net</c> is
    /// derived from it unless <see cref="Endpoint"/> overrides.
    /// </summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>
    /// Optional absolute endpoint override. Use when targeting a
    /// sovereign cloud (e.g. <c>{ns}.servicebus.usgovcloudapi.net</c>)
    /// or a future EH emulator. When empty, the canonical
    /// <c>{Namespace}.servicebus.windows.net</c> form is used.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// SAS rule name (per-namespace shared-access policy). Mutually
    /// exclusive with the AAD shape.
    /// </summary>
    public string SasKeyName { get; set; } = string.Empty;

    /// <summary>
    /// SAS rule key (primary or secondary). Mutually exclusive with
    /// the AAD shape.
    /// </summary>
    public string SasKey { get; set; } = string.Empty;

    /// <summary>
    /// Entra token auth mode. Defaults to client-secret credentials; set to
    /// managed identity or workload identity to replace the secret-based AAD
    /// flow when no Event Hubs SAS key is configured.
    /// </summary>
    public AzureAuthMode AuthMode { get; set; } = AzureAuthMode.ClientSecret;

    /// <summary>
    /// Entra tenant id for AAD auth. Required when SAS fields are
    /// empty; mutually exclusive with the SAS shape.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Entra application (client) id for AAD auth.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Entra client secret for AAD auth.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Signing secret used to HMAC the opaque shard-iterator tokens
    /// the Kinesis module hands out from <c>GetShardIterator</c>.
    /// Optional in the credential entry; falls back to a per-process
    /// derived secret when empty, but operators are strongly
    /// encouraged to set this in production so iterator tokens remain
    /// valid across proxy restarts. Base64-encoded; must decode to at
    /// least 32 bytes.
    /// </summary>
    public string? ShardIteratorSigningKey { get; set; }

    /// <summary>
    /// Optional per-stream overrides. Key is the Kinesis stream name
    /// exactly as AWS callers send it; value carries the Event Hubs
    /// entity name to back it with and any stream-level knobs (e.g.
    /// consumer group). When a stream is absent from this map, the
    /// proxy assumes the Event Hub entity name == stream name and
    /// uses the namespace-level <c>$Default</c> consumer group.
    /// </summary>
    public Dictionary<string, KinesisStreamSettings>? Streams { get; set; }
}

/// <summary>
/// Per-stream overrides for the Kinesis module. All fields optional —
/// missing values fall back to the namespace-level defaults derived
/// from the AWS stream name.
/// </summary>
public sealed class KinesisStreamSettings
{
    /// <summary>
    /// Event Hubs entity name backing this Kinesis stream. When null,
    /// the Kinesis stream name is used verbatim. Useful when AWS
    /// stream names exceed Event Hubs naming rules or already exist
    /// under different identifiers on the Azure side.
    /// </summary>
    public string? EventHubName { get; set; }

    /// <summary>
    /// Event Hubs consumer group used by <c>GetRecords</c>. Defaults
    /// to <c>$Default</c>. Distinct AWS consumers sharing the same
    /// stream should map to distinct consumer groups.
    /// </summary>
    public string? ConsumerGroup { get; set; }

    /// <summary>
    /// Optional static partition-count override used when the proxy
    /// cannot query the Event Hubs management surface (for example,
    /// local emulator-backed integration tests). When set, the Kinesis
    /// module derives shard metadata directly from config.
    /// </summary>
    public int? PartitionCount { get; set; }
}

/// <summary>
/// System.Text.Json source-generated context for <see cref="ProxyConfig"/>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ProxyConfig))]
public sealed partial class ProxyConfigJsonContext : JsonSerializerContext
{
}
