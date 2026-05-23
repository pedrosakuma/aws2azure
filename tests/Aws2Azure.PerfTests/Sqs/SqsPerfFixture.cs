using Amazon.SQS;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.PerfTests;

public sealed class SqsPerfFixture : IAsyncLifetime
{
    public const string AwsAccessKey = "AKIA-PERF-SQS";
    public const string AwsSecret = "perf-sqs-secret";

    private readonly ServiceBusEmulatorFixture _emulator = new();
    private readonly PerfProxyProcess _proxy = new();

    public bool Ready { get; private set; }
    public string? SkipReason { get; private set; }
    public string ServiceUrl => _proxy.ServiceUrlForHost("sqs");
    public string QueueName => ServiceBusEmulatorFixture.StandardQueue;
    public string QueueUrl => $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}";

    public string ProxyOutput => _proxy.Output;

    public AmazonSQSClient CreateClient() => new(
        AwsAccessKey,
        AwsSecret,
        new AmazonSQSConfig
        {
            ServiceURL = ServiceUrl,
            AuthenticationRegion = "us-east-1",
            UseHttp = true,
        });

    public async Task InitializeAsync()
    {
        if (!PerfGate.Enabled)
        {
            SkipReason = "AWS2AZURE_PERF=1 not set.";
            return;
        }

        try
        {
            await _emulator.InitializeAsync().ConfigureAwait(false);
            if (!_emulator.DockerAvailable)
            {
                SkipReason = "Docker not available for Service Bus emulator.";
                return;
            }

            var amqpUrl = $"http://{_emulator.AmqpHost}:{_emulator.AmqpPort}/";
            var config = $$"""
                {
                  "listen": "http://127.0.0.1:0",
                  "services": {
                    "s3":  { "enabled": false },
                    "sqs": { "azureService": "servicebus", "namespace": "{{ServiceBusEmulatorFixture.Namespace}}" }
                  },
                  "credentials": [
                    {
                      "awsAccessKeyId": "{{AwsAccessKey}}",
                      "awsSecretAccessKey": "{{AwsSecret}}",
                      "azure": {
                        "serviceBus": {
                          "namespace": "{{amqpUrl}}",
                          "sasKeyName": "{{ServiceBusEmulatorFixture.SasKeyName}}",
                          "sasKey": "{{ServiceBusEmulatorFixture.WellKnownSasKey}}",
                          "transport": "Amqp"
                        }
                      }
                    }
                  ]
                }
                """;
            await _proxy.StartAsync(config, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            Ready = true;
        }
        catch (Exception ex)
        {
            SkipReason = $"Fixture init failed: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync().ConfigureAwait(false);
        await _emulator.DisposeAsync().ConfigureAwait(false);
    }
}

[CollectionDefinition(Name)]
public sealed class SqsPerfCollection : ICollectionFixture<SqsPerfFixture>
{
    public const string Name = "sqs-perf";
}
