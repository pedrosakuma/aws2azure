using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Modules.Sns.Management;

namespace Aws2Azure.IntegrationTests.Sns;

internal static class SnsRealAzureManagementRestProbe
{
    private const string ServiceBusApiVersion = "2021-05";

    public static Task<Response> CreateTopicAsync(string connectionString, string topicName)
        => SendAsync(
            connectionString,
            HttpMethod.Put,
            topicName,
            ServiceBusAtomXml.BuildTopicDescriptionEntry(new ServiceBusTopicDescription(
                TopicName: topicName,
                SubscriptionCount: 0,
                RequiresDuplicateDetection: false)));

    public static Task<Response> DeleteTopicAsync(string connectionString, string topicName)
        => SendAsync(connectionString, HttpMethod.Delete, topicName);

    public static Task<Response> GetSubscriptionRuleAsync(
        string connectionString,
        string topicName,
        string subscriptionName,
        string ruleName)
        => SendAsync(connectionString, HttpMethod.Get, BuildRulePath(topicName, subscriptionName, ruleName));

    public static Task<Response> PutSubscriptionRuleAsync(
        string connectionString,
        string topicName,
        string subscriptionName,
        ServiceBusSubscriptionRuleDescription description)
        => SendAsync(
            connectionString,
            HttpMethod.Put,
            BuildRulePath(topicName, subscriptionName, description.RuleName),
            ServiceBusAtomXml.BuildSubscriptionRuleDescriptionEntry(description));

    public static Task<Response> DeleteSubscriptionRuleAsync(
        string connectionString,
        string topicName,
        string subscriptionName,
        string ruleName)
        => SendAsync(connectionString, HttpMethod.Delete, BuildRulePath(topicName, subscriptionName, ruleName));

    private static string BuildRulePath(string topicName, string subscriptionName, string ruleName)
        => $"{Uri.EscapeDataString(topicName)}/subscriptions/{Uri.EscapeDataString(subscriptionName)}/rules/{Uri.EscapeDataString(ruleName)}";

    private static async Task<Response> SendAsync(
        string connectionString,
        HttpMethod method,
        string relativePath,
        string? body = null)
    {
        var parts = ParseServiceBusConnectionString(connectionString);
        var namespaceUri = new Uri(parts.Endpoint);
        var requestUri = new Uri(
            $"https://{namespaceUri.Host}/{relativePath}?api-version={ServiceBusApiVersion}",
            UriKind.Absolute);

        using var request = new HttpRequestMessage(method, requestUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            GenerateSharedAccessSignature(requestUri, parts.SharedAccessKeyName, parts.SharedAccessKey, DateTimeOffset.UtcNow.AddMinutes(20)));

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/atom+xml");
            request.Content.Headers.ContentType!.Parameters.Add(new NameValueHeaderValue("type", "entry"));
        }

        using var httpClient = new HttpClient();
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return new Response(response.StatusCode, responseBody, CollectHeaders(response));
    }

    private static ConnectionStringParts ParseServiceBusConnectionString(string connectionString)
    {
        string? endpoint = null;
        string? sharedAccessKeyName = null;
        string? sharedAccessKey = null;

        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = segment[..separator];
            var value = segment[(separator + 1)..];
            switch (key)
            {
                case "Endpoint":
                    endpoint = value;
                    break;
                case "SharedAccessKeyName":
                    sharedAccessKeyName = value;
                    break;
                case "SharedAccessKey":
                    sharedAccessKey = value;
                    break;
            }
        }

        return new ConnectionStringParts(
            endpoint ?? throw new Xunit.Sdk.XunitException("Service Bus connection string did not contain Endpoint."),
            sharedAccessKeyName ?? throw new Xunit.Sdk.XunitException("Service Bus connection string did not contain SharedAccessKeyName."),
            sharedAccessKey ?? throw new Xunit.Sdk.XunitException("Service Bus connection string did not contain SharedAccessKey."));
    }

    private static string GenerateSharedAccessSignature(
        Uri resourceUri,
        string keyName,
        string keyValue,
        DateTimeOffset expiry)
    {
        var resource = resourceUri.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant();
        var encodedResource = WebUtility.UrlEncode(resource);
        var expirySeconds = expiry.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var stringToSign = encodedResource + "\n" + expirySeconds;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(keyValue));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        return "SharedAccessSignature sr=" + encodedResource
             + "&sig=" + WebUtility.UrlEncode(signature)
             + "&se=" + expirySeconds
             + "&skn=" + WebUtility.UrlEncode(keyName);
    }

    private static IReadOnlyDictionary<string, string> CollectHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        foreach (var header in response.Content.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        return headers;
    }

    private sealed record ConnectionStringParts(
        string Endpoint,
        string SharedAccessKeyName,
        string SharedAccessKey);

    internal sealed record Response(
        HttpStatusCode StatusCode,
        string Body,
        IReadOnlyDictionary<string, string> Headers)
    {
        public string FormatDiagnosticSummary()
        {
            var trimmedBody = string.IsNullOrWhiteSpace(Body)
                ? "<empty>"
                : Body.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (trimmedBody.Length > 240)
            {
                trimmedBody = trimmedBody[..240] + "…";
            }

            return $"status={(int)StatusCode}; headers=[Server={GetHeader("Server") ?? "<missing>"}, "
                + $"Content-Length={GetHeader("Content-Length") ?? "<missing>"}, "
                + $"ETag={GetHeader("ETag") ?? "<missing>"}, "
                + $"WWW-Authenticate={GetHeader("WWW-Authenticate") ?? "<missing>"}, "
                + $"x-ms-request-id={GetHeader("x-ms-request-id") ?? "<missing>"}]; body={trimmedBody}";
        }

        private string? GetHeader(string name)
            => Headers.TryGetValue(name, out var value) ? value : null;
    }
}
