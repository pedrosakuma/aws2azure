using System.IO;
using System.Net;
using System.Text;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.Operations;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.Sns;

public sealed class SnsManagementThrottleTests
{
    [Fact]
    public async Task TooManyRequests_maps_to_Throttled_429()
    {
        // The shared AzureHttpClient passes 429 (Service Bus management throttling)
        // through without internal retry, so the mapper must surface the SNS
        // throttle the AWS SDK retries with back-off. The SNS query wire code is
        // "Throttled" (the ThrottledException shape) at HTTP 429.
        var context = NewContext();
        var ex = new ServiceBusTopicsManagementException(HttpStatusCode.TooManyRequests, responseBody: null);

        await SnsTopicSupport.WriteManagementErrorAsync(context, ex);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Contains("<Code>Throttled</Code>", ReadBody(context));
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
