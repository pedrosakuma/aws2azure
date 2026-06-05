using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Modules.Kinesis.EventHubsRest;

public interface IEventHubsAuthenticator
{
    ValueTask AuthenticateAsync(HttpRequestMessage request, EventHubsCredentials credentials, CancellationToken cancellationToken = default);
}

public sealed class EventHubsAuthenticator : IEventHubsAuthenticator
{
    public const string EventHubsScope = "https://eventhubs.azure.net/.default";

    private readonly EntraIdTokenProvider _tokenProvider;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _tokenTtl;

    public EventHubsAuthenticator(
        EntraIdTokenProvider tokenProvider,
        TimeProvider? clock = null,
        TimeSpan? tokenTtl = null)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _tokenProvider = tokenProvider;
        _clock = clock ?? TimeProvider.System;
        _tokenTtl = tokenTtl ?? TimeSpan.FromMinutes(20);
    }

    public async ValueTask AuthenticateAsync(
        HttpRequestMessage request,
        EventHubsCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credentials);

        if (!string.IsNullOrWhiteSpace(credentials.SasKeyName)
            && !string.IsNullOrWhiteSpace(credentials.SasKey))
        {
            if (request.RequestUri is null)
            {
                throw new InvalidOperationException("RequestUri is required for Event Hubs SAS authentication.");
            }

            request.Headers.TryAddWithoutValidation(
                "Authorization",
                GenerateSharedAccessSignature(request.RequestUri, credentials.SasKeyName, credentials.SasKey, _clock.GetUtcNow().Add(_tokenTtl)));
            return;
        }

        try
        {
            var token = await _tokenProvider.GetTokenAsync(
                credentials.TenantId!,
                credentials.ClientId!,
                credentials.ClientSecret!,
                EventHubsScope,
                cancellationToken).ConfigureAwait(false);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }
        catch (EntraIdTokenException ex)
        {
            // Convert an AAD token-endpoint failure into the module's status-carrying
            // error so the existing KinesisMetadataSupport.WriteManagementErrorAsync
            // mapping renders the faithful Kinesis error (429 -> LimitExceededException,
            // transient -> InternalFailure, auth -> AccessDeniedException). The
            // token-endpoint body is dropped — it must never reach the AWS client.
            throw new EventHubsManagementException(ex.BackendStatus, null);
        }
    }

    internal static string GenerateSharedAccessSignature(Uri resourceUri, string keyName, string keyValue, DateTimeOffset expiry)
    {
        ArgumentNullException.ThrowIfNull(resourceUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyValue);

        var resource = resourceUri.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant();
        var encodedResource = HttpUtility.UrlEncode(resource);
        var expirySeconds = expiry.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var stringToSign = encodedResource + "\n" + expirySeconds;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(keyValue));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        return "SharedAccessSignature sr=" + encodedResource
             + "&sig=" + HttpUtility.UrlEncode(signature)
             + "&se=" + expirySeconds
             + "&skn=" + HttpUtility.UrlEncode(keyName);
    }
}
