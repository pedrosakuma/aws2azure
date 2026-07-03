using System.Text.Json.Serialization;

namespace Aws2Azure.Core.Configuration;

/// <summary>
/// The on-disk configuration schema (binding-centric). This is the shape
/// operators author in JSON and override with <c>AWS2AZURE__*</c> env vars.
/// It is deserialized reflection-free via <see cref="ConfigDocumentJsonContext"/>
/// and then projected onto the resolved <see cref="ProxyConfig"/> model consumed
/// by the runtime through <see cref="ConfigDocumentTranslator"/>.
/// </summary>
/// <remarks>
/// Three top-level concerns are kept honestly separated:
/// <list type="bullet">
///   <item><see cref="Services"/> — which service modules run, plus their
///   behavior knobs (folded under each service, not scattered at the root).</item>
///   <item><see cref="Bindings"/> — the spine: each binding maps one AWS identity
///   (<see cref="BindingEntry.Aws"/>) to a set of Azure backends
///   (<see cref="BindingEntry.Azure"/>).</item>
///   <item><see cref="AzureIdentities"/> — an optional named AAD identity pool that
///   <c>auth.mode: reference</c> entries point at.</item>
/// </list>
/// Within each Azure backend, <c>target</c> (where — non-secret, commit-safe) is
/// split from <c>auth</c> (how — the secret), so a topology can be reviewed without
/// exposing keys.
/// </remarks>
public sealed class ConfigDocument
{
    public ServicesConfig Services { get; set; } = new();
    public List<BindingEntry> Bindings { get; set; } = new();

    /// <summary>
    /// Optional named Azure AD identity pool. Azure backend <c>auth</c> blocks with
    /// <c>mode: reference</c> point at an entry here by name instead of repeating
    /// the same tenant/client fields inline.
    /// </summary>
    public Dictionary<string, AzureIdentity>? AzureIdentities { get; set; }
}

/// <summary>
/// Per-service module toggles and behavior settings. Behavior that used to live in
/// top-level <c>s3</c>/<c>sns</c>/<c>dynamodb</c> blocks now folds under the matching
/// service here, so everything about a service is in one place.
/// </summary>
public sealed class ServicesConfig
{
    public S3ServiceConfig? S3 { get; set; }
    public ServiceToggleConfig? Sqs { get; set; }
    public DynamoDbServiceConfig? DynamoDb { get; set; }
    public SnsServiceConfig? Sns { get; set; }
    public ServiceToggleConfig? Kinesis { get; set; }
    public ServiceToggleConfig? SecretsManager { get; set; }
}

/// <summary>A service with no behavior settings beyond the on/off toggle.</summary>
public sealed class ServiceToggleConfig
{
    public bool Enabled { get; set; }
}

/// <summary>S3 module toggle plus its behavior settings.</summary>
public sealed class S3ServiceConfig
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Opt-in presigned-URL host rewrite allowlist. See
    /// <see cref="S3Settings.PresignedTrustedSigningHosts"/> for the full semantics.
    /// </summary>
    public List<string>? PresignedTrustedSigningHosts { get; set; }
}

/// <summary>SNS module toggle plus its behavior settings.</summary>
public sealed class SnsServiceConfig
{
    public bool Enabled { get; set; }

    /// <summary>Default Azure backend for SNS topics that lack a per-topic override.</summary>
    public SnsTopicBackend DefaultBackend { get; set; } = SnsTopicBackend.ServiceBusTopics;
}

/// <summary>DynamoDB module toggle plus its behavior settings.</summary>
public sealed class DynamoDbServiceConfig
{
    public bool Enabled { get; set; }
    public StoredProcedureMode UseStoredProcedures { get; set; } = StoredProcedureMode.Disabled;
    public ConsistencyCheckMode ConsistencyCheck { get; set; } = ConsistencyCheckMode.Disabled;
    public bool CosmosBinaryResponses { get; set; }
    public bool CosmosBinaryRequests { get; set; }
    public bool EnableGlobalSecondaryIndexQueries { get; set; }
    public bool EnableLocalSecondaryIndexNumericOrdering { get; set; }
}

/// <summary>
/// One binding: an AWS access key paired with the Azure backends it maps to.
/// The binding is the central domain concept — colocating its <see cref="Aws"/>
/// and <see cref="Azure"/> halves expresses one mapping, not a grab-bag of
/// credentials.
/// </summary>
public sealed class BindingEntry
{
    public AwsIdentityConfig Aws { get; set; } = new();
    public AzureBindingSet Azure { get; set; } = new();
}

/// <summary>The AWS half of a binding: the incoming access key and its secret.</summary>
public sealed class AwsIdentityConfig
{
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;
}

/// <summary>
/// The Azure half of a binding: at most one backend per AWS service. Each entry
/// carries a <see cref="AzureBackendConfig.Kind"/> discriminator so the target and
/// auth shapes are unambiguous.
/// </summary>
public sealed class AzureBindingSet
{
    public AzureBackendConfig? S3 { get; set; }
    public AzureBackendConfig? Sqs { get; set; }
    public AzureBackendConfig? DynamoDb { get; set; }
    public AzureBackendConfig? Sns { get; set; }
    public AzureBackendConfig? Kinesis { get; set; }
    public AzureBackendConfig? SecretsManager { get; set; }
}

/// <summary>
/// A single Azure backend within a binding: <see cref="Kind"/> selects the backend
/// type, <see cref="Target"/> carries the non-secret topology, and
/// <see cref="Auth"/> carries the secret. Per-service collections
/// (<see cref="Queues"/>/<see cref="Topics"/>/<see cref="Streams"/>) and
/// <see cref="ShardIteratorSigningKey"/> only apply to their respective kinds.
/// </summary>
public sealed class AzureBackendConfig
{
    /// <summary>
    /// Backend discriminator: <c>blob</c>, <c>serviceBus</c>,
    /// <c>serviceBusTopics</c>, <c>cosmos</c>, <c>eventHubs</c>, <c>eventGrid</c>,
    /// or <c>keyVault</c>. Must be valid for the AWS service it sits under.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    public AzureTargetConfig Target { get; set; } = new();
    public AzureAuthConfig Auth { get; set; } = new();

    /// <summary>Per-queue overrides (SQS / <c>serviceBus</c> only).</summary>
    public Dictionary<string, SqsQueueSettings>? Queues { get; set; }

    /// <summary>Per-topic overrides (SNS / <c>serviceBusTopics</c>/<c>eventGrid</c> only).</summary>
    public Dictionary<string, SnsTopicSettings>? Topics { get; set; }

    /// <summary>Per-stream overrides (Kinesis / <c>eventHubs</c> only).</summary>
    public Dictionary<string, KinesisStreamSettings>? Streams { get; set; }

    /// <summary>
    /// Base64 HMAC key used to sign opaque shard-iterator tokens (Kinesis /
    /// <c>eventHubs</c> only). Proxy-internal secret, not an Azure credential; must
    /// decode to at least 32 bytes when set.
    /// </summary>
    public string? ShardIteratorSigningKey { get; set; }
}

/// <summary>
/// The non-secret topology half of an Azure backend. Every field is optional; which
/// ones apply depends on <see cref="AzureBackendConfig.Kind"/>.
/// </summary>
public sealed class AzureTargetConfig
{
    /// <summary>Absolute service endpoint (blob/cosmos/serviceBusTopics/eventHubs/eventGrid).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Storage account name (<c>blob</c>).</summary>
    public string? AccountName { get; set; }

    /// <summary>Namespace short name (serviceBus/serviceBusTopics/eventHubs/eventGrid).</summary>
    public string? Namespace { get; set; }

    /// <summary>Logical Cosmos database mapped to the DynamoDB namespace (<c>cosmos</c>).</summary>
    public string? DatabaseName { get; set; }

    /// <summary>Management REST endpoint override (<c>serviceBusTopics</c>).</summary>
    public string? ManagementEndpoint { get; set; }

    /// <summary>Ordered Cosmos region preference list (<c>cosmos</c>).</summary>
    public List<string>? PreferredRegions { get; set; }

    /// <summary>Namespace-default wire transport (SQS / <c>serviceBus</c>).</summary>
    public SqsTransport? Transport { get; set; }

    /// <summary>Key Vault base URL (<c>keyVault</c>).</summary>
    public string? VaultUrl { get; set; }

    /// <summary>Event Grid topic host prefix (<c>eventGrid</c>).</summary>
    public string? TopicName { get; set; }
}

/// <summary>
/// The secret half of an Azure backend. <see cref="Mode"/> selects the auth shape and
/// determines which of the remaining fields are consulted.
/// </summary>
public sealed class AzureAuthConfig
{
    /// <summary>How the proxy authenticates to this backend.</summary>
    public AzureAuthKind Mode { get; set; } = AzureAuthKind.SharedKey;

    /// <summary>
    /// The shared secret. For <see cref="AzureAuthKind.SharedKey"/> this is the
    /// account/master/access key (blob account key, Cosmos primary key, Event Grid
    /// access key); for <see cref="AzureAuthKind.Sas"/> it is the SAS key value.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>SAS policy name (<see cref="AzureAuthKind.Sas"/> only).</summary>
    public string? KeyName { get; set; }

    /// <summary>Entra tenant id (<see cref="AzureAuthKind.ClientSecret"/>).</summary>
    public string? TenantId { get; set; }

    /// <summary>Entra application/client id (AAD modes; optional for managed identity).</summary>
    public string? ClientId { get; set; }

    /// <summary>Entra client secret (<see cref="AzureAuthKind.ClientSecret"/>).</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Name of an <see cref="ConfigDocument.AzureIdentities"/> entry
    /// (<see cref="AzureAuthKind.Reference"/> only).
    /// </summary>
    public string? Identity { get; set; }
}

/// <summary>
/// Unified auth shape selector for Azure backends. Replaces the per-backend
/// "is the key field empty?" heuristic with an explicit discriminator.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AzureAuthKind>))]
public enum AzureAuthKind
{
    /// <summary>Account/master/access key HMAC (blob, cosmos, eventGrid).</summary>
    SharedKey = 0,

    /// <summary>Shared Access Signature: <c>keyName</c> + <c>key</c> (serviceBus, serviceBusTopics, eventHubs).</summary>
    Sas = 1,

    /// <summary>Entra managed identity (system- or user-assigned via <c>clientId</c>).</summary>
    ManagedIdentity = 2,

    /// <summary>Entra service principal: <c>tenantId</c> + <c>clientId</c> + <c>clientSecret</c>.</summary>
    ClientSecret = 3,

    /// <summary>Entra workload identity federation.</summary>
    WorkloadIdentity = 4,

    /// <summary>Reference a shared <see cref="ConfigDocument.AzureIdentities"/> entry via <c>identity</c>.</summary>
    Reference = 5,
}

/// <summary>
/// System.Text.Json source-generated context for <see cref="ConfigDocument"/>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ConfigDocument))]
public sealed partial class ConfigDocumentJsonContext : JsonSerializerContext
{
}
