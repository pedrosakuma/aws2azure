using System.IO;
using System.Net;
using System.Text;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.Operations;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.Kinesis;

public sealed class KinesisManagementThrottleTests
{
    [Fact]
    public async Task TooManyRequests_maps_to_LimitExceededException()
    {
        // The shared AzureHttpClient passes 429 (Event Hubs management throttling)
        // through without internal retry, so the mapper must surface the Kinesis
        // control-plane throttle the AWS SDK retries with back-off.
        var context = NewContext();
        var ex = new EventHubsManagementException(HttpStatusCode.TooManyRequests, responseBody: null);

        await KinesisMetadataSupport.WriteManagementErrorAsync(context, ex, "orders");

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("LimitExceededException", ReadBody(context));
    }

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();
    }
}
