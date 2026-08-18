using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.ConfigSchema;

/// <summary>
/// Generates the normative operator schema from an explicit, compile-time mapping
/// of the source-generated ConfigDocument contract. Backend-specific definitions
/// intentionally mirror the translator and startup validator instead of exposing
/// the wider generic AzureBackendConfig POCO shape.
/// </summary>
public static class ConfigSchemaGenerator
{
    public const string ArtifactRelativePath = "config.schema.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public static string Generate()
    {
        VerifyContract();

        var schema = Object(
            ("$schema", "https://json-schema.org/draft/2020-12/schema"),
            ("$id", "https://github.com/pedrosakuma/aws2azure/config.schema.json"),
            ("title", "aws2azure operator configuration"),
            ("description", "Normative binding-centric ConfigDocument selected by AWS2AZURE_CONFIG_FILE."),
            ("type", "object"),
            ("additionalProperties", false),
            ("properties", Object(
                ("services", Ref("services")),
                ("bindings", Object(
                    ("type", "array"),
                    ("minItems", 1),
                    ("items", Ref("binding")))),
                ("azureIdentities", Object(
                    ("type", "object"),
                    ("description", "Named Entra identities referenced by AAD-capable backend auth blocks."),
                    ("propertyNames", Object(("minLength", 1))),
                    ("additionalProperties", Ref("azureIdentity")))))),
            ("required", Array("bindings")),
            ("$defs", Definitions()),
            ("allOf", Array(EventGridDefaultConstraint())),
            ("examples", Array(JsonNode.Parse(CompleteExample))));

        return schema.ToJsonString(SerializerOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static JsonObject Definitions() => Object(
        ("nonEmptyString", Object(
            ("type", "string"),
            ("minLength", 1),
            ("pattern", "\\S"))),
        ("services", Services()),
        ("serviceToggle", ServiceToggle()),
        ("s3Service", S3Service()),
        ("snsService", SnsService()),
        ("dynamoDbService", DynamoDbService()),
        ("binding", Binding()),
        ("awsIdentity", AwsIdentity()),
        ("azureBindings", AzureBindings()),
        ("azureIdentity", AzureIdentity()),
        ("s3Backend", S3Backend()),
        ("sqsBackend", SqsBackend()),
        ("dynamoDbBackend", DynamoDbBackend()),
        ("snsBackend", SnsBackend()),
        ("snsServiceBusTopicsBackend", SnsServiceBusTopicsBackend()),
        ("snsEventGridBackend", SnsEventGridBackend()),
        ("eventGridFallback", EventGridFallback()),
        ("kinesisBackend", KinesisBackend()),
        ("secretsManagerBackend", SecretsManagerBackend()),
        ("sharedKeyAuth", SharedKeyAuth()),
        ("sasAuth", SasAuth()),
        ("managedIdentityAuth", ManagedIdentityAuth()),
        ("clientSecretAuth", ClientSecretAuth()),
        ("workloadIdentityAuth", WorkloadIdentityAuth()),
        ("referenceAuth", ReferenceAuth()),
        ("aadAuth", AadAuth()),
        ("keyOrAadAuth", KeyOrAadAuth()),
        ("sasOrAadAuth", SasOrAadAuth()),
        ("queueSettings", QueueSettings()),
        ("topicSettings", TopicSettings()),
        ("topicSettingsWithoutFallback", TopicSettingsWithoutFallback()),
        ("streamSettings", StreamSettings()));

    private static JsonObject Services() => ClosedObject(
        Object(
            ("s3", Ref("s3Service")),
            ("sqs", Ref("serviceToggle")),
            ("dynamodb", Ref("dynamoDbService")),
            ("sns", Ref("snsService")),
            ("kinesis", Ref("serviceToggle")),
            ("secretsmanager", Ref("serviceToggle"))));

    private static JsonObject ServiceToggle() => ClosedObject(
        Object(("enabled", Boolean(defaultValue: false))));

    private static JsonObject S3Service() => ClosedObject(
        Object(
            ("enabled", Boolean(defaultValue: false)),
            ("presignedTrustedSigningHosts", Object(
                ("type", "array"),
                ("description", "Lowercase bare signing hosts; schemes, paths, and whitespace are not allowed."),
                ("uniqueItems", true),
                ("items", Object(
                    ("type", "string"),
                    ("minLength", 1),
                    ("pattern", "^(?!.*://)[^A-Z/\\s]+$")))))));

    private static JsonObject SnsService() => ClosedObject(
        Object(
            ("enabled", Boolean(defaultValue: false)),
            ("defaultBackend", Enum(
                nameof(SnsTopicBackend.ServiceBusTopics),
                nameof(SnsTopicBackend.ServiceBusTopics),
                nameof(SnsTopicBackend.EventGrid)))));

    private static JsonObject DynamoDbService() => ClosedObject(
        Object(
            ("enabled", Boolean(defaultValue: false)),
            ("useStoredProcedures", Enum(
                nameof(StoredProcedureMode.Disabled),
                nameof(StoredProcedureMode.Disabled),
                nameof(StoredProcedureMode.Preferred),
                nameof(StoredProcedureMode.Required))),
            ("consistencyCheck", Enum(
                nameof(ConsistencyCheckMode.Disabled),
                nameof(ConsistencyCheckMode.Disabled),
                nameof(ConsistencyCheckMode.Warn),
                nameof(ConsistencyCheckMode.Required))),
            ("cosmosBinaryResponses", Boolean(defaultValue: false)),
            ("cosmosBinaryRequests", Boolean(defaultValue: false)),
            ("enableGlobalSecondaryIndexQueries", Boolean(defaultValue: false)),
            ("enableLocalSecondaryIndexNumericOrdering", Boolean(defaultValue: false))));

    private static JsonObject Binding() => ClosedObject(
        Object(
            ("aws", Ref("awsIdentity")),
            ("azure", Ref("azureBindings"))),
        "aws",
        "azure");

    private static JsonObject AwsIdentity() => ClosedObject(
        Object(
            ("accessKeyId", Ref("nonEmptyString")),
            ("secretAccessKey", Ref("nonEmptyString"))),
        "accessKeyId",
        "secretAccessKey");

    private static JsonObject AzureBindings() => ClosedObject(
        Object(
            ("s3", Ref("s3Backend")),
            ("sqs", Ref("sqsBackend")),
            ("dynamodb", Ref("dynamoDbBackend")),
            ("sns", Ref("snsBackend")),
            ("kinesis", Ref("kinesisBackend")),
            ("secretsmanager", Ref("secretsManagerBackend"))));

    private static JsonObject AzureIdentity() => Object(
        ("description", "A reusable Entra identity. Workload identity fields come from AZURE_* environment variables."),
        ("oneOf", Array(
            ClosedObject(
                Object(
                    ("authMode", Const(Camel(nameof(AzureAuthMode.ClientSecret)))),
                    ("tenantId", Ref("nonEmptyString")),
                    ("clientId", Ref("nonEmptyString")),
                    ("clientSecret", Ref("nonEmptyString"))),
                "authMode", "tenantId", "clientId", "clientSecret"),
            ClosedObject(
                Object(
                    ("authMode", Const(Camel(nameof(AzureAuthMode.ManagedIdentity)))),
                    ("clientId", Ref("nonEmptyString"))),
                "authMode"),
            ClosedObject(
                Object(("authMode", Const(Camel(nameof(AzureAuthMode.WorkloadIdentity))))),
                "authMode"))));

    private static JsonObject S3Backend() => ClosedObject(
        Object(
            ("kind", Const("blob")),
            ("target", ClosedObject(
                Object(
                    ("accountName", Ref("nonEmptyString")),
                    ("endpoint", HttpUri())),
                "accountName")),
            ("auth", Ref("sharedKeyAuth"))),
        "kind", "target", "auth");

    private static JsonObject SqsBackend() => ClosedObject(
        Object(
            ("kind", Const("serviceBus")),
            ("target", ClosedObject(
                Object(
                    ("namespace", Ref("nonEmptyString")),
                    ("managementEndpoint", HttpUri()),
                    ("transport", Enum(
                        nameof(SqsTransport.Rest),
                        nameof(SqsTransport.Rest),
                        nameof(SqsTransport.Amqp)))),
                "namespace")),
            ("auth", Ref("sasAuth")),
            ("queues", NamedMap(Ref("queueSettings")))),
        "kind", "target", "auth");

    private static JsonObject DynamoDbBackend() => ClosedObject(
        Object(
            ("kind", Const("cosmos")),
            ("target", ClosedObject(
                Object(
                    ("endpoint", HttpUri()),
                    ("databaseName", Ref("nonEmptyString")),
                    ("preferredRegions", Object(
                        ("type", "array"),
                        ("minItems", 1),
                        ("items", Ref("nonEmptyString"))))),
                "endpoint", "databaseName")),
            ("auth", Ref("keyOrAadAuth"))),
        "kind", "target", "auth");

    private static JsonObject SnsBackend() => Object(
        ("oneOf", Array(
            Ref("snsServiceBusTopicsBackend"),
            Ref("snsEventGridBackend"))));

    private static JsonObject SnsServiceBusTopicsBackend()
    {
        var backend = ClosedObject(
            Object(
            ("kind", Const("serviceBusTopics")),
            ("target", ClosedObject(
                Object(
                    ("namespace", Ref("nonEmptyString")),
                    ("endpoint", ServiceBusUri()),
                    ("managementEndpoint", HttpUri())),
                "namespace")),
            ("auth", Ref("sasOrAadAuth")),
            ("topics", NamedMap(Ref("topicSettings"))),
            ("eventGridFallback", Ref("eventGridFallback"))),
            "kind", "target", "auth");
        backend.Add("allOf", Array(
            Object(
                ("if", Object(
                    ("not", Object(("required", Array("eventGridFallback")))))),
                ("then", Object(
                    ("properties", Object(
                        ("topics", NamedMap(Ref("topicSettingsWithoutFallback"))))))))));
        return backend;
    }

    private static JsonObject SnsEventGridBackend() => ClosedObject(
        Object(
            ("kind", Const("eventGrid")),
            ("target", EventGridTarget()),
            ("auth", Ref("keyOrAadAuth"))),
        "kind", "target", "auth");

    private static JsonObject EventGridFallback() => ClosedObject(
        Object(
            ("kind", Const("eventGrid")),
            ("target", EventGridTarget()),
            ("auth", Ref("keyOrAadAuth"))),
        "kind", "target", "auth");

    private static JsonObject EventGridTarget() => Object(
        ("type", "object"),
        ("additionalProperties", false),
        ("properties", Object(
            ("endpoint", HttpsUri()),
            ("namespace", Ref("nonEmptyString")),
            ("topicName", Ref("nonEmptyString")))),
        ("anyOf", Array(
            Object(("required", Array("endpoint"))),
            Object(("required", Array("namespace", "topicName"))))));

    private static JsonObject KinesisBackend() => ClosedObject(
        Object(
            ("kind", Const("eventHubs")),
            ("target", ClosedObject(
                Object(
                    ("namespace", Ref("nonEmptyString")),
                    ("endpoint", ServiceBusUri())),
                "namespace")),
            ("auth", Ref("sasOrAadAuth")),
            ("streams", NamedMap(Ref("streamSettings"))),
            ("shardIteratorSigningKey", Object(
                ("type", "string"),
                ("description", "Base64-encoded HMAC key that decodes to at least 32 bytes."),
                ("minLength", 44),
                ("contentEncoding", "base64"),
                ("pattern", "^(?![A-Za-z0-9+/]{42}==$)(?=.{44,}$)(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$")))),
        "kind", "target", "auth");

    private static JsonObject SecretsManagerBackend() => ClosedObject(
        Object(
            ("kind", Const("keyVault")),
            ("target", ClosedObject(
                Object(("vaultUrl", HttpsUri())),
                "vaultUrl")),
            ("auth", Ref("aadAuth"))),
        "kind", "target", "auth");

    private static JsonObject SharedKeyAuth() => ClosedObject(
        Object(
            ("mode", Const(Camel(nameof(AzureAuthKind.SharedKey)))),
            ("key", Ref("nonEmptyString"))),
        "mode", "key");

    private static JsonObject SasAuth() => ClosedObject(
        Object(
            ("mode", Const(Camel(nameof(AzureAuthKind.Sas)))),
            ("keyName", Ref("nonEmptyString")),
            ("key", Ref("nonEmptyString"))),
        "mode", "keyName", "key");

    private static JsonObject ManagedIdentityAuth() => ClosedObject(
        Object(
            ("mode", Const(Camel(nameof(AzureAuthKind.ManagedIdentity)))),
            ("clientId", Ref("nonEmptyString"))),
        "mode");

    private static JsonObject ClientSecretAuth() => ClosedObject(
        Object(
            ("mode", Const(Camel(nameof(AzureAuthKind.ClientSecret)))),
            ("tenantId", Ref("nonEmptyString")),
            ("clientId", Ref("nonEmptyString")),
            ("clientSecret", Ref("nonEmptyString"))),
        "mode", "tenantId", "clientId", "clientSecret");

    private static JsonObject WorkloadIdentityAuth() => ClosedObject(
        Object(("mode", Const(Camel(nameof(AzureAuthKind.WorkloadIdentity))))),
        "mode");

    private static JsonObject ReferenceAuth() => ClosedObject(
        Object(
            ("mode", Const(Camel(nameof(AzureAuthKind.Reference)))),
            ("identity", Ref("nonEmptyString"))),
        "mode", "identity");

    private static JsonObject AadAuth() => Object(
        ("oneOf", Array(
            Ref("managedIdentityAuth"),
            Ref("clientSecretAuth"),
            Ref("workloadIdentityAuth"),
            Ref("referenceAuth"))));

    private static JsonObject KeyOrAadAuth() => Object(
        ("oneOf", Array(
            Ref("sharedKeyAuth"),
            Ref("managedIdentityAuth"),
            Ref("clientSecretAuth"),
            Ref("workloadIdentityAuth"),
            Ref("referenceAuth"))));

    private static JsonObject SasOrAadAuth() => Object(
        ("oneOf", Array(
            Ref("sasAuth"),
            Ref("managedIdentityAuth"),
            Ref("clientSecretAuth"),
            Ref("workloadIdentityAuth"),
            Ref("referenceAuth"))));

    private static JsonObject QueueSettings() => ClosedObject(
        Object(
            ("transport", Object(
                ("type", Array("string", "null")),
                ("enum", Array(nameof(SqsTransport.Rest), nameof(SqsTransport.Amqp), null))))));

    private static JsonObject TopicSettings() => ClosedObject(
        Object(
            ("backend", Enum(
                nameof(SnsTopicBackend.ServiceBusTopics),
                nameof(SnsTopicBackend.ServiceBusTopics),
                nameof(SnsTopicBackend.EventGrid))),
            ("serviceBusTopicName", Ref("nonEmptyString")),
            ("eventGridTopicEndpoint", HttpsUri()),
            ("eventGridAccessKey", Ref("nonEmptyString"))));

    private static JsonObject TopicSettingsWithoutFallback() => Object(
        ("oneOf", Array(
            ClosedObject(
                Object(
                    ("backend", Object(
                        ("const", nameof(SnsTopicBackend.ServiceBusTopics)),
                        ("default", nameof(SnsTopicBackend.ServiceBusTopics)))),
                    ("serviceBusTopicName", Ref("nonEmptyString")))),
            ClosedObject(
                Object(
                    ("backend", Const(nameof(SnsTopicBackend.EventGrid))),
                    ("eventGridTopicEndpoint", HttpsUri()),
                    ("eventGridAccessKey", Ref("nonEmptyString"))),
                "backend", "eventGridTopicEndpoint", "eventGridAccessKey"))));

    private static JsonObject StreamSettings() => ClosedObject(
        Object(
            ("eventHubName", Ref("nonEmptyString")),
            ("consumerGroup", Ref("nonEmptyString")),
            ("partitionCount", Object(("type", "integer"), ("minimum", 1)))));

    private static JsonObject NamedMap(JsonNode valueSchema) => Object(
        ("type", "object"),
        ("propertyNames", Object(("minLength", 1), ("pattern", "\\S"))),
        ("additionalProperties", valueSchema));

    private static JsonObject HttpUri() => SchemeUri("https?");

    private static JsonObject HttpsUri() => SchemeUri("https");

    private static JsonObject ServiceBusUri() => SchemeUri("(?:https?|amqps?)");

    private static JsonObject SchemeUri(string schemes) => Object(
        ("type", "string"),
        ("format", "uri"),
        ("pattern",
            $"^{schemes}://" +
            "(?:(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\\.)*" +
            "[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)" +
            "(?::(?:[0-9]{1,4}|[1-5][0-9]{4}|6[0-4][0-9]{3}|" +
            "65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5]))?" +
            "(?:[/?#][^\\s]*)?$"));

    private static JsonObject EventGridDefaultConstraint()
    {
        var snsCondition = Object(
            ("required", Array("defaultBackend")),
            ("properties", Object(
                ("defaultBackend", Const(nameof(SnsTopicBackend.EventGrid))))));
        var servicesCondition = Object(
            ("required", Array("sns")),
            ("properties", Object(("sns", snsCondition))));
        var rootCondition = Object(
            ("required", Array("services")),
            ("properties", Object(("services", servicesCondition))));
        var bindingConstraint = Object(
            ("if", ServiceBusTopicsBindingCondition()),
            ("then", ServiceBusTopicsFallbackRequirement()));
        var rootRequirement = Object(
            ("properties", Object(
                ("bindings", Object(("items", bindingConstraint))))));
        return Object(("if", rootCondition), ("then", rootRequirement));
    }

    private static JsonObject ServiceBusTopicsBindingCondition()
    {
        var snsCondition = Object(
            ("required", Array("kind")),
            ("properties", Object(("kind", Const("serviceBusTopics")))));
        var azureCondition = Object(
            ("required", Array("sns")),
            ("properties", Object(("sns", snsCondition))));
        return Object(
            ("required", Array("azure")),
            ("properties", Object(("azure", azureCondition))));
    }

    private static JsonObject ServiceBusTopicsFallbackRequirement()
    {
        var snsRequirement = Object(("required", Array("eventGridFallback")));
        var azureRequirement = Object(
            ("properties", Object(("sns", snsRequirement))));
        return Object(
            ("properties", Object(("azure", azureRequirement))));
    }

    private static JsonObject Boolean(bool defaultValue) => Object(
        ("type", "boolean"),
        ("default", defaultValue));

    private static JsonObject Enum(string defaultValue, params string[] values) => Object(
        ("type", "string"),
        ("enum", Array(values)),
        ("default", defaultValue));

    private static JsonObject Const(string value) => Object(("const", value));

    private static string Camel(string value) => JsonNamingPolicy.CamelCase.ConvertName(value);

    private static JsonObject Ref(string definition) => Object(("$ref", $"#/$defs/{definition}"));

    private static JsonObject ClosedObject(JsonObject properties, params string[] required)
    {
        var schema = Object(
            ("type", "object"),
            ("additionalProperties", false),
            ("properties", properties));
        if (required.Length > 0)
        {
            schema.Add("required", Array(required));
        }
        return schema;
    }

    private static JsonObject Object(params (string Name, JsonNode? Value)[] properties)
    {
        var result = new JsonObject();
        foreach (var (name, value) in properties)
        {
            result.Add(name, value);
        }
        return result;
    }

    private static JsonArray Array(params object?[] values)
    {
        var result = new JsonArray();
        foreach (var value in values)
        {
            result.Add(value switch
            {
                null => null,
                JsonNode node => node,
                string text => JsonValue.Create(text),
                _ => JsonValue.Create(value),
            });
        }
        return result;
    }

    private static void VerifyContract()
    {
        VerifyProperties(
            ConfigDocumentJsonContext.Default.ConfigDocument,
            "services", "bindings", "azureIdentities");
        VerifyProperties(
            ConfigDocumentJsonContext.Default.ServicesConfig,
            "s3", "sqs", "dynamoDb", "sns", "kinesis", "secretsManager");
        VerifyProperties(ConfigDocumentJsonContext.Default.ServiceToggleConfig, "enabled");
        VerifyProperties(
            ConfigDocumentJsonContext.Default.S3ServiceConfig,
            "enabled", "presignedTrustedSigningHosts");
        VerifyProperties(
            ConfigDocumentJsonContext.Default.SnsServiceConfig,
            "enabled", "defaultBackend");
        VerifyProperties(
            ConfigDocumentJsonContext.Default.DynamoDbServiceConfig,
            "enabled", "useStoredProcedures", "consistencyCheck", "cosmosBinaryResponses",
            "cosmosBinaryRequests", "enableGlobalSecondaryIndexQueries",
            "enableLocalSecondaryIndexNumericOrdering");
        VerifyProperties(ConfigDocumentJsonContext.Default.BindingEntry, "aws", "azure");
        VerifyProperties(
            ConfigDocumentJsonContext.Default.AwsIdentityConfig,
            "accessKeyId", "secretAccessKey");
        VerifyProperties(
            ConfigDocumentJsonContext.Default.AzureBindingSet,
            "s3", "sqs", "dynamoDb", "sns", "kinesis", "secretsManager");
        VerifyProperties(
            ConfigDocumentJsonContext.Default.AzureBackendConfig,
            "kind", "target", "auth", "queues", "topics", "streams",
            "shardIteratorSigningKey", "eventGridFallback");
        VerifyProperties(
            ConfigDocumentJsonContext.Default.AzureTargetConfig,
            "endpoint", "accountName", "namespace", "databaseName", "managementEndpoint",
            "preferredRegions", "transport", "vaultUrl", "topicName");
        VerifyProperties(
            ConfigDocumentJsonContext.Default.AzureAuthConfig,
            "mode", "key", "keyName", "tenantId", "clientId", "clientSecret", "identity");
        VerifyProperties(
            ConfigDocumentJsonContext.Default.AzureIdentity,
            "authMode", "tenantId", "clientId", "clientSecret");
        VerifyProperties(ConfigDocumentJsonContext.Default.SqsQueueSettings, "transport");
        VerifyProperties(
            ConfigDocumentJsonContext.Default.SnsTopicSettings,
            "backend", "serviceBusTopicName", "eventGridTopicEndpoint", "eventGridAccessKey");
        VerifyProperties(
            ConfigDocumentJsonContext.Default.KinesisStreamSettings,
            "eventHubName", "consumerGroup", "partitionCount");
        VerifyEnums();
        VerifyDefaults();
    }

    private static void VerifyProperties(JsonTypeInfo type, params string[] expected)
    {
        if (type.Properties.Count != expected.Length)
        {
            throw ContractMismatch(type, expected);
        }

        foreach (var expectedName in expected)
        {
            var found = false;
            foreach (var property in type.Properties)
            {
                if (property.Name.Equals(expectedName, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw ContractMismatch(type, expected);
            }
        }
    }

    private static InvalidOperationException ContractMismatch(JsonTypeInfo type, string[] expected)
        => new(
            $"ConfigDocument contract changed for {type.Type.FullName}. " +
            $"Update the schema mapping; expected JSON properties: {string.Join(", ", expected)}.");

    private static void VerifyEnums()
    {
        VerifyEnum<AzureAuthKind>(
            nameof(AzureAuthKind.SharedKey),
            nameof(AzureAuthKind.Sas),
            nameof(AzureAuthKind.ManagedIdentity),
            nameof(AzureAuthKind.ClientSecret),
            nameof(AzureAuthKind.WorkloadIdentity),
            nameof(AzureAuthKind.Reference));
        VerifyEnum<AzureAuthMode>(
            nameof(AzureAuthMode.ClientSecret),
            nameof(AzureAuthMode.ManagedIdentity),
            nameof(AzureAuthMode.WorkloadIdentity));
        VerifyEnum<SnsTopicBackend>(
            nameof(SnsTopicBackend.ServiceBusTopics),
            nameof(SnsTopicBackend.EventGrid));
        VerifyEnum<StoredProcedureMode>(
            nameof(StoredProcedureMode.Disabled),
            nameof(StoredProcedureMode.Preferred),
            nameof(StoredProcedureMode.Required));
        VerifyEnum<ConsistencyCheckMode>(
            nameof(ConsistencyCheckMode.Disabled),
            nameof(ConsistencyCheckMode.Warn),
            nameof(ConsistencyCheckMode.Required));
        VerifyEnum<SqsTransport>(
            nameof(SqsTransport.Rest),
            nameof(SqsTransport.Amqp));
    }

    private static void VerifyEnum<TEnum>(params string[] expected)
        where TEnum : struct, Enum
    {
        var actual = System.Enum.GetNames<TEnum>();
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"{typeof(TEnum).FullName} changed. Update the schema mapping; " +
                $"expected values: {string.Join(", ", expected)}.");
        }
    }

    private static void VerifyDefaults()
    {
        var document = new ConfigDocument();
        var sns = new SnsServiceConfig();
        var dynamoDb = new DynamoDbServiceConfig();

        Require(document.Bindings.Count == 0, "ConfigDocument.Bindings");
        Require(document.AzureIdentities is null, "ConfigDocument.AzureIdentities");
        Require(!new ServiceToggleConfig().Enabled, "ServiceToggleConfig.Enabled");
        Require(!new S3ServiceConfig().Enabled, "S3ServiceConfig.Enabled");
        Require(!sns.Enabled, "SnsServiceConfig.Enabled");
        Require(sns.DefaultBackend == SnsTopicBackend.ServiceBusTopics, "SnsServiceConfig.DefaultBackend");
        Require(dynamoDb.UseStoredProcedures == StoredProcedureMode.Disabled, "DynamoDbServiceConfig.UseStoredProcedures");
        Require(dynamoDb.ConsistencyCheck == ConsistencyCheckMode.Disabled, "DynamoDbServiceConfig.ConsistencyCheck");
        Require(!dynamoDb.Enabled, "DynamoDbServiceConfig.Enabled");
        Require(!dynamoDb.CosmosBinaryResponses, "DynamoDbServiceConfig.CosmosBinaryResponses");
        Require(!dynamoDb.CosmosBinaryRequests, "DynamoDbServiceConfig.CosmosBinaryRequests");
        Require(!dynamoDb.EnableGlobalSecondaryIndexQueries, "DynamoDbServiceConfig.EnableGlobalSecondaryIndexQueries");
        Require(!dynamoDb.EnableLocalSecondaryIndexNumericOrdering, "DynamoDbServiceConfig.EnableLocalSecondaryIndexNumericOrdering");
        Require(new AzureAuthConfig().Mode == AzureAuthKind.SharedKey, "AzureAuthConfig.Mode");
        Require(new AzureIdentity().AuthMode == AzureAuthMode.ClientSecret, "AzureIdentity.AuthMode");
        Require(new SnsTopicSettings().Backend == SnsTopicBackend.ServiceBusTopics, "SnsTopicSettings.Backend");
        Require(new SqsQueueSettings().Transport is null, "SqsQueueSettings.Transport");

        var sqsDocument = new ConfigDocument
        {
            Bindings =
            {
                new BindingEntry
                {
                    Azure = new AzureBindingSet
                    {
                        Sqs = new AzureBackendConfig
                        {
                            Kind = "serviceBus",
                            Auth = new AzureAuthConfig { Mode = AzureAuthKind.Sas },
                        },
                    },
                },
            },
        };
        var translatedSqs = ConfigDocumentTranslator.ToProxyConfig(sqsDocument)
            .Credentials[0].Azure.ServiceBus;
        Require(translatedSqs?.Transport == SqsTransport.Rest, "SQS effective transport");
    }

    private static void Require(bool condition, string member)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                $"{member} default changed. Update the normative configuration schema.");
        }
    }

    private const string CompleteExample = """
    {
      "services": {
        "s3": {
          "enabled": true,
          "presignedTrustedSigningHosts": [ "s3.us-east-1.amazonaws.com" ]
        },
        "sqs": { "enabled": true },
        "dynamodb": {
          "enabled": true,
          "useStoredProcedures": "Preferred",
          "consistencyCheck": "Warn",
          "cosmosBinaryResponses": false,
          "cosmosBinaryRequests": false,
          "enableGlobalSecondaryIndexQueries": true,
          "enableLocalSecondaryIndexNumericOrdering": false
        },
        "sns": { "enabled": true, "defaultBackend": "ServiceBusTopics" },
        "kinesis": { "enabled": true },
        "secretsmanager": { "enabled": true }
      },
      "azureIdentities": {
        "operator-mi": {
          "authMode": "managedIdentity",
          "clientId": "00000000-0000-0000-0000-000000000000"
        }
      },
      "bindings": [
        {
          "aws": {
            "accessKeyId": "AKIAEXAMPLE",
            "secretAccessKey": "replace-me"
          },
          "azure": {
            "s3": {
              "kind": "blob",
              "target": { "accountName": "storageaccount" },
              "auth": { "mode": "sharedKey", "key": "replace-me" }
            },
            "sqs": {
              "kind": "serviceBus",
              "target": { "namespace": "queue-ns", "transport": "Amqp" },
              "auth": { "mode": "sas", "keyName": "RootManageSharedAccessKey", "key": "replace-me" },
              "queues": { "orders": { "transport": "Rest" } }
            },
            "dynamodb": {
              "kind": "cosmos",
              "target": {
                "endpoint": "https://account.documents.azure.com/",
                "databaseName": "orders",
                "preferredRegions": [ "West US", "East US" ]
              },
              "auth": { "mode": "reference", "identity": "operator-mi" }
            },
            "sns": {
              "kind": "serviceBusTopics",
              "target": { "namespace": "topic-ns" },
              "auth": { "mode": "sas", "keyName": "RootManageSharedAccessKey", "key": "replace-me" },
              "topics": {
                "orders-*": { "backend": "EventGrid", "serviceBusTopicName": "orders" }
              },
              "eventGridFallback": {
                "kind": "eventGrid",
                "target": { "endpoint": "https://orders.westus-1.eventgrid.azure.net/api/events" },
                "auth": { "mode": "sharedKey", "key": "replace-me" }
              }
            },
            "kinesis": {
              "kind": "eventHubs",
              "target": { "namespace": "events-ns" },
              "auth": { "mode": "reference", "identity": "operator-mi" },
              "shardIteratorSigningKey": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
              "streams": {
                "orders": {
                  "eventHubName": "orders-v1",
                  "consumerGroup": "aws-readers",
                  "partitionCount": 4
                }
              }
            },
            "secretsmanager": {
              "kind": "keyVault",
              "target": { "vaultUrl": "https://operator-vault.vault.azure.net/" },
              "auth": { "mode": "reference", "identity": "operator-mi" }
            }
          }
        }
      ]
    }
    """;
}
