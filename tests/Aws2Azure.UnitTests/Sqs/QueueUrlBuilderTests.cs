using Aws2Azure.Modules.Sqs.Internal;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public class QueueUrlBuilderTests
{
    [Fact]
    public void Builds_url_using_request_scheme_and_host()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("sqs.proxy.localtest.me", 8443);

        var url = QueueUrlBuilder.Build(ctx, "my-queue");
        Assert.Equal("https://sqs.proxy.localtest.me:8443/000000000000/my-queue", url);
    }

    [Fact]
    public void Extract_returns_last_path_segment()
    {
        Assert.Equal("my-queue",
            QueueUrlBuilder.ExtractQueueName("https://sqs.us-east-1.amazonaws.com/123456789012/my-queue"));
        Assert.Equal("my-queue.fifo",
            QueueUrlBuilder.ExtractQueueName("https://example.com/my-queue.fifo"));
    }

    [Fact]
    public void Extract_unescapes_percent_encoding()
    {
        Assert.Equal("a b",
            QueueUrlBuilder.ExtractQueueName("https://example.com/000000000000/a%20b"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-url")]
    [InlineData("https://example.com/")]
    public void Extract_returns_null_for_unrecognised_inputs(string? input) =>
        Assert.Null(QueueUrlBuilder.ExtractQueueName(input));
}
