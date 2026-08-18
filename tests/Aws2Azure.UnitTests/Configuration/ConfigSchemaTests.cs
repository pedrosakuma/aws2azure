using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Aws2Azure.ConfigSchema;
using Aws2Azure.Core.Configuration;
using Json.Schema;
using YamlDotNet.RepresentationModel;

namespace Aws2Azure.UnitTests.Configuration;

public sealed class ConfigSchemaTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly JsonSchema Schema = JsonSchema.FromText(
        File.ReadAllText(Path.Combine(RepoRoot, ConfigSchemaGenerator.ArtifactRelativePath)));

    [Fact]
    public void Generated_schema_matches_committed_artifact()
    {
        var committed = File.ReadAllText(
            Path.Combine(RepoRoot, ConfigSchemaGenerator.ArtifactRelativePath));

        Assert.Equal(committed, ConfigSchemaGenerator.Generate());
    }

    [Fact]
    public void Schema_contains_every_source_generated_config_property()
    {
        var generated = ConfigSchemaGenerator.Generate();
        JsonTypeInfo[] contractTypes =
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

        foreach (var type in contractTypes)
        {
            foreach (var property in type.Properties)
            {
                var schemaName = property.Name switch
                {
                    "dynamoDb" => "dynamodb",
                    "secretsManager" => "secretsmanager",
                    _ => property.Name,
                };
                Assert.Contains($"\"{schemaName}\"", generated, StringComparison.Ordinal);
            }
        }
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
        AssertValid(JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "docker", "config.json")))!);
        AssertValid(ReadHelmConfigContent(
            Path.Combine(RepoRoot, "deploy", "helm", "aws2azure", "values.yaml")));
        AssertValid(ReadKubernetesConfigJson(
            Path.Combine(RepoRoot, "deploy", "sidecar", "secret.yaml")));
        AssertValid(ReadKubernetesConfigJson(
            Path.Combine(RepoRoot, "deploy", "sidecar", "demo-azurite.yaml")));
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

        Assert.False(Schema.Evaluate(instance).IsValid);
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

        Assert.False(Schema.Evaluate(invalid).IsValid);
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

        Assert.False(Schema.Evaluate(invalid).IsValid);
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

        Assert.False(Schema.Evaluate(invalidS3).IsValid);
        Assert.False(Schema.Evaluate(invalidCosmos).IsValid);
        Assert.False(Schema.Evaluate(invalidAuthority).IsValid);
    }

    [Fact]
    public void Shard_iterator_signing_key_requires_at_least_32_decoded_bytes()
    {
        var tooShort = KinesisConfig(Convert.ToBase64String(new byte[31]));
        var minimum = KinesisConfig(Convert.ToBase64String(new byte[32]));

        Assert.False(Schema.Evaluate(tooShort).IsValid);
        AssertValid(minimum);
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

        Assert.False(Schema.Evaluate(invalid).IsValid);
        AssertValid(valid);
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

        Assert.False(Schema.Evaluate(invalid).IsValid);
    }

    private static void AssertValid(JsonNode instance)
    {
        var result = Schema.Evaluate(instance);
        Assert.True(result.IsValid, JsonSerializer.Serialize(result));
    }

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
