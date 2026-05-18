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
/// Generates Azure Service Bus SAS tokens of the form
/// <c>SharedAccessSignature sr={uri}&amp;sig={sig}&amp;se={expiry}&amp;skn={keyName}</c>.
/// The signature is HMAC-SHA256 over <c>urlEncode(resourceUri) + "\n" + expiry</c>.
/// </summary>
public sealed class ServiceBusSasAuthenticator : IAzureAuthenticator
{
    private readonly string _keyName;
    private readonly byte[] _key;
    private readonly TimeSpan _tokenTtl;
    private readonly TimeProvider _clock;

    public ServiceBusSasAuthenticator(string keyName, string keyValue, TimeSpan? tokenTtl = null, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyValue);
        _keyName = keyName;
        _key = Encoding.UTF8.GetBytes(keyValue);
        _tokenTtl = tokenTtl ?? TimeSpan.FromMinutes(20);
        _clock = clock ?? TimeProvider.System;
    }

    public ValueTask AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        if (request.RequestUri is null)
        {
            throw new InvalidOperationException("RequestUri is required for Service Bus SAS signing.");
        }
        var token = GenerateToken(request.RequestUri, _clock.GetUtcNow().Add(_tokenTtl));
        request.Headers.TryAddWithoutValidation("Authorization", token);
        return ValueTask.CompletedTask;
    }

    public string GenerateToken(Uri resourceUri, DateTimeOffset expiry)
    {
        ArgumentNullException.ThrowIfNull(resourceUri);

        var resource = resourceUri.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant();
        var encodedResource = HttpUtility.UrlEncode(resource);
        var expirySeconds = expiry.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var stringToSign = encodedResource + "\n" + expirySeconds;

        using var hmac = new HMACSHA256(_key);
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        return "SharedAccessSignature sr=" + encodedResource +
               "&sig=" + HttpUtility.UrlEncode(signature) +
               "&se=" + expirySeconds +
               "&skn=" + HttpUtility.UrlEncode(_keyName);
    }
}
