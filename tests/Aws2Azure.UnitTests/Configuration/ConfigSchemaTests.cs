using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Aws2Azure.ConfigSchema;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Core.SigV4;
using Json.Schema;
using YamlDotNet.RepresentationModel;

namespace Aws2Azure.UnitTests.Configuration;

[Collection("EnvironmentVariables")]
public sealed class ConfigSchemaTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    // JsonSchema.Net 9.x throws if the same $id is registered twice against
    // the shared Global registry. This schema's $id is also loaded
    // independently by ConfigExampleValidator (DocsQualityTests), so build
    // against a private registry here to avoid a cross-test collision.
    private static readonly JsonSchema Schema = JsonSchema.FromText(
        File.ReadAllText(Path.Combine(RepoRoot, ConfigSchemaGenerator.ArtifactRelativePath)),
        new BuildOptions { SchemaRegistry = new SchemaRegistry() });
    private static readonly EvaluationOptions EvaluationOptions = new()
    {
        RequireFormatValidation = true,
    };

    [Fact]
    public void Generated_schema_matches_committed_artifact()
    {
        var committed = File.ReadAllText(
            Path.Combine(RepoRoot, ConfigSchemaGenerator.ArtifactRelativePath));

        Assert.Equal(committed, ConfigSchemaGenerator.Generate());
    }

    [Fact]
    public void Generated_configuration_reference_matches_committed_artifact()
    {
        var committed = File.ReadAllText(
            Path.Combine(RepoRoot, ConfigurationReferenceGenerator.ArtifactRelativePath));

        Assert.Equal(committed, ConfigurationReferenceGenerator.Generate());
    }

    [Fact]
    public void Generated_configuration_reference_contains_every_source_generated_config_property()
    {
        var generated = ConfigurationReferenceGenerator.Generate();
        var documentedNames = generated
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static line => line.StartsWith("| `", StringComparison.Ordinal))
            .Select(static line => line.Split('`')[1])
            .Select(static path => path.Split('.').Last().Replace("[]", "", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var type in ConfigContractTypes())
        {
            foreach (var property in type.Properties)
            {
                Assert.Contains(property.Name, documentedNames);
            }
        }
    }

    [Fact]
    public void Generated_configuration_reference_contains_cross_field_sns_requirements()
    {
        var generated = ConfigurationReferenceGenerator.Generate();

        Assert.Contains(
            "every serviceBusTopics SNS binding requires eventGridFallback",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "requires both eventGridTopicEndpoint and eventGridAccessKey",
            generated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_configuration_reference_contains_every_map_key_constraint()
    {
        var generated = ConfigurationReferenceGenerator.Generate();
        var mapRows = generated
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static line => line.StartsWith("| `", StringComparison.Ordinal))
            .Where(static line => line.Split('`')[1].EndsWith(".<name>", StringComparison.Ordinal))
            .GroupBy(static line => line.Split('`')[1], StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var schema = JsonNode.Parse(ConfigSchemaGenerator.Generate())!;
        var propertyNameSchemas = new List<JsonObject>();
        CollectPropertyNameSchemas(schema, propertyNameSchemas);

        Assert.NotEmpty(propertyNameSchemas);
        Assert.All(mapRows.Values, static row => Assert.Contains("Map key:", row, StringComparison.Ordinal));
        foreach (var propertyNames in propertyNameSchemas)
        {
            if (propertyNames["minLength"] is not null)
            {
                Assert.Contains(
                    mapRows.Values,
                    static row => row.Contains("Minimum length 1.", StringComparison.Ordinal));
            }
            if (propertyNames["pattern"] is not null)
            {
                Assert.Contains(
                    mapRows.Values,
                    static row =>
                        row.Contains(
                            "Map key: Must contain a non-whitespace character.",
                            StringComparison.Ordinal));
            }
        }
        Assert.Contains(
            "Map key: Minimum length 1.",
            mapRows["azureIdentities.<name>"],
            StringComparison.Ordinal);
    }

    [Fact]
    public void Environment_reference_contains_every_process_environment_variable()
    {
        var documented = File.ReadAllText(
            Path.Combine(RepoRoot, "docs", "configuration-environment.md"));
        var source = string.Join(
            '\n',
            Directory.EnumerateFiles(
                    Path.Combine(RepoRoot, "src"),
                    "*.cs",
                    SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        var directReads = Regex.Matches(
            source,
            "GetEnvironmentVariable\\(\"(?<name>[A-Z][A-Z0-9_]+)\"\\)");
        var namedConstants = Regex.Matches(
            source,
            "EnvironmentVariable\\s*=\\s*\"(?<name>[A-Z][A-Z0-9_]+)\"");
        var names = directReads
            .Concat(namedConstants)
            .Select(static match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(names);
        foreach (var name in names)
        {
            Assert.Contains($"`{name}`", documented, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Troubleshooting_reference_uses_protocol_specific_clock_skew_errors()
    {
        var documented = File.ReadAllText(Path.Combine(RepoRoot, "docs", "troubleshooting.md"));
        var xmlSkew = AuthErrorVocabulary.Resolve(
            AwsAuthErrorDialect.S3Xml,
            SigV4ValidationStatus.ClockSkewTooLarge);
        var jsonSkew = AuthErrorVocabulary.Resolve(
            AwsAuthErrorDialect.Json,
            SigV4ValidationStatus.ClockSkewTooLarge);
        var xmlMismatch = AuthErrorVocabulary.Resolve(
            AwsAuthErrorDialect.S3Xml,
            SigV4ValidationStatus.InvalidSignature);

        Assert.Contains($"`{xmlSkew.Code}`", documented, StringComparison.Ordinal);
        Assert.Contains($"`{jsonSkew.Code}`", documented, StringComparison.Ordinal);
        Assert.Contains($"`{xmlMismatch.Code}`", documented, StringComparison.Ordinal);
        Assert.NotEqual(xmlMismatch.Code, xmlSkew.Code);
    }

    [Fact]
    public void Production_documentation_examples_cover_all_backends_and_auth_modes()
    {
        var exampleDirectory = Path.Combine(RepoRoot, "docs", "configuration", "examples");
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        var modes = new HashSet<string>(StringComparer.Ordinal);
        var previousTenant = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        var previousClient = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var previousToken = Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AZURE_TENANT_ID", "documentation-tenant");
            Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", "documentation-client");
            Environment.SetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE", "documentation-token-file");

            foreach (var path in Directory.EnumerateFiles(exampleDirectory, "*.json").Order())
            {
                var instance = JsonNode.Parse(File.ReadAllText(path))
                    ?? throw new InvalidDataException($"{path} is empty.");
                AssertValid(instance);
                CollectStringProperties(instance, "kind", kinds);
                CollectStringProperties(instance, "mode", modes);
                ValidateRuntime(instance);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_TENANT_ID", previousTenant);
            Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", previousClient);
            Environment.SetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE", previousToken);
        }

        Assert.Equal(
            new[] { "blob", "cosmos", "eventGrid", "eventHubs", "keyVault", "serviceBus", "serviceBusTopics" },
            kinds.Order());
        Assert.Equal(
            new[] { "clientSecret", "managedIdentity", "reference", "sas", "sharedKey", "workloadIdentity" },
            modes.Order());
    }

    [Fact]
    public void Queue_transport_has_no_schema_default_because_it_inherits_backend_transport()
    {
        var generated = JsonNode.Parse(ConfigSchemaGenerator.Generate())!;
        var transport = generated["$defs"]!["queueSettings"]!["properties"]!["transport"]!;

        Assert.DoesNotContain("\"default\"", transport.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_contains_every_source_generated_config_property()
    {
        var generated = ConfigSchemaGenerator.Generate();
        foreach (var type in ConfigContractTypes())
        {
            foreach (var property in type.Properties)
            {
                Assert.Contains($"\"{property.Name}\"", generated, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Source_context_serializes_canonical_names_and_defaults()
    {
        var document = new ConfigDocument
        {
            Services = new ServicesConfig
            {
                DynamoDb = new DynamoDbServiceConfig(),
                SecretsManager = new ServiceToggleConfig(),
            },
            AzureIdentities = new Dictionary<string, AzureIdentity>
            {
                ["default-client-secret"] = new()
                {
                    TenantId = "tenant",
                    ClientId = "client",
                    ClientSecret = "secret",
                },
            },
        };
        document.Bindings.Add(new BindingEntry
        {
            Aws = new AwsIdentityConfig
            {
                AccessKeyId = "AKIA",
                SecretAccessKey = "secret",
            },
            Azure = new AzureBindingSet
            {
                S3 = new AzureBackendConfig
                {
                    Kind = "blob",
                    Target = new AzureTargetConfig { AccountName = "account" },
                    Auth = new AzureAuthConfig { Key = "key" },
                },
            },
        });

        var json = JsonSerializer.Serialize(
            document,
            ConfigDocumentJsonContext.Default.ConfigDocument);
        var serialized = JsonNode.Parse(json)!.AsObject();
        var services = serialized["services"]!.AsObject();
        var identity = serialized["azureIdentities"]!["default-client-secret"]!.AsObject();
        var auth = serialized["bindings"]![0]!["azure"]!["s3"]!["auth"]!.AsObject();

        Assert.True(services.ContainsKey("dynamodb"));
        Assert.True(services.ContainsKey("secretsmanager"));
        Assert.False(services.ContainsKey("dynamoDb"));
        Assert.False(services.ContainsKey("secretsManager"));
        Assert.Equal("clientSecret", identity["authMode"]!.GetValue<string>());
        Assert.Equal("sharedKey", auth["mode"]!.GetValue<string>());
        AssertValid(serialized);
    }

    [Fact]
    public void Canonical_schema_rejects_legacy_names_while_runtime_preserves_meaning()
    {
        var legacy = JsonNode.Parse("""
        {
          "BINDINGS": [
            {
              "AWS": {
                "AccessKeyId": "AKIA",
                "SecretAccessKey": "secret",
                "extension": true
              },
              "AZURE": {
                "S3": {
                  "Kind": "BLOB",
                  "Target": { "AccountName": "account", "extension": 1 },
                  "Auth": { "Key": "key" },
                  "extension": "ignored"
                }
              },
              "extension": {}
            }
          ],
          "extension": true
        }
        """)!;

        Assert.False(Evaluate(legacy).IsValid);
        var document = JsonSerializer.Deserialize(
            legacy.ToJsonString(),
            ConfigDocumentJsonContext.Default.ConfigDocument)!;
        var config = ConfigDocumentTranslator.ToProxyConfig(document);
        var credential = Assert.Single(config.Credentials);
        Assert.Equal("AKIA", credential.AwsAccessKeyId);
        Assert.Equal("secret", credential.AwsSecretAccessKey);
        Assert.Equal("account", credential.Azure.Blob!.AccountName);
        Assert.Equal("key", credential.Azure.Blob.AccountKey);
    }

    [Fact]
    public void Legacy_standalone_event_grid_matches_canonical_fallback_migration()
    {
        const string legacyJson = """
        {
          "bindings": [{
            "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
            "azure": {
              "sns": {
                "kind": " EVENTGRID ",
                "target": { "endpoint": "https://orders.westus-1.eventgrid.azure.net/api/events" },
                "auth": { "mode": "sharedKey", "key": "event-grid-key" }
              }
            }
          }]
        }
        """;
        const string canonicalJson = """
        {
          "bindings": [{
            "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
            "azure": {
              "sns": {
                "kind": "serviceBusTopics",
                "target": { "namespace": "orders-service-bus" },
                "auth": { "mode": "sas", "keyName": "Root", "key": "service-bus-key" },
                "eventGridFallback": {
                  "kind": "eventGrid",
                  "target": { "endpoint": "https://orders.westus-1.eventgrid.azure.net/api/events" },
                  "auth": { "mode": "sharedKey", "key": "event-grid-key" }
                }
              }
            }
          }]
        }
        """;

        Assert.False(Evaluate(JsonNode.Parse(legacyJson)!).IsValid);
        AssertValid(JsonNode.Parse(canonicalJson)!);
        var legacyDocument = JsonSerializer.Deserialize(
            legacyJson,
            ConfigDocumentJsonContext.Default.ConfigDocument)!;
        var canonicalDocument = JsonSerializer.Deserialize(
            canonicalJson,
            ConfigDocumentJsonContext.Default.ConfigDocument)!;
        var legacyConfig = ConfigDocumentTranslator.ToProxyConfig(legacyDocument);
        var canonicalConfig = ConfigDocumentTranslator.ToProxyConfig(canonicalDocument);
        ProxyConfigValidator.Validate(legacyConfig);
        ProxyConfigValidator.Validate(canonicalConfig);
        var legacy = Assert.IsType<EventGridCredentials>(
            Assert.Single(legacyConfig.Credentials).Azure.EventGrid);
        var canonical = Assert.IsType<EventGridCredentials>(
            Assert.Single(canonicalConfig.Credentials).Azure.EventGrid);

        Assert.Equal(canonical.Endpoint, legacy.Endpoint);
        Assert.Equal(canonical.AccessKey, legacy.AccessKey);
        var serialized = JsonNode.Parse(JsonSerializer.Serialize(
            legacyDocument,
            ConfigDocumentJsonContext.Default.ConfigDocument))!;
        Assert.Equal(
            "eventGrid",
            serialized["bindings"]![0]!["azure"]!["sns"]!["kind"]!.GetValue<string>());
        Assert.False(Evaluate(serialized).IsValid);
    }

    [Fact]
    public void Defaulted_auth_discriminators_are_valid_in_schema_and_runtime()
    {
        var instance = JsonNode.Parse("""
        {
          "azureIdentities": {
            "client-secret": {
              "tenantId": "tenant",
              "clientId": "client",
              "clientSecret": "secret"
            }
          },
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "s3": {
                  "kind": "BLOB",
                  "target": { "accountName": "account" },
                  "auth": { "key": "key" }
                }
              }
            }
          ]
        }
        """)!;

        AssertValid(instance);
        var document = JsonSerializer.Deserialize(
            instance.ToJsonString(),
            ConfigDocumentJsonContext.Default.ConfigDocument)!;
        Assert.Equal(AzureAuthKind.SharedKey, document.Bindings[0].Azure.S3!.Auth.Mode);
        Assert.Equal(AzureAuthMode.ClientSecret, document.AzureIdentities!["client-secret"].AuthMode);
        ProxyConfigValidator.Validate(ConfigDocumentTranslator.ToProxyConfig(document));
    }

    [Fact]
    public void Nullable_optional_properties_are_valid_in_schema_and_runtime()
    {
        var instance = JsonNode.Parse("""
        {
          "services": { "sqs": null },
          "azureIdentities": null,
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "s3": {
                  "kind": "blob",
                  "target": {
                    "accountName": "account",
                    "endpoint": null,
                    "namespace": null
                  },
                  "auth": { "key": "key", "clientSecret": null },
                  "queues": null
                },
                "kinesis": null
              }
            }
          ]
        }
        """)!;

        AssertValid(instance);
        var document = JsonSerializer.Deserialize(
            instance.ToJsonString(),
            ConfigDocumentJsonContext.Default.ConfigDocument)!;
        ProxyConfigValidator.Validate(ConfigDocumentTranslator.ToProxyConfig(document));
    }

    [Fact]
    public void Nullable_sns_topics_are_treated_as_omitted()
    {
        var instance = ServiceBusTopicsConfig(
            services: null,
            topic: null,
            fallback: null).AsObject();
        instance["bindings"]![0]!["azure"]!["sns"]!["topics"] = null;

        AssertValid(instance);
        var document = JsonSerializer.Deserialize(
            instance.ToJsonString(),
            ConfigDocumentJsonContext.Default.ConfigDocument)!;
        ProxyConfigValidator.Validate(ConfigDocumentTranslator.ToProxyConfig(document));
    }

    [Fact]
    public void Null_known_fields_in_conditional_shapes_are_treated_as_omitted()
    {
        var instance = ServiceBusTopicsConfig(
            services: """
            "services": { "sns": null },
            """,
            topic: """
            {
              "backend": "ServiceBusTopics",
              "eventGridTopicEndpoint": null,
              "eventGridAccessKey": null
            }
            """,
            fallback: null).AsObject();
        instance["azureIdentities"] = JsonNode.Parse("""
        {
          "managed": {
            "authMode": "managedIdentity",
            "tenantId": null,
            "clientSecret": null
          }
        }
        """);

        AssertValid(instance);
        var document = JsonSerializer.Deserialize(
            instance.ToJsonString(),
            ConfigDocumentJsonContext.Default.ConfigDocument)!;
        ProxyConfigValidator.Validate(ConfigDocumentTranslator.ToProxyConfig(document));
    }

    [Fact]
    public void Backend_specific_extra_fields_are_rejected_by_schema_but_ignored_at_runtime()
    {
        var instance = JsonNode.Parse("""
        {
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "s3": {
                  "kind": "blob",
                  "target": { "accountName": "account", "namespace": "not-blob" },
                  "auth": { "key": "key", "clientSecret": "not-shared-key" },
                  "queues": {}
                }
              }
            }
          ]
        }
        """)!;

        Assert.False(Evaluate(instance).IsValid);
        var document = JsonSerializer.Deserialize(
            instance.ToJsonString(),
            ConfigDocumentJsonContext.Default.ConfigDocument)!;
        var config = ConfigDocumentTranslator.ToProxyConfig(document);
        ProxyConfigValidator.Validate(config);
        var blob = Assert.IsType<BlobCredentials>(
            Assert.Single(config.Credentials).Azure.Blob);
        Assert.Equal("account", blob.AccountName);
        Assert.Equal("key", blob.AccountKey);
    }

    [Theory]
    [InlineData("""{ "services": { "dynamodb": { "useStoredProcedures": "1" } }, "bindings": [] }""")]
    [InlineData("""{ "services": { "dynamodb": { "useStoredProcedures": " Preferred " } }, "bindings": [] }""")]
    [InlineData("""{ "services": { "dynamodb": { "useStoredProcedures": "Preferred\n" } }, "bindings": [] }""")]
    public void Legacy_numeric_strings_or_whitespace_padded_enum_names_are_runtime_compatible(string json)
    {
        var instance = JsonNode.Parse(json)!;

        Assert.False(Evaluate(instance).IsValid);
        var document = JsonSerializer.Deserialize(
            json,
            ConfigDocumentJsonContext.Default.ConfigDocument)!;
        var config = ConfigDocumentTranslator.ToProxyConfig(document);
        Assert.Equal(StoredProcedureMode.Preferred, config.DynamoDb.UseStoredProcedures);
    }

    [Theory]
    [InlineData("999")]
    [InlineData("Disabled, Preferred")]
    public void Undefined_or_compound_enum_strings_are_rejected(string value)
    {
        var json = $$"""
        { "services": { "dynamodb": { "useStoredProcedures": "{{value}}" } }, "bindings": [] }
        """;

        Assert.False(Evaluate(JsonNode.Parse(json)!).IsValid);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            json,
            ConfigDocumentJsonContext.Default.ConfigDocument));
    }

    [Fact]
    public void Legacy_numeric_enums_and_whitespace_kind_preserve_translated_meaning()
    {
        const string legacyJson = """
        {
          "services": {
            "dynamodb": {
              "useStoredProcedures": 1,
              "consistencyCheck": 1
            }
          },
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "s3": {
                  "kind": " BLOB ",
                  "target": { "accountName": "account" },
                  "auth": { "mode": 0, "key": "key" }
                }
              }
            }
          ]
        }
        """;
        const string canonicalJson = """
        {
          "services": {
            "dynamodb": {
              "useStoredProcedures": "Preferred",
              "consistencyCheck": "Warn"
            }
          },
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "s3": {
                  "kind": "blob",
                  "target": { "accountName": "account" },
                  "auth": { "mode": "sharedKey", "key": "key" }
                }
              }
            }
          ]
        }
        """;

        Assert.False(Evaluate(JsonNode.Parse(legacyJson)!).IsValid);
        var legacyDocument = JsonSerializer.Deserialize(
            legacyJson,
            ConfigDocumentJsonContext.Default.ConfigDocument)!;
        var canonicalDocument = JsonSerializer.Deserialize(
            canonicalJson,
            ConfigDocumentJsonContext.Default.ConfigDocument)!;
        var legacy = ConfigDocumentTranslator.ToProxyConfig(legacyDocument);
        var canonical = ConfigDocumentTranslator.ToProxyConfig(canonicalDocument);

        Assert.Equal(canonical.DynamoDb.UseStoredProcedures, legacy.DynamoDb.UseStoredProcedures);
        Assert.Equal(canonical.DynamoDb.ConsistencyCheck, legacy.DynamoDb.ConsistencyCheck);
        var legacyCredential = Assert.Single(legacy.Credentials);
        var canonicalCredential = Assert.Single(canonical.Credentials);
        Assert.Equal(canonicalCredential.Azure.Blob!.AccountName, legacyCredential.Azure.Blob!.AccountName);
        Assert.Equal(canonicalCredential.Azure.Blob.AccountKey, legacyCredential.Azure.Blob.AccountKey);

        var serialized = JsonSerializer.Serialize(
            legacyDocument,
            ConfigDocumentJsonContext.Default.ConfigDocument);
        Assert.Contains("\"useStoredProcedures\":\"Preferred\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"consistencyCheck\":\"Warn\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"sharedKey\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"blob\"", serialized, StringComparison.Ordinal);
        AssertValid(JsonNode.Parse(serialized)!);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    public void Undefined_numeric_enum_values_are_rejected(int value)
    {
        var json = $$"""
        {
          "services": {
            "dynamodb": { "useStoredProcedures": {{value}} }
          },
          "bindings": []
        }
        """;

        Assert.False(Evaluate(JsonNode.Parse(json)!).IsValid);
        var exception = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(
                json,
                ConfigDocumentJsonContext.Default.ConfigDocument));
        Assert.Contains("must name a defined member", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_binding_is_rejected_with_a_configuration_error()
    {
        var instance = JsonNode.Parse("""{ "bindings": [ null ] }""")!;

        Assert.False(Evaluate(instance).IsValid);
        var document = JsonSerializer.Deserialize(
            instance.ToJsonString(),
            ConfigDocumentJsonContext.Default.ConfigDocument)!;
        var exception = Assert.Throws<ProxyConfigException>(
            () => ConfigDocumentTranslator.ToProxyConfig(document));
        Assert.Contains("bindings[0]", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""
        {
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "dynamodb": {
                  "kind": "cosmos",
                  "target": { "endpoint": "not-a-uri", "databaseName": "db" },
                  "auth": { "key": "key" }
                }
              }
            }
          ]
        }
        """)]
    [InlineData("""
        {
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "s3": {
                  "kind": "blob",
                  "target": { "accountName": "account", "endpoint": "https://example.com/a b" },
                  "auth": { "key": "key" }
                }
              }
            }
          ]
        }
        """)]
    public void Schema_and_runtime_reject_invalid_absolute_uris(string json)
    {
        var instance = JsonNode.Parse(json)!;

        Assert.False(Evaluate(instance).IsValid);
        var document = JsonSerializer.Deserialize(
            json,
            ConfigDocumentJsonContext.Default.ConfigDocument)!;
        Assert.Throws<ProxyConfigException>(
            () => ProxyConfigValidator.Validate(
                ConfigDocumentTranslator.ToProxyConfig(document)));
    }

    [Fact]
    public void Uppercase_schemes_and_ipv6_literals_are_valid_absolute_uris()
    {
        var instance = JsonNode.Parse("""
        {
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "s3": {
                  "kind": "blob",
                  "target": {
                    "accountName": "account",
                    "endpoint": "HTTP://[::1]:10000/devstoreaccount1"
                  },
                  "auth": { "key": "key" }
                }
              }
            }
          ]
        }
        """)!;

        AssertValid(instance);
        var document = JsonSerializer.Deserialize(
            instance.ToJsonString(),
            ConfigDocumentJsonContext.Default.ConfigDocument)!;
        ProxyConfigValidator.Validate(ConfigDocumentTranslator.ToProxyConfig(document));
    }

    [Theory]
    [InlineData("https://[foo]")]
    [InlineData("https://.")]
    [InlineData("https://foo@")]
    [InlineData("https://user@@host")]
    [InlineData("https://[::::]")]
    [InlineData("https://a..b")]
    [InlineData("https://a%")]
    public void Uri_pattern_rejects_malformed_authorities_without_format_assertion(
        string endpoint)
    {
        var instance = JsonNode.Parse($$"""
        {
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "s3": {
                  "kind": "blob",
                  "target": {
                    "accountName": "account",
                    "endpoint": "{{endpoint}}"
                  },
                  "auth": { "key": "key" }
                }
              }
            }
          ]
        }
        """)!;
        var annotationOnlyOptions = new EvaluationOptions
        {
            RequireFormatValidation = false,
        };

        Assert.False(Schema.Evaluate(ToElement(instance), annotationOnlyOptions).IsValid);
        Assert.Throws<ProxyConfigException>(() => ValidateRuntime(instance));
    }

    [Fact]
    public void Uri_pattern_accepts_runtime_valid_leading_zero_port_without_format_assertion()
    {
        var instance = JsonNode.Parse("""
        {
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "s3": {
                  "kind": "blob",
                  "target": {
                    "accountName": "account",
                    "endpoint": "https://host:00080"
                  },
                  "auth": { "key": "key" }
                }
              }
            }
          ]
        }
        """)!;
        var annotationOnlyOptions = new EvaluationOptions
        {
            RequireFormatValidation = false,
        };

        Assert.True(Schema.Evaluate(ToElement(instance), annotationOnlyOptions).IsValid);
        ValidateRuntime(instance);
    }

    [Fact]
    public void Uri_pattern_accepts_runtime_valid_empty_port_without_format_assertion()
    {
        var instance = JsonNode.Parse("""
        {
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "s3": {
                  "kind": "blob",
                  "target": {
                    "accountName": "account",
                    "endpoint": "https://host:"
                  },
                  "auth": { "key": "key" }
                }
              }
            }
          ]
        }
        """)!;
        var annotationOnlyOptions = new EvaluationOptions
        {
            RequireFormatValidation = false,
        };

        Assert.True(Schema.Evaluate(ToElement(instance), annotationOnlyOptions).IsValid);
        ValidateRuntime(instance);
    }

    [Theory]
    [InlineData("HTTP://[fe80::1%eth0]:10000/path")]
    [InlineData("HTTP://[fe80::1%25eth0]:10000/path")]
    [InlineData("")]
    public void Optional_uri_schema_accepts_runtime_compatible_values(string endpoint)
    {
        var instance = JsonNode.Parse($$"""
        {
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "s3": {
                  "kind": "blob",
                  "target": {
                    "accountName": "account",
                    "endpoint": "{{endpoint}}"
                  },
                  "auth": { "key": "key" }
                }
              }
            }
          ]
        }
        """)!;

        AssertValid(instance);
        ValidateRuntime(instance);
    }

    [Fact]
    public void Complete_all_services_example_is_valid()
    {
        var schemaDocument = JsonNode.Parse(ConfigSchemaGenerator.Generate())!.AsObject();
        var example = schemaDocument["examples"]!.AsArray()[0]!;

        AssertValid(example);
        var document = JsonSerializer.Deserialize(
            example.ToJsonString(),
            ConfigDocumentJsonContext.Default.ConfigDocument);
        Assert.NotNull(document);
        ProxyConfigValidator.Validate(ConfigDocumentTranslator.ToProxyConfig(document));
    }

    [Fact]
    public void Committed_operator_examples_are_valid()
    {
        AssertValid(JsonNode.Parse(File.ReadAllText(
            Path.Combine(RepoRoot, "src", "Aws2Azure.Proxy", "config.json")))!);
        AssertValid(JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "docker", "config.json")))!);
        AssertValid(ReadHelmConfigContent(
            Path.Combine(RepoRoot, "deploy", "helm", "aws2azure", "values.yaml")));
        AssertValid(ReadKubernetesConfigJson(
            Path.Combine(RepoRoot, "deploy", "sidecar", "secret.yaml")));
        AssertValid(ReadKubernetesConfigJson(
            Path.Combine(RepoRoot, "deploy", "sidecar", "demo-azurite.yaml")));
    }

    [Fact]
    public void Bundled_operator_config_is_separate_from_host_settings_and_runtime_loadable()
    {
        var operatorConfigPath = Path.Combine(
            RepoRoot, "src", "Aws2Azure.Proxy", "config.json");
        var hostSettings = JsonNode.Parse(File.ReadAllText(
            Path.Combine(RepoRoot, "src", "Aws2Azure.Proxy", "appsettings.json")))!.AsObject();
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot, "Dockerfile"));

        Assert.False(hostSettings.ContainsKey("services"));
        Assert.False(hostSettings.ContainsKey("bindings"));
        Assert.Contains("COPY --from=build /app/config.json .", dockerfile, StringComparison.Ordinal);
        Assert.Contains("COPY --from=build /app/appsettings.json .", dockerfile, StringComparison.Ordinal);

        var config = ProxyConfigLoader.Load(
            operatorConfigPath,
            envVars: new Dictionary<string, string?>());

        Assert.True(config.Services["s3"].Enabled);
        Assert.Single(config.Credentials);
    }

    [Theory]
    [InlineData("s3", "blob", "sas")]
    [InlineData("sqs", "serviceBus", "sharedKey")]
    [InlineData("dynamodb", "cosmos", "sas")]
    [InlineData("kinesis", "eventHubs", "sharedKey")]
    [InlineData("secretsmanager", "keyVault", "sharedKey")]
    public void Invalid_backend_auth_combinations_are_rejected(
        string service,
        string kind,
        string mode)
    {
        var instance = JsonNode.Parse($$"""
        {
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIAEXAMPLE", "secretAccessKey": "secret" },
              "azure": {
                "{{service}}": {
                  "kind": "{{kind}}",
                  "target": {},
                  "auth": { "mode": "{{mode}}", "key": "key", "keyName": "name" }
                }
              }
            }
          ]
        }
        """)!;

        Assert.False(Evaluate(instance).IsValid);
    }

    [Fact]
    public void Sns_event_grid_fallback_is_only_valid_on_service_bus_topics()
    {
        var invalid = JsonNode.Parse("""
        {
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIAEXAMPLE", "secretAccessKey": "secret" },
              "azure": {
                "sns": {
                  "kind": "eventGrid",
                  "target": { "endpoint": "https://topic.westus-1.eventgrid.azure.net/api/events" },
                  "auth": { "mode": "sharedKey", "key": "key" },
                  "eventGridFallback": {
                    "kind": "eventGrid",
                    "target": { "endpoint": "https://fallback.westus-1.eventgrid.azure.net/api/events" },
                    "auth": { "mode": "sharedKey", "key": "key" }
                  }
                }
              }
            }
          ]
        }
        """)!;

        Assert.False(Evaluate(invalid).IsValid);
    }

    [Fact]
    public void Whitespace_only_required_values_are_rejected()
    {
        var invalid = JsonNode.Parse("""
        {
          "bindings": [
            {
              "aws": { "accessKeyId": " ", "secretAccessKey": "\t" },
              "azure": {}
            }
          ]
        }
        """)!;

        Assert.False(Evaluate(invalid).IsValid);
    }

    [Fact]
    public void Malformed_backend_uris_are_rejected_without_optional_format_assertions()
    {
        var invalidS3 = JsonNode.Parse("""
        {
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "s3": {
                  "kind": "blob",
                  "target": { "accountName": "account", "endpoint": "http://" },
                  "auth": { "mode": "sharedKey", "key": "key" }
                }
              }
            }
          ]
        }
        """)!;
        var invalidCosmos = JsonNode.Parse("""
        {
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "dynamodb": {
                  "kind": "cosmos",
                  "target": { "endpoint": "not a uri", "databaseName": "db" },
                  "auth": { "mode": "sharedKey", "key": "key" }
                }
              }
            }
          ]
        }
        """)!;
        var invalidAuthority = JsonNode.Parse("""
        {
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "s3": {
                  "kind": "blob",
                  "target": { "accountName": "account", "endpoint": "https://[" },
                  "auth": { "mode": "sharedKey", "key": "key" }
                }
              }
            }
          ]
        }
        """)!;

        Assert.False(Evaluate(invalidS3).IsValid);
        Assert.False(Evaluate(invalidCosmos).IsValid);
        Assert.False(Evaluate(invalidAuthority).IsValid);
    }

    [Fact]
    public void Shard_iterator_signing_key_requires_at_least_32_decoded_bytes()
    {
        var tooShort = KinesisConfig(Convert.ToBase64String(new byte[31]));
        var minimum = KinesisConfig(Convert.ToBase64String(new byte[32]));
        var malformed = KinesisConfig("not-base64");

        Assert.False(Evaluate(tooShort).IsValid);
        Assert.False(Evaluate(malformed).IsValid);
        AssertValid(minimum);
        Assert.Throws<ProxyConfigException>(() => ValidateRuntime(tooShort));
        Assert.Throws<ProxyConfigException>(() => ValidateRuntime(malformed));
        ValidateRuntime(minimum);
    }

    [Fact]
    public void Sns_event_grid_topic_without_fallback_requires_complete_overrides()
    {
        var invalid = ServiceBusTopicsConfig(
            services: null,
            topic: """
            { "backend": "EventGrid" }
            """,
            fallback: null);
        var valid = ServiceBusTopicsConfig(
            services: null,
            topic: """
            {
              "backend": "EventGrid",
              "eventGridTopicEndpoint": "https://orders.westus-1.eventgrid.azure.net/api/events",
              "eventGridAccessKey": "key"
            }
            """,
            fallback: null);

        Assert.False(Evaluate(invalid).IsValid);
        AssertValid(valid);
    }

    [Theory]
    [InlineData("""
        {
          "backend": "ServiceBusTopics",
          "eventGridTopicEndpoint": "https://orders.westus-1.eventgrid.azure.net/api/events"
        }
        """)]
    [InlineData("""
        {
          "backend": "EventGrid",
          "serviceBusTopicName": "orders"
        }
        """)]
    public void Topic_backend_rejects_fields_from_the_other_backend(string topic)
    {
        var fallback = """
        {
          "kind": "eventGrid",
          "target": { "endpoint": "https://fallback.westus-1.eventgrid.azure.net/api/events" },
          "auth": { "key": "key" }
        }
        """;
        var instance = ServiceBusTopicsConfig(
            services: null,
            topic: topic,
            fallback: fallback);

        Assert.False(Evaluate(instance).IsValid);
        var document = JsonSerializer.Deserialize(
            instance.ToJsonString(),
            ConfigDocumentJsonContext.Default.ConfigDocument)!;
        Assert.Throws<ProxyConfigException>(
            () => ProxyConfigValidator.Validate(
                ConfigDocumentTranslator.ToProxyConfig(document)));
    }

    [Fact]
    public void Sns_event_grid_default_requires_fallback_for_service_bus_topics_binding()
    {
        var invalid = ServiceBusTopicsConfig(
            services: """
            "services": {
              "sns": { "enabled": true, "defaultBackend": "EventGrid" }
            },
            """,
            topic: null,
            fallback: null);

        Assert.False(Evaluate(invalid).IsValid);
    }

    private static void AssertValid(JsonNode instance)
    {
        var result = Evaluate(instance);
        Assert.True(result.IsValid, JsonSerializer.Serialize(result));
    }

    private static EvaluationResults Evaluate(JsonNode instance) =>
        Schema.Evaluate(ToElement(instance), EvaluationOptions);

    // JsonSchema.Net 9.x evaluates against JsonElement rather than JsonNode.
    private static JsonElement ToElement(JsonNode instance) =>
        JsonSerializer.SerializeToElement(instance);

    private static JsonTypeInfo[] ConfigContractTypes() =>
    [
        ConfigDocumentJsonContext.Default.ConfigDocument,
        ConfigDocumentJsonContext.Default.ServicesConfig,
        ConfigDocumentJsonContext.Default.ServiceToggleConfig,
        ConfigDocumentJsonContext.Default.S3ServiceConfig,
        ConfigDocumentJsonContext.Default.SnsServiceConfig,
        ConfigDocumentJsonContext.Default.DynamoDbServiceConfig,
        ConfigDocumentJsonContext.Default.BindingEntry,
        ConfigDocumentJsonContext.Default.AwsIdentityConfig,
        ConfigDocumentJsonContext.Default.AzureBindingSet,
        ConfigDocumentJsonContext.Default.AzureBackendConfig,
        ConfigDocumentJsonContext.Default.AzureTargetConfig,
        ConfigDocumentJsonContext.Default.AzureAuthConfig,
        ConfigDocumentJsonContext.Default.AzureIdentity,
        ConfigDocumentJsonContext.Default.SqsQueueSettings,
        ConfigDocumentJsonContext.Default.SnsTopicSettings,
        ConfigDocumentJsonContext.Default.KinesisStreamSettings,
    ];

    private static void ValidateRuntime(JsonNode instance)
    {
        var document = JsonSerializer.Deserialize(
            instance.ToJsonString(),
            ConfigDocumentJsonContext.Default.ConfigDocument)!;
        ProxyConfigValidator.Validate(ConfigDocumentTranslator.ToProxyConfig(document));
    }

    private static void CollectStringProperties(JsonNode node, string propertyName, HashSet<string> values)
    {
        if (node is JsonObject obj)
        {
            foreach (var (name, value) in obj)
            {
                if (name.Equals(propertyName, StringComparison.Ordinal)
                    && value is JsonValue scalar
                    && scalar.TryGetValue<string>(out var text))
                {
                    values.Add(text);
                }

                if (value is not null)
                {
                    CollectStringProperties(value, propertyName, values);
                }
            }

        }
        else if (node is JsonArray array)
        {
            foreach (var value in array)
            {
                if (value is not null)
                {
                    CollectStringProperties(value, propertyName, values);
                }
            }
        }
    }

    private static void CollectPropertyNameSchemas(
        JsonNode node,
        List<JsonObject> propertyNameSchemas)
    {
        if (node is JsonObject obj)
        {
            foreach (var (name, value) in obj)
            {
                if (name == "propertyNames" && value is JsonObject propertyNames)
                {
                    propertyNameSchemas.Add(propertyNames);
                }
                else if (value is not null)
                {
                    CollectPropertyNameSchemas(value, propertyNameSchemas);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var value in array)
            {
                if (value is not null)
                {
                    CollectPropertyNameSchemas(value, propertyNameSchemas);
                }
            }
        }
    }

    private static JsonNode MinimalConfig() => JsonNode.Parse("""
    {
      "bindings": [
        {
          "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
          "azure": {}
        }
      ]
    }
    """)!;

    private static JsonNode KinesisConfig(string signingKey) => JsonNode.Parse($$"""
    {
      "bindings": [
        {
          "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
          "azure": {
            "kinesis": {
              "kind": "eventHubs",
              "target": { "namespace": "events" },
              "auth": { "mode": "sas", "keyName": "Root", "key": "key" },
              "shardIteratorSigningKey": "{{signingKey}}"
            }
          }
        }
      ]
    }
    """)!;

    private static JsonNode ServiceBusTopicsConfig(
        string? services,
        string? topic,
        string? fallback)
    {
        var topicsJson = topic is null
            ? string.Empty
            : $$"""
              ,"topics": { "orders": {{topic}} }
              """;
        var fallbackJson = fallback is null
            ? string.Empty
            : $$"""
              ,"eventGridFallback": {{fallback}}
              """;
        return JsonNode.Parse($$"""
        {
          {{services}}
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "sns": {
                  "kind": "serviceBusTopics",
                  "target": { "namespace": "topics" },
                  "auth": { "mode": "sas", "keyName": "Root", "key": "key" }
                  {{topicsJson}}
                  {{fallbackJson}}
                }
              }
            }
          ]
        }
        """)!;
    }

    private static JsonNode ReadHelmConfigContent(string path)
    {
        var yaml = LoadYaml(path);
        var root = Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
        var config = Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("config")]);
        return ConvertYaml(config.Children[new YamlScalarNode("content")]);
    }

    private static JsonNode ReadKubernetesConfigJson(string path)
    {
        var yaml = LoadYaml(path);
        foreach (var document in yaml.Documents)
        {
            if (document.RootNode is not YamlMappingNode root
                || !root.Children.TryGetValue(new YamlScalarNode("stringData"), out var stringDataNode)
                || stringDataNode is not YamlMappingNode stringData
                || !stringData.Children.TryGetValue(new YamlScalarNode("config.json"), out var configNode)
                || configNode is not YamlScalarNode configText)
            {
                continue;
            }

            return JsonNode.Parse(configText.Value!)!;
        }

        throw new InvalidDataException($"{path} does not contain stringData.config.json.");
    }

    private static YamlStream LoadYaml(string path)
    {
        var stream = new YamlStream();
        using var reader = File.OpenText(path);
        stream.Load(reader);
        return stream;
    }

    private static JsonNode ConvertYaml(YamlNode node) => node switch
    {
        YamlMappingNode mapping => ConvertMapping(mapping),
        YamlSequenceNode sequence => ConvertSequence(sequence),
        YamlScalarNode scalar => ConvertScalar(scalar),
        _ => throw new InvalidDataException($"Unsupported YAML node type {node.GetType().Name}."),
    };

    private static JsonObject ConvertMapping(YamlMappingNode mapping)
    {
        var result = new JsonObject();
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            var key = Assert.IsType<YamlScalarNode>(keyNode).Value
                ?? throw new InvalidDataException("YAML mapping key cannot be null.");
            result.Add(key, ConvertYaml(valueNode));
        }
        return result;
    }

    private static JsonArray ConvertSequence(YamlSequenceNode sequence)
    {
        var result = new JsonArray();
        foreach (var child in sequence.Children)
        {
            result.Add(ConvertYaml(child));
        }
        return result;
    }

    private static JsonNode ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (value is null || value.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create((string?)null)!;
        }
        if (bool.TryParse(value, out var boolean))
        {
            return JsonValue.Create(boolean);
        }
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return JsonValue.Create(integer);
        }
        return JsonValue.Create(value);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))
                && File.Exists(Path.Combine(directory.FullName, "aws2azure.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not find the aws2azure repository root.");
    }
}
