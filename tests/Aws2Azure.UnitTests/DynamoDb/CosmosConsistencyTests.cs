using System.Text;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.DynamoDb.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

public class CosmosConsistencyTests
{
    [Fact]
    public void ParseDefaultConsistency_reads_each_level()
    {
        Assert.Equal(CosmosConsistencyLevel.Strong, Parse("Strong"));
        Assert.Equal(CosmosConsistencyLevel.BoundedStaleness, Parse("BoundedStaleness"));
        Assert.Equal(CosmosConsistencyLevel.Session, Parse("Session"));
        Assert.Equal(CosmosConsistencyLevel.ConsistentPrefix, Parse("ConsistentPrefix"));
        Assert.Equal(CosmosConsistencyLevel.Eventual, Parse("Eventual"));

        static CosmosConsistencyLevel Parse(string name)
        {
            var json = "{\"id\":\"acct\",\"userConsistencyPolicy\":{\"defaultConsistencyLevel\":\"" + name + "\"}}";
            return CosmosConsistency.ParseDefaultConsistency(Encoding.UTF8.GetBytes(json));
        }
    }

    [Fact]
    public void ParseDefaultConsistency_is_case_insensitive()
    {
        var json = """{"userConsistencyPolicy":{"defaultConsistencyLevel":"session"}}""";
        Assert.Equal(CosmosConsistencyLevel.Session,
            CosmosConsistency.ParseDefaultConsistency(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("""{"userConsistencyPolicy":{"defaultConsistencyLevel":"Banana"}}""")]
    [InlineData("""{"userConsistencyPolicy":{}}""")]
    [InlineData("""{"userConsistencyPolicy":"Strong"}""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("not json")]
    [InlineData("")]
    public void ParseDefaultConsistency_returns_unknown_for_missing_or_garbage(string json)
    {
        Assert.Equal(CosmosConsistencyLevel.Unknown,
            CosmosConsistency.ParseDefaultConsistency(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void CanHonorConsistentRead_only_strong_and_bounded()
    {
        Assert.True(CosmosConsistency.CanHonorConsistentRead(CosmosConsistencyLevel.Strong));
        Assert.True(CosmosConsistency.CanHonorConsistentRead(CosmosConsistencyLevel.BoundedStaleness));
        Assert.False(CosmosConsistency.CanHonorConsistentRead(CosmosConsistencyLevel.Session));
        Assert.False(CosmosConsistency.CanHonorConsistentRead(CosmosConsistencyLevel.ConsistentPrefix));
        Assert.False(CosmosConsistency.CanHonorConsistentRead(CosmosConsistencyLevel.Eventual));
        Assert.False(CosmosConsistency.CanHonorConsistentRead(CosmosConsistencyLevel.Unknown));
    }

    [Fact]
    public void Decide_truth_table()
    {
        // Disabled is always Ok regardless of level.
        Assert.Equal(CosmosConsistency.ProbeOutcome.Ok,
            CosmosConsistency.Decide(CosmosConsistencyLevel.Eventual, ConsistencyCheckMode.Disabled));
        Assert.Equal(CosmosConsistency.ProbeOutcome.Ok,
            CosmosConsistency.Decide(CosmosConsistencyLevel.Strong, ConsistencyCheckMode.Disabled));
        // Honorable levels are always Ok.
        Assert.Equal(CosmosConsistency.ProbeOutcome.Ok,
            CosmosConsistency.Decide(CosmosConsistencyLevel.Strong, ConsistencyCheckMode.Warn));
        Assert.Equal(CosmosConsistency.ProbeOutcome.Ok,
            CosmosConsistency.Decide(CosmosConsistencyLevel.BoundedStaleness, ConsistencyCheckMode.Required));
        // Below-threshold warns under Warn, fails under Required.
        Assert.Equal(CosmosConsistency.ProbeOutcome.Warn,
            CosmosConsistency.Decide(CosmosConsistencyLevel.Session, ConsistencyCheckMode.Warn));
        Assert.Equal(CosmosConsistency.ProbeOutcome.Fail,
            CosmosConsistency.Decide(CosmosConsistencyLevel.Session, ConsistencyCheckMode.Required));
        Assert.Equal(CosmosConsistency.ProbeOutcome.Fail,
            CosmosConsistency.Decide(CosmosConsistencyLevel.Eventual, ConsistencyCheckMode.Required));
        // Unknown follows the same warn/fail path.
        Assert.Equal(CosmosConsistency.ProbeOutcome.Warn,
            CosmosConsistency.Decide(CosmosConsistencyLevel.Unknown, ConsistencyCheckMode.Warn));
        Assert.Equal(CosmosConsistency.ProbeOutcome.Fail,
            CosmosConsistency.Decide(CosmosConsistencyLevel.Unknown, ConsistencyCheckMode.Required));
    }
}

public class CosmosClientConsistencyProbeTests
{
    [Fact]
    public async Task ReadAccountConsistencyAsync_parses_account_body()
    {
        var handler = new StubHandler(System.Net.HttpStatusCode.OK,
            """{"id":"acct","userConsistencyPolicy":{"defaultConsistencyLevel":"Session"}}""");
        using var http = new Aws2Azure.Core.Azure.AzureHttpClient(handler, ownsHandler: false);
        var creds = new CosmosCredentials
        {
            Endpoint = "https://example.documents.azure.com:443/",
            PrimaryKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
            DatabaseName = "main",
        };
        var client = new CosmosClient(http, creds, new MasterKeyCosmosAuthenticator(creds.PrimaryKey));

        var level = await client.ReadAccountConsistencyAsync(System.Threading.CancellationToken.None);

        Assert.Equal(CosmosConsistencyLevel.Session, level);
        // Account read targets the account root, not a database path.
        Assert.Equal("https://example.documents.azure.com/", handler.Last!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ReadAccountConsistencyAsync_throws_on_non_success()
    {
        var handler = new StubHandler(System.Net.HttpStatusCode.Unauthorized, "{}");
        using var http = new Aws2Azure.Core.Azure.AzureHttpClient(handler, ownsHandler: false);
        var creds = new CosmosCredentials
        {
            Endpoint = "https://example.documents.azure.com",
            PrimaryKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
            DatabaseName = "main",
        };
        var client = new CosmosClient(http, creds, new MasterKeyCosmosAuthenticator(creds.PrimaryKey));

        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(
            () => client.ReadAccountConsistencyAsync(System.Threading.CancellationToken.None));
    }

    private sealed class StubHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly System.Net.HttpStatusCode _status;
        private readonly string _body;
        public System.Net.Http.HttpRequestMessage? Last { get; private set; }

        public StubHandler(System.Net.HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken ct)
        {
            Last = request;
            return System.Threading.Tasks.Task.FromResult(new System.Net.Http.HttpResponseMessage(_status)
            {
                Content = new System.Net.Http.StringContent(_body),
            });
        }
    }
}
