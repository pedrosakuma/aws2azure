using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Aws2Azure.Core;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Core.SigV4;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Modules;

public class StubServiceModuleTests
{
    [Fact]
    public void Matches_host_by_configured_prefix()
    {
        var s3 = new StubServiceModule("s3", AwsErrorFormat.Xml, ["ListBuckets"], "s3.");
        Assert.True(s3.MatchesHost("s3.example.com"));
        Assert.True(s3.MatchesHost("S3.PROXY.LOCALTEST.ME"));
        Assert.False(s3.MatchesHost("sqs.example.com"));
        Assert.False(s3.MatchesHost(string.Empty));
    }

    [Fact]
    public void Capabilities_default_to_stub_status()
    {
        var sqs = new StubServiceModule("sqs", AwsErrorFormat.Json,
            ["SendMessage", "ReceiveMessage"], "sqs.");

        Assert.Equal("sqs", sqs.Capabilities.ServiceName);
        Assert.All(sqs.Capabilities.Operations,
            op => Assert.Equal(OperationStatus.Stub, op.Status));
        Assert.Contains(sqs.Capabilities.Operations, op => op.Name == "SendMessage");
    }

    [Fact]
    public void Stubs_opt_out_of_sigv4()
    {
        var stub = new StubServiceModule("x", AwsErrorFormat.Json, [], "x.");
        Assert.False(stub.RequiresSigV4);
    }
}

public class AwsErrorResponseTests
{
    [Fact]
    public void Builds_well_formed_xml_for_s3()
    {
        var xml = AwsErrorResponse.BuildXml("NoSuchBucket", "The specified bucket does not exist", "my-bucket", "req-1");
        var doc = XDocument.Parse(xml);
        Assert.Equal("Error", doc.Root!.Name.LocalName);
        Assert.Equal("NoSuchBucket", doc.Root.Element("Code")!.Value);
        Assert.Equal("The specified bucket does not exist", doc.Root.Element("Message")!.Value);
        Assert.Equal("my-bucket", doc.Root.Element("Resource")!.Value);
        Assert.Equal("req-1", doc.Root.Element("RequestId")!.Value);
    }

    [Fact]
    public void Builds_json_for_non_s3()
    {
        var json = AwsErrorResponse.BuildJson("QueueDoesNotExist", "no such queue");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("QueueDoesNotExist", doc.RootElement.GetProperty("__type").GetString());
        Assert.Equal("no such queue", doc.RootElement.GetProperty("message").GetString());
    }
}

public class ServiceModuleRegistryTests
{
    private static ICredentialResolver EmptyResolver() => new StaticCredentialResolver(new ProxyConfig
    {
        Credentials =
        {
            new CredentialEntry
            {
                AwsAccessKeyId = "AKIA",
                AwsSecretAccessKey = "secret",
                Azure = new AzureCredentials(),
            },
        },
    });

    private sealed class FakeModule : IServiceModule
    {
        public string ServiceName => "fake";
        public bool MatchesHost(string host) => host.StartsWith("fake.", StringComparison.OrdinalIgnoreCase);
        public CapabilityMatrix Capabilities { get; } =
            new("fake", [new OperationCapability("DoThing", OperationStatus.Implemented)]);
        public bool RequiresSigV4 { get; init; }
        public AwsErrorFormat ErrorFormat { get; init; } = AwsErrorFormat.Json;
        public bool Invoked { get; private set; }
        public ValueTask HandleAsync(HttpContext context)
        {
            Invoked = true;
            context.Response.StatusCode = StatusCodes.Status200OK;
            return ValueTask.CompletedTask;
        }
    }

    private static DefaultHttpContext NewContext(string host, string path = "/")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString(host);
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Fact]
    public async Task Dispatches_to_first_matching_module()
    {
        var fake = new FakeModule();
        var registry = new ServiceModuleRegistry([fake]);

        await registry.DispatchAsync(NewContext("fake.example.com"));

        Assert.True(fake.Invoked);
    }

    [Fact]
    public async Task Returns_404_when_no_module_matches()
    {
        var registry = new ServiceModuleRegistry([new FakeModule()]);
        var ctx = NewContext("nothing.example.com");

        await registry.DispatchAsync(ctx);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Rejects_request_with_invalid_sigv4_using_module_error_format()
    {
        var fake = new FakeModule { RequiresSigV4 = true, ErrorFormat = AwsErrorFormat.Xml };
        var validator = new SigV4Validator(EmptyResolver());
        var registry = new ServiceModuleRegistry([fake], validator);

        var ctx = NewContext("fake.example.com");
        await registry.DispatchAsync(ctx);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.False(fake.Invoked);

        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var doc = XDocument.Parse(body);
        Assert.Equal("InvalidRequest", doc.Root!.Element("Code")!.Value);
    }

    [Fact]
    public async Task Skips_sigv4_when_module_opts_out()
    {
        var fake = new FakeModule { RequiresSigV4 = false };
        var registry = new ServiceModuleRegistry([fake], sigV4Validator: null);

        var ctx = NewContext("fake.example.com");
        await registry.DispatchAsync(ctx);

        Assert.True(fake.Invoked);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Returns_internal_error_when_sigv4_required_but_unconfigured()
    {
        var fake = new FakeModule { RequiresSigV4 = true, ErrorFormat = AwsErrorFormat.Json };
        var registry = new ServiceModuleRegistry([fake], sigV4Validator: null);

        var ctx = NewContext("fake.example.com");
        await registry.DispatchAsync(ctx);

        Assert.Equal(StatusCodes.Status500InternalServerError, ctx.Response.StatusCode);
    }
}
