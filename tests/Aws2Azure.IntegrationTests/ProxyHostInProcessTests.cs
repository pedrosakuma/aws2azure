using System.Net;
using System.Threading.Tasks;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests;

[Collection(ProxyCollection.Name)]
public class ProxyHostInProcessTests
{
    private readonly ProxyHostFixture _fx;
    public ProxyHostInProcessTests(ProxyHostFixture fx) => _fx = fx;

    [Fact]
    public async Task Health_Returns200()
    {
        var client = _fx.CreateClient();
        var response = await client.GetAsync("/_aws2azure/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", body);
    }

    [Fact]
    public async Task Capabilities_ReturnsFiveServicesFromYaml()
    {
        var client = _fx.CreateClient();
        var response = await client.GetAsync("/_aws2azure/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // Source of truth: docs/gaps/*.yaml → CapabilityRegistry.g.cs
        foreach (var name in new[] { "\"s3\"", "\"sqs\"", "\"dynamodb\"", "\"kinesis\"", "\"sns\"" })
        {
            Assert.Contains(name, body);
        }
    }

    [Fact]
    public async Task UnknownHost_Returns404()
    {
        var client = _fx.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/anything");
        request.Headers.Host = "unknown.proxy.localtest.me";
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StubModule_Returns501()
    {
        var client = _fx.CreateClient();
        // DynamoDB/Kinesis/SNS remain stubbed; S3 and SQS are real modules
        // as of Phase-1 slice 1 and Phase-2 slice 0 respectively.
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "dynamodb.proxy.localtest.me";
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }
}
