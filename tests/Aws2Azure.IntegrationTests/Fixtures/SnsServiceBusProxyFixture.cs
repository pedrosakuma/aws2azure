using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aws2Azure.IntegrationTests.Fixtures;

public sealed class SnsServiceBusProxyFixture : IAsyncLifetime
{
    public const string AwsAccessKey = "AKIAIOSFODNN7SNSIT";
    public const string AwsSecret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLESNS";

    private readonly ServiceBusEmulatorFixture _emulator = new();
    private WebApplicationFactoryHost? _host;
    private string? _configFile;
    private string? _previousConfigFile;

    public bool DockerAvailable => _emulator.DockerAvailable;
    public ServiceBusEmulatorFixture Emulator => _emulator;

    public HttpClient CreateSnsClient()
    {
        if (_host is null)
        {
            throw new InvalidOperationException(
                "SnsServiceBusProxyFixture: proxy host not initialised. " +
                "Check DockerAvailable before calling CreateSnsClient.");
        }

        return _host.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://sns.us-east-1.amazonaws.com"),
        });
    }

    public string CreateServiceBusConnectionString()
        => $"Endpoint=sb://{_emulator.AmqpHost}:{_emulator.AmqpPort};SharedAccessKeyName={ServiceBusEmulatorFixture.SasKeyName};SharedAccessKey={ServiceBusEmulatorFixture.WellKnownSasKey};UseDevelopmentEmulator=true;";

    public async Task InitializeAsync()
    {
        await _emulator.InitializeAsync().ConfigureAwait(false);
        if (!_emulator.DockerAvailable)
        {
            return;
        }

        _configFile = Path.Combine(AppContext.BaseDirectory,
            "sns-it-config-" + Guid.NewGuid().ToString("N") + ".json");

        var amqpUrl = $"http://{_emulator.AmqpHost}:{_emulator.AmqpPort}/";
        var managementUrl = $"http://{_emulator.AmqpHost}:{_emulator.HttpPort}/";
        await File.WriteAllTextAsync(_configFile, $$"""
            {
              "listen": "http://127.0.0.1:0",
              "services": {
                "s3": { "enabled": false },
                "sqs": { "enabled": false },
                "dynamodb": { "enabled": false },
                "kinesis": { "enabled": false },
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
            """).ConfigureAwait(false);

        _previousConfigFile = Environment.GetEnvironmentVariable("AWS2AZURE_CONFIG_FILE");
        Environment.SetEnvironmentVariable("AWS2AZURE_CONFIG_FILE", _configFile);
        _host = new WebApplicationFactoryHost();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
        }

        Environment.SetEnvironmentVariable("AWS2AZURE_CONFIG_FILE", _previousConfigFile);

        if (_configFile is not null)
        {
            try { File.Delete(_configFile); } catch { }
        }

        await _emulator.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class WebApplicationFactoryHost : WebApplicationFactory<Program>
    {
        public WebApplicationFactory<Program> Factory => this;

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            return base.CreateHost(builder);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SnsServiceBusProxyCollection : ICollectionFixture<SnsServiceBusProxyFixture>
{
    public const string Name = "sns-servicebus-proxy";
}
