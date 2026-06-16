using System.Text;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.TestSupport.Http;

/// <summary>
/// Canonical HttpContext factory and response-body reader for module tests.
/// </summary>
public static class TestHttpContext
{
    public static DefaultHttpContext CreateContext(
        string body = "",
        string method = "POST",
        string path = "/",
        string? queryString = null,
        string? contentType = null,
        IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (!string.IsNullOrEmpty(queryString))
        {
            context.Request.QueryString = queryString[0] == '?'
                ? new QueryString(queryString)
                : new QueryString("?" + queryString);
        }

        if (contentType is not null)
        {
            context.Request.ContentType = contentType;
        }

        if (headers is not null)
        {
            foreach (var header in headers)
            {
                context.Request.Headers[header.Key] = header.Value;
            }
        }

        SetRequestBody(context, body);
        context.Response.Body = new MemoryStream();
        return context;
    }

    public static DefaultHttpContext CreateAwsJsonContext(string target, string body = "{}")
        => CreateContext(
            body,
            HttpMethods.Post,
            contentType: "application/x-amz-json-1.1",
            headers: [new KeyValuePair<string, string>("X-Amz-Target", target)]);

    public static void SetRequestBody(HttpContext context, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
    }

    public static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true).ReadToEnd();
    }

    public static async Task<string> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true).ReadToEndAsync(cancellationToken);
    }
}
