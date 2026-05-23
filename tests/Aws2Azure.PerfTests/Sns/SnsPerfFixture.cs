using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.PerfTests;

public sealed class SnsPerfFixture : IAsyncLifetime
{
    public const string AwsAccessKey = "AKIA-PERF-SNS";
    public const string AwsSecret = "perf-sns-secret";

    private readonly ServiceBusEmulatorFixture _emulator = new();
    private readonly PerfProxyProcess _proxy = new();

    public bool Ready { get; private set; }
    public string? SkipReason { get; private set; }
    public string ServiceUrl => _proxy.ServiceUrlForHost("sns");
    public string TopicArn { get; private set; } = string.Empty;

    public AmazonSimpleNotificationServiceClient CreateClient() => new(
        AwsAccessKey,
        AwsSecret,
        new AmazonSimpleNotificationServiceConfig
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
            var managementUrl = $"http://{_emulator.AmqpHost}:{_emulator.HttpPort}/";
            var config = $$"""
                {
                  "listen": "http://127.0.0.1:0",
                  "services": {
                    "s3":  { "enabled": false },
                    "sqs": { "enabled": false },
                    "sns": { "enabled": true }
                  },
                  "credentials": [
                    {
                      "awsAccessKeyId": "{{AwsAccessKey}}",
                      "awsSecretAccessKey": "{{AwsSecret}}",
                      "azure": {
                        "serviceBusTopics": {
                          "namespace": "{{ServiceBusEmulatorFixture.Namespace}}",
                          "endpoint": "{{amqpUrl}}",
                          "managementEndpoint": "{{managementUrl}}",
                          "sasKeyName": "{{ServiceBusEmulatorFixture.SasKeyName}}",
                          "sasKey": "{{ServiceBusEmulatorFixture.WellKnownSasKey}}"
                        }
                      }
                    }
                  ]
                }
                """;
            await _proxy.StartAsync(config, TimeSpan.FromMinutes(2)).ConfigureAwait(false);

            using var sns = CreateClient();
            var topicName = "perftopic" + Guid.NewGuid().ToString("N")[..8];
            var create = await sns.CreateTopicAsync(topicName).ConfigureAwait(false);
            TopicArn = create.TopicArn;
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
public sealed class SnsPerfCollection : ICollectionFixture<SnsPerfFixture>
{
    public const string Name = "sns-perf";
}
