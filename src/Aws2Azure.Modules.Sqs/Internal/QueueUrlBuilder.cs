using System;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sqs.Internal;

/// <summary>
/// Builds the SQS-shaped <c>QueueUrl</c> the proxy returns to clients.
/// AWS SDKs use this URL verbatim for follow-up requests (SendMessage,
/// ReceiveMessage, …), so it must:
/// <list type="bullet">
///   <item>Use the scheme + host the caller reached us on so clients
///         keep routing back to the proxy.</item>
///   <item>Carry an AWS-shaped account-id path segment so SDK URL parsers
///         (which expect <c>{scheme}://{host}/{account}/{queue}</c>) don't
///         choke.</item>
/// </list>
/// </summary>
internal static class QueueUrlBuilder
{
    /// <summary>
    /// Placeholder AWS account id (12 zeros) used as the URL path prefix.
    /// The proxy doesn't model AWS accounts; the account id is purely a
    /// URL convention that AWS SDKs require to parse the queue URL.
    /// </summary>
    public const string PlaceholderAccountId = "000000000000";

    public static string Build(HttpContext context, string queueName)
    {
        // Request.Host can include the port; preserve it so the SDK reaches
        // back via the same socket the proxy is bound to.
        var scheme = context.Request.Scheme;
        var host = context.Request.Host.HasValue ? context.Request.Host.Value : "localhost";
        return $"{scheme}://{host}/{PlaceholderAccountId}/{queueName}";
    }

    /// <summary>
    /// Extracts the queue name from a fully-qualified QueueUrl. Tolerates
    /// the AWS-account path segment being absent or different. Returns
    /// null when the URL is not in a recognisable form.
    /// </summary>
    public static string? ExtractQueueName(string? queueUrl)
    {
        if (string.IsNullOrEmpty(queueUrl)) return null;
        if (!Uri.TryCreate(queueUrl, UriKind.Absolute, out var uri)) return null;

        // Path forms accepted:  /<account>/<name>   |   /<name>
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;
        var name = segments[^1];
        return string.IsNullOrEmpty(name) ? null : Uri.UnescapeDataString(name);
    }
}
