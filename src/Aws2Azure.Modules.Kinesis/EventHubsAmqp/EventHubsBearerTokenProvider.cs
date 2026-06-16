using System.Buffers;
using System.Text.Json;
using Aws2Azure.Amqp.Security;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.EventHubsRest;

namespace Aws2Azure.Modules.Kinesis.EventHubsAmqp;

internal sealed class EventHubsBearerTokenProvider : IAmqpTokenProvider
{
    private readonly EntraIdTokenProvider _tokenProvider;
    private readonly EventHubsCredentials _credentials;

    public EventHubsBearerTokenProvider(EntraIdTokenProvider tokenProvider, EventHubsCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(credentials);
        _tokenProvider = tokenProvider;
        _credentials = credentials;
    }

    public string TokenType => "jwt";

    public AmqpToken GetToken(string audience)
    {
        _ = audience;
        var auth = new AadAuthSettings(_credentials.AuthMode, _credentials.TenantId, _credentials.ClientId, _credentials.ClientSecret);
        var token = _tokenProvider
            .GetTokenAsync(
                auth,
                EventHubsAuthenticator.EventHubsScope)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return new AmqpToken(token, TryParseJwtExpiry(token));
    }

    private static DateTimeOffset? TryParseJwtExpiry(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var firstDot = token.IndexOf('.');
        if (firstDot < 0)
        {
            return null;
        }

        var secondDot = token.IndexOf('.', firstDot + 1);
        if (secondDot < 0)
        {
            return null;
        }

        var payload = token.AsSpan(firstDot + 1, secondDot - firstDot - 1);
        if (payload.IsEmpty)
        {
            return null;
        }

        var requiredLength = GetBase64UrlDecodedLength(payload.Length);
        var rented = ArrayPool<byte>.Shared.Rent(requiredLength);
        try
        {
            if (!TryDecodeBase64Url(payload, rented, out var written))
            {
                return null;
            }

            var reader = new Utf8JsonReader(rented.AsSpan(0, written), isFinalBlock: true, state: default);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName
                    && reader.ValueTextEquals("exp"u8)
                    && reader.Read()
                    && reader.TokenType == JsonTokenType.Number
                    && reader.TryGetInt64(out var seconds))
                {
                    return DateTimeOffset.FromUnixTimeSeconds(seconds);
                }
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int GetBase64UrlDecodedLength(int encodedLength)
        => ((encodedLength + 3) / 4) * 3;

    private static bool TryDecodeBase64Url(ReadOnlySpan<char> input, Span<byte> destination, out int written)
    {
        written = 0;
        var padding = (4 - (input.Length % 4)) % 4;
        Span<char> base64 = input.Length + padding <= 512
            ? stackalloc char[input.Length + padding]
            : new char[input.Length + padding];

        for (var i = 0; i < input.Length; i++)
        {
            base64[i] = input[i] switch
            {
                '-' => '+',
                '_' => '/',
                _ => input[i],
            };
        }

        for (var i = 0; i < padding; i++)
        {
            base64[input.Length + i] = '=';
        }

        return Convert.TryFromBase64Chars(base64, destination, out written);
    }
}
