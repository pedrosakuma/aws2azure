using System;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Aws2Azure.Core.Azure;

/// <summary>
/// Implements Cosmos DB master-key authorization. Each request requires a
/// fresh signature computed over a strictly-formatted payload, then encoded
/// into <c>type=master&amp;ver=1.0&amp;sig={sig}</c> and URL-encoded.
/// </summary>
public sealed class CosmosAuthenticator
{
    private readonly byte[] _key;
    private readonly TimeProvider _clock;

    public CosmosAuthenticator(string base64Key, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);
        _key = Convert.FromBase64String(base64Key);
        _clock = clock ?? TimeProvider.System;
    }

    public string BuildAuthorizationHeader(string verb, string resourceType, string resourceLink, DateTimeOffset utcDate)
    {
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(resourceType);
        ArgumentNullException.ThrowIfNull(resourceLink);

        var dateHeader = utcDate.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        var payload =
            verb.ToLowerInvariant() + "\n" +
            resourceType.ToLowerInvariant() + "\n" +
            resourceLink + "\n" +
            dateHeader.ToLowerInvariant() + "\n" +
            "\n";

        using var hmac = new HMACSHA256(_key);
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        return HttpUtility.UrlEncode("type=master&ver=1.0&sig=" + signature);
    }

    public void Apply(HttpRequestMessage request, string resourceType, string resourceLink)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = _clock.GetUtcNow();
        var dateHeader = now.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        request.Headers.TryAddWithoutValidation("x-ms-date", dateHeader);
        request.Headers.TryAddWithoutValidation("x-ms-version", "2018-12-31");
        var auth = BuildAuthorizationHeader(request.Method.Method, resourceType, resourceLink, now);
        request.Headers.TryAddWithoutValidation("Authorization", auth);
    }
}
