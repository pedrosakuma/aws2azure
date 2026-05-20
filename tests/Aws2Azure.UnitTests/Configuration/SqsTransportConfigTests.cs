using Aws2Azure.Core.Configuration;

namespace Aws2Azure.UnitTests.Configuration;

public class SqsTransportConfigTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"aws2azure-test-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    [Fact]
    public void Defaults_to_rest_when_unset()
    {
        var creds = new ServiceBusCredentials { Namespace = "n", SasKeyName = "k", SasKey = "s" };
        Assert.Equal(SqsTransport.Rest, SqsTransportResolver.Resolve(creds, "queue-a"));
    }

    [Fact]
    public void Namespace_default_applies_when_no_per_queue_override()
    {
        var creds = new ServiceBusCredentials
        {
            Namespace = "n", SasKeyName = "k", SasKey = "s",
            Transport = SqsTransport.Amqp,
        };
        Assert.Equal(SqsTransport.Amqp, SqsTransportResolver.Resolve(creds, "queue-a"));
    }

    [Fact]
    public void Per_queue_override_wins_over_namespace_default()
    {
        var creds = new ServiceBusCredentials
        {
            Namespace = "n", SasKeyName = "k", SasKey = "s",
            Transport = SqsTransport.Amqp,
            Queues = new Dictionary<string, SqsQueueSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["legacy"] = new SqsQueueSettings { Transport = SqsTransport.Rest },
            },
        };
        Assert.Equal(SqsTransport.Rest, SqsTransportResolver.Resolve(creds, "legacy"));
        Assert.Equal(SqsTransport.Amqp, SqsTransportResolver.Resolve(creds, "other"));
    }

    [Fact]
    public void Per_queue_lookup_is_case_insensitive()
    {
        var creds = new ServiceBusCredentials
        {
            Namespace = "n", SasKeyName = "k", SasKey = "s",
            Queues = new Dictionary<string, SqsQueueSettings>
            {
                ["MyQueue"] = new SqsQueueSettings { Transport = SqsTransport.Amqp },
            },
        };
        Assert.Equal(SqsTransport.Amqp, SqsTransportResolver.Resolve(creds, "myqueue"));
    }

    [Fact]
    public void Per_queue_entry_with_null_transport_falls_back_to_namespace_default()
    {
        var creds = new ServiceBusCredentials
        {
            Namespace = "n", SasKeyName = "k", SasKey = "s",
            Transport = SqsTransport.Amqp,
            Queues = new Dictionary<string, SqsQueueSettings>
            {
                ["q"] = new SqsQueueSettings { Transport = null },
            },
        };
        Assert.Equal(SqsTransport.Amqp, SqsTransportResolver.Resolve(creds, "q"));
    }

    [Fact]
    public void Json_round_trip_preserves_transport_and_queues()
    {
        File.WriteAllText(_tempFile, """
        {
          "credentials": [ {
            "awsAccessKeyId": "AKIA",
            "awsSecretAccessKey": "s",
            "azure": {
              "serviceBus": {
                "namespace": "ns",
                "sasKeyName": "kn",
                "sasKey": "kv",
                "transport": "amqp",
                "queues": {
                  "legacy": { "transport": "rest" },
                  "modern": { "transport": "amqp" }
                }
              }
            }
          } ]
        }
        """);

        var config = ProxyConfigLoader.Load(_tempFile, envVars: new Dictionary<string, string?>());
        var sb = config.Credentials[0].Azure.ServiceBus!;

        Assert.Equal(SqsTransport.Amqp, sb.Transport);
        Assert.Equal(SqsTransport.Rest, sb.Queues!["legacy"].Transport);
        Assert.Equal(SqsTransport.Amqp, sb.Queues!["modern"].Transport);
        Assert.Equal(SqsTransport.Rest, SqsTransportResolver.Resolve(sb, "legacy"));
        Assert.Equal(SqsTransport.Amqp, SqsTransportResolver.Resolve(sb, "modern"));
        Assert.Equal(SqsTransport.Amqp, SqsTransportResolver.Resolve(sb, "anything-else"));
    }

    [Fact]
    public void Env_vars_set_namespace_transport_and_per_queue_override()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["AWS2AZURE__CREDENTIALS__0__AWSACCESSKEYID"] = "AKIA",
            ["AWS2AZURE__CREDENTIALS__0__AWSSECRETACCESSKEY"] = "s",
            ["AWS2AZURE__CREDENTIALS__0__AZURE__SERVICEBUS__NAMESPACE"] = "ns",
            ["AWS2AZURE__CREDENTIALS__0__AZURE__SERVICEBUS__SASKEYNAME"] = "kn",
            ["AWS2AZURE__CREDENTIALS__0__AZURE__SERVICEBUS__SASKEY"] = "kv",
            ["AWS2AZURE__CREDENTIALS__0__AZURE__SERVICEBUS__TRANSPORT"] = "amqp",
            ["AWS2AZURE__CREDENTIALS__0__AZURE__SERVICEBUS__QUEUES__myqueue__TRANSPORT"] = "rest",
        };

        var config = ProxyConfigLoader.Load(jsonFilePath: null, envVars: env);
        var sb = config.Credentials[0].Azure.ServiceBus!;

        Assert.Equal(SqsTransport.Amqp, sb.Transport);
        Assert.Equal(SqsTransport.Rest, sb.Queues!["myqueue"].Transport);
        Assert.Equal(SqsTransport.Rest, SqsTransportResolver.Resolve(sb, "myqueue"));
        Assert.Equal(SqsTransport.Amqp, SqsTransportResolver.Resolve(sb, "other"));
    }

    [Fact]
    public void Env_var_queue_name_with_double_underscore_is_preserved()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["AWS2AZURE__CREDENTIALS__0__AWSACCESSKEYID"] = "AKIA",
            ["AWS2AZURE__CREDENTIALS__0__AWSSECRETACCESSKEY"] = "s",
            ["AWS2AZURE__CREDENTIALS__0__AZURE__SERVICEBUS__NAMESPACE"] = "ns",
            ["AWS2AZURE__CREDENTIALS__0__AZURE__SERVICEBUS__SASKEYNAME"] = "kn",
            ["AWS2AZURE__CREDENTIALS__0__AZURE__SERVICEBUS__SASKEY"] = "kv",
            ["AWS2AZURE__CREDENTIALS__0__AZURE__SERVICEBUS__QUEUES__orders__dlq__TRANSPORT"] = "amqp",
        };

        var config = ProxyConfigLoader.Load(jsonFilePath: null, envVars: env);
        var sb = config.Credentials[0].Azure.ServiceBus!;

        Assert.True(sb.Queues!.ContainsKey("orders__dlq"));
        Assert.Equal(SqsTransport.Amqp, sb.Queues["orders__dlq"].Transport);
    }

    [Fact]
    public void Validator_rejects_null_queue_entry()
    {
        var config = new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = "AKIA",
                    AwsSecretAccessKey = "s",
                    Azure = new AzureCredentials
                    {
                        ServiceBus = new ServiceBusCredentials
                        {
                            Namespace = "n", SasKeyName = "k", SasKey = "v",
                            Queues = new Dictionary<string, SqsQueueSettings> { ["bad"] = null! },
                        },
                    },
                },
            },
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("queues.bad", ex.Message);
    }

    [Fact]
    public void Validator_rejects_undefined_transport_value()
    {
        var config = new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = "AKIA",
                    AwsSecretAccessKey = "s",
                    Azure = new AzureCredentials
                    {
                        ServiceBus = new ServiceBusCredentials
                        {
                            Namespace = "n", SasKeyName = "k", SasKey = "v",
                            Transport = (SqsTransport)99,
                        },
                    },
                },
            },
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("serviceBus.transport", ex.Message);
    }
}
