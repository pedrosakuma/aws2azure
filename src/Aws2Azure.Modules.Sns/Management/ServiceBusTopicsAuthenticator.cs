using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Modules.Sns.Management;

public interface IServiceBusTopicsAuthenticator
{
    ValueTask AuthenticateAsync(HttpRequestMessage request, ServiceBusTopicsCredentials credentials, CancellationToken cancellationToken = default);
}

public sealed class ServiceBusTopicsAuthenticator : IServiceBusTopicsAuthenticator
{
    public const string ServiceBusScope = "https://servicebus.azure.net/.default";

    private readonly EntraIdTokenProvider _tokenProvider;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _tokenTtl;

    public ServiceBusTopicsAuthenticator(
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
        ServiceBusTopicsCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credentials);

        if (!string.IsNullOrWhiteSpace(credentials.SasKeyName)
            && !string.IsNullOrWhiteSpace(credentials.SasKey))
        {
            if (request.RequestUri is null)
            {
                throw new InvalidOperationException("RequestUri is required for Service Bus Topics SAS authentication.");
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
                ServiceBusScope,
                cancellationToken).ConfigureAwait(false);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }
        catch (EntraIdTokenException ex)
        {
            // Convert an AAD token-endpoint failure into the module's status-carrying
            // error so the existing SnsTopicSupport.WriteManagementErrorAsync mapping
            // renders the faithful SNS error (429 -> Throttled, transient ->
            // InternalFailure, auth -> AuthorizationError). The token-endpoint body is
            // dropped — it must never reach the AWS client.
            throw new ServiceBusTopicsManagementException(ex.BackendStatus, null);
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
