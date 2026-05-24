using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Amazon.Kinesis;
using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Xunit;

namespace Aws2Azure.IntegrationTests.Fixtures;

public sealed class KinesisEmulatorProxyFixture : IAsyncLifetime
{
    public const string Namespace = "emulatorNs1";
    public const string EventHubName = "hub1";
    public const string StreamName = "orders";
    public const string SasKeyName = "RootManageSharedAccessKey";
    public const string SasKey = "SAS_KEY_VALUE";
    public const string AwsAccessKey = "AKIA-KINESIS-IT";
    public const string AwsSecret = "kinesis-it-secret";

    private const string EmulatorImageTag = "aws2azure-it/eh-emulator:1";
    private const string EmulatorHostAlias = "eventhubs-emulator";
    private const string AzuriteHostAlias = "azurite";
    private const ushort EmulatorAmqpPort = 5672;
    private const ushort AzuriteBlobPort = 10000;
    private const string ProxyHostName = "kinesis.127.0.0.1.nip.io";

    private readonly StringBuilder _proxyOutput = new();
    private INetwork? _network;
    private IContainer? _azurite;
    private IContainer? _emulator;
    private IFutureDockerImage? _emulatorImage;
    private Process? _proxyProcess;
    private string? _configFile;

    public bool DockerAvailable { get; private set; }
    public string? SkipReason { get; private set; }
    public string ProxyServiceUrl { get; private set; } = string.Empty;
    public string AmqpHost { get; private set; } = string.Empty;
    public int AmqpPort { get; private set; }
    public string EmulatorLogs => _proxyOutput.ToString();

    /// <summary>
    /// Azure SDK connection string for the in-process Event Hubs emulator.
    /// Exposed for baseline perf comparisons (e.g. Azure.Messaging.EventHubs
    /// vs the proxy path) — NOT for production use.
    /// </summary>
    public string EventHubsConnectionString
        => string.IsNullOrEmpty(AmqpHost)
            ? string.Empty
            : $"Endpoint=sb://{AmqpHost}:{AmqpPort};SharedAccessKeyName={SasKeyName};SharedAccessKey={SasKey};UseDevelopmentEmulator=true";

    public AmazonKinesisClient CreateClient(string? accessKey = null, string? secret = null, HttpMessageHandler? httpCounter = null)
    {
        var config = new AmazonKinesisConfig
        {
            ServiceURL = ProxyServiceUrl,
            UseHttp = true,
            AuthenticationRegion = "us-east-1",
        };
        if (httpCounter is not null)
        {
            config.HttpClientFactory = new DiagnosticHandlerHttpClientFactory(httpCounter);
        }
        return new AmazonKinesisClient(
            accessKey ?? AwsAccessKey,
            secret ?? AwsSecret,
            config);
    }

    private sealed class DiagnosticHandlerHttpClientFactory : Amazon.Runtime.HttpClientFactory
    {
        private readonly HttpMessageHandler _outerHandler;
        public DiagnosticHandlerHttpClientFactory(HttpMessageHandler outerHandler) => _outerHandler = outerHandler;
        public override HttpClient CreateHttpClient(Amazon.Runtime.IClientConfig clientConfig)
        {
            if (_outerHandler is DelegatingHandler dh && dh.InnerHandler is null)
            {
                dh.InnerHandler = new HttpClientHandler();
            }
            return new HttpClient(_outerHandler, disposeHandler: false);
        }
        public override bool UseSDKHttpClientCaching(Amazon.Runtime.IClientConfig clientConfig) => true;
        public override bool DisposeHttpClientsAfterUse(Amazon.Runtime.IClientConfig clientConfig) => false;
    }

    public async Task InitializeAsync()
    {
        try
        {
            await EnsureDockerAvailableAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDockerUnavailable(ex))
        {
            SkipReason = ex.Message;
            await DisposeAsync().ConfigureAwait(false);
            DockerAvailable = false;
            return;
        }

        try
        {
            var emulatorDir = Path.Combine(AppContext.BaseDirectory, "Kinesis", "Emulator");
            if (!File.Exists(Path.Combine(emulatorDir, "Dockerfile"))
                || !File.Exists(Path.Combine(emulatorDir, "Config.json")))
            {
                throw new FileNotFoundException($"Kinesis emulator assets not found under '{emulatorDir}'.");
            }

            _emulatorImage = new ImageFromDockerfileBuilder()
                .WithName(EmulatorImageTag)
                .WithDockerfileDirectory(emulatorDir)
                .WithDockerfile("Dockerfile")
                .WithCleanUp(false)
                .Build();
            await _emulatorImage.CreateAsync().ConfigureAwait(false);

            _network = new NetworkBuilder()
                .WithName("aws2azure-it-ehnet-" + Guid.NewGuid().ToString("N")[..8])
                .Build();
            await _network.CreateAsync().ConfigureAwait(false);

            _azurite = new ContainerBuilder()
                .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
                .WithName("aws2azure-it-azurite-" + Guid.NewGuid().ToString("N")[..8])
                .WithNetwork(_network)
                .WithNetworkAliases(AzuriteHostAlias)
                .WithPortBinding(AzuriteBlobPort, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(AzuriteBlobPort))
                .Build();
            await _azurite.StartAsync().ConfigureAwait(false);

            _emulator = new ContainerBuilder()
                .WithImage(_emulatorImage)
                .WithName("aws2azure-it-ehemu-" + Guid.NewGuid().ToString("N")[..8])
                .WithNetwork(_network)
                .WithNetworkAliases(EmulatorHostAlias)
                .WithEnvironment("BLOB_SERVER", AzuriteHostAlias)
                .WithEnvironment("METADATA_SERVER", AzuriteHostAlias)
                .WithEnvironment("ACCEPT_EULA", "Y")
                .WithPortBinding(EmulatorAmqpPort, true)
                .Build();
            await _emulator.StartAsync().ConfigureAwait(false);

            var amqpHost = _emulator.Hostname;
            var amqpPort = _emulator.GetMappedPublicPort(EmulatorAmqpPort);
            AmqpHost = amqpHost;
            AmqpPort = amqpPort;
            await WaitForPortAsync(amqpHost, amqpPort, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            var proxyPort = GetFreePort();
            ProxyServiceUrl = $"http://{ProxyHostName}:{proxyPort}";
            _configFile = Path.Combine(AppContext.BaseDirectory, "kinesis-it-config-" + Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllTextAsync(_configFile, $$"""
                {
                  "services": {
                    "s3": { "enabled": false },
                    "sqs": { "enabled": false },
                    "dynamodb": { "enabled": false },
                    "kinesis": { "enabled": true }
                  },
                  "credentials": [
                    {
                      "awsAccessKeyId": "{{AwsAccessKey}}",
                      "awsSecretAccessKey": "{{AwsSecret}}",
                      "azure": {
                        "eventHubs": {
                          "namespace": "{{Namespace}}",
                          "endpoint": "http://{{amqpHost}}:{{amqpPort}}/",
                          "sasKeyName": "{{SasKeyName}}",
                          "sasKey": "{{SasKey}}",
                          "shardIteratorSigningKey": "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
                          "streams": {
                            "{{StreamName}}": {
                              "eventHubName": "{{EventHubName}}",
                              "consumerGroup": "$Default",
                              "partitionCount": 4
                            }
                          }
                        }
                      }
                    }
                  ]
                }
                """).ConfigureAwait(false);

            _proxyProcess = StartProxyProcess(proxyPort, _configFile);
            await WaitForProxyAsync(proxyPort, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            await WaitForKinesisAsync(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            DockerAvailable = true;
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_proxyProcess is not null)
        {
            try
            {
                if (!_proxyProcess.HasExited)
                {
                    _proxyProcess.Kill(entireProcessTree: true);
                    await _proxyProcess.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch
            {
            }

            _proxyProcess.Dispose();
        }

        if (_configFile is not null)
        {
            try { File.Delete(_configFile); } catch { }
        }

        if (_emulator is not null)
        {
            await _emulator.DisposeAsync().ConfigureAwait(false);
        }

        if (_azurite is not null)
        {
            await _azurite.DisposeAsync().ConfigureAwait(false);
        }

        if (_network is not null)
        {
            await _network.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task EnsureDockerAvailableAsync()
    {
        using var client = new DockerClientConfiguration().CreateClient();
        await client.System.PingAsync().ConfigureAwait(false);
    }

    private static bool IsDockerUnavailable(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var typeName = current.GetType().FullName ?? string.Empty;
            if (typeName.StartsWith("Docker", StringComparison.Ordinal)) return true;

            var message = current.Message ?? string.Empty;
            if (message.Contains("docker daemon", StringComparison.OrdinalIgnoreCase)) return true;
            if (message.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)) return true;
            if (message.Contains("no such host", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static async Task WaitForPortAsync(string host, int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port).ConfigureAwait(false);
                return;
            }
            catch
            {
                await Task.Delay(500).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"Timed out waiting for TCP {host}:{port}.");
    }

    private async Task WaitForKinesisAsync(TimeSpan timeout)
    {
        using var client = CreateClient();
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastException = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await client.DescribeStreamAsync(new Amazon.Kinesis.Model.DescribeStreamRequest
                {
                    StreamName = StreamName,
                }).ConfigureAwait(false);
                await client.PutRecordAsync(new Amazon.Kinesis.Model.PutRecordRequest
                {
                    StreamName = StreamName,
                    PartitionKey = "fixture-warmup",
                    Data = new MemoryStream(Encoding.UTF8.GetBytes("warmup")),
                }).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }

        var emulatorLogs = string.Empty;
        if (_emulator is not null)
        {
            var logs = await _emulator.GetLogsAsync().ConfigureAwait(false);
            emulatorLogs = string.Join(Environment.NewLine, logs.Stdout, logs.Stderr);
        }
        throw new TimeoutException(
            "Timed out waiting for Kinesis emulator readiness. Last error: "
            + lastException?.Message
            + Environment.NewLine
            + _proxyOutput
            + Environment.NewLine
            + emulatorLogs);
    }

    private static async Task WaitForProxyAsync(int port, TimeSpan timeout)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync("/_aws2azure/health").ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for proxy on port {port}.");
    }

    private Process StartProxyProcess(int port, string configFile)
    {
        var repoRoot = FindRepoRoot();
        var startInfo = new ProcessStartInfo("dotnet", "run -c Release --project src/Aws2Azure.Proxy/Aws2Azure.Proxy.csproj --no-build --no-restore --no-launch-profile")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["AWS2AZURE_CONFIG_FILE"] = configFile;
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Testing";

        // Propagate optional diagnostics opt-ins so perf experiments can turn
        // on AMQP timing breadcrumbs in the proxy process from a test host.
        var amqpTiming = Environment.GetEnvironmentVariable("AWS2AZURE_AMQP_TIMING");
        if (!string.IsNullOrEmpty(amqpTiming))
        {
            startInfo.Environment["AWS2AZURE_AMQP_TIMING"] = amqpTiming;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => AppendOutput(args.Data);
        process.ErrorDataReceived += (_, args) => AppendOutput(args.Data);
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start aws2azure proxy process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private void AppendOutput(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        lock (_proxyOutput)
        {
            _proxyOutput.AppendLine(line);
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "aws2azure.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root for Kinesis integration fixture.");
    }
}

[CollectionDefinition(Name)]
public sealed class KinesisEmulatorProxyCollection : ICollectionFixture<KinesisEmulatorProxyFixture>
{
    public const string Name = "kinesis-emulator-proxy";
}
