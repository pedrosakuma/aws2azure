using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aws2Azure.IntegrationTests.Fixtures;

/// <summary>
/// Phase 2.7 Slice 5 — boots the proxy in-process pointed at the Service
/// Bus emulator fixture so SQS lifecycle integration tests
/// (Send / Receive / Delete / ChangeMessageVisibility) can talk to a
/// real broker.
///
/// <para>The fixture owns a <see cref="ServiceBusEmulatorFixture"/>
/// internally rather than composing two xUnit collection fixtures
/// (xUnit does not let a test class join two collections): the
/// emulator boots in <see cref="InitializeAsync"/>, then we write an
/// appsettings JSON that points the SQS module at the emulator's
/// mapped AMQP port via the new plain-AMQP URL convention
/// (<c>http://{host}:{port}/</c> on the credential's namespace
/// field) and with <c>"transport": "Amqp"</c>.</para>
///
/// <para>Skips host-side via <see cref="DockerAvailable"/> when Docker
/// isn't reachable so fork PRs and sandboxes that lack Docker don't
/// break the build, mirroring the
/// <see cref="ServiceBusEmulatorFixture"/> / <see cref="AzuriteFixture"/>
/// pattern.</para>
/// </summary>
public sealed class SqsEmulatorProxyFixture : IAsyncLifetime
{
    /// <summary>
    /// AWS access key the tests sign with; matched by the
    /// credentials block in the generated appsettings so the proxy's
    /// SigV4 validator accepts the request and the credential
    /// resolver hands the SB SAS pair to the AMQP pool.
    /// </summary>
    public const string AwsAccessKey = "AKIAIOSFODNN7EXAMPLE";

    /// <summary>Matching AWS secret the test signer uses to derive the SigV4 key.</summary>
    public const string AwsSecret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

    private readonly ServiceBusEmulatorFixture _emulator = new();
    private WebApplicationFactoryHost? _host;
    private string? _configFile;

    public bool DockerAvailable => _emulator.DockerAvailable;
    public ServiceBusEmulatorFixture Emulator => _emulator;

    /// <summary>
    /// HttpClient that targets the in-process proxy with a Host header
    /// of <c>sqs.us-east-1.amazonaws.com</c> so
    /// <see cref="Aws2Azure.Modules.Sqs.SqsServiceModule.MatchesHost"/>
    /// routes the request to the SQS module.
    /// </summary>
    public HttpClient CreateSqsClient()
    {
        if (_host is null)
        {
            throw new InvalidOperationException(
                "SqsEmulatorProxyFixture: proxy host not initialised. " +
                "Check DockerAvailable before calling CreateSqsClient.");
        }
        return _host.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://sqs.us-east-1.amazonaws.com"),
        });
    }

    public async Task InitializeAsync()
    {
        await _emulator.InitializeAsync().ConfigureAwait(false);
        if (!_emulator.DockerAvailable)
        {
            return;
        }

        _configFile = Path.Combine(Path.GetTempPath(),
            "aws2azure-sqs-it-" + Guid.NewGuid().ToString("N") + ".json");

        var amqpUrl = $"http://{_emulator.AmqpHost}:{_emulator.AmqpPort}/";
        File.WriteAllText(_configFile, $$"""
            {
              "listen": "http://127.0.0.1:0",
              "services": {
                "s3":  { "azureService": "blob",  "account": "devstoreaccount1" },
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
            """);
        Environment.SetEnvironmentVariable("AWS2AZURE_CONFIG_FILE", _configFile);

        _host = new WebApplicationFactoryHost();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
        }
        if (_configFile is not null)
        {
            try { File.Delete(_configFile); } catch { /* best-effort */ }
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

[CollectionDefinition(Name)]
public sealed class SqsEmulatorProxyCollection : ICollectionFixture<SqsEmulatorProxyFixture>
{
    public const string Name = "sqs-emulator-proxy";
}
