using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aws2Azure.IntegrationTests.Fixtures;

/// <summary>
/// Boots the proxy in-process via WebApplicationFactory. The proxy reads its
/// config from a file pointed at by AWS2AZURE_CONFIG_FILE; the fixture writes
/// a temporary appsettings.json with the Azurite well-known dev credentials
/// so tests do not depend on the source-tree appsettings.json.
/// </summary>
public sealed class ProxyHostFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _configFile;

    public ProxyHostFixture()
    {
        _configFile = Path.Combine(Path.GetTempPath(), "aws2azure-it-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(_configFile, """
            {
              "listen": "http://127.0.0.1:0",
              "services": {
                "s3":  { "azureService": "blob",  "account": "devstoreaccount1" },
                "sqs": { "azureService": "servicebus", "namespace": "aws2azure" }
              },
              "credentials": [
                {
                  "awsAccessKeyId": "DEVSTOREACCOUNT1",
                  "awsSecretAccessKey": "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
                  "azureAccountKey": "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="
                }
              ]
            }
            """);
        Environment.SetEnvironmentVariable("AWS2AZURE_CONFIG_FILE", _configFile);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        return base.CreateHost(builder);
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        try { File.Delete(_configFile); } catch { /* best-effort */ }
    }
}

[CollectionDefinition(Name)]
public sealed class ProxyCollection : ICollectionFixture<ProxyHostFixture>
{
    public const string Name = "proxy";
}
