using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Amqp.Security;

namespace Aws2Azure.UnitTests.Amqp.Security;

/// <summary>
/// Conformance tests for the SAS token format (Service Bus
/// CBS-compatible). The expected signature is recomputed in-test
/// using the same algorithm via <see cref="HttpUtility.UrlEncode"/>
/// so we cross-check the AOT-friendly <c>UrlEncoder</c> path against
/// the canonical .NET implementation.
/// </summary>
public sealed class ServiceBusSasTokenProviderTests
{
    private const string KeyName = "RootManageSharedAccessKey";
    private const string KeyValue = "abcdefghijklmnopqrstuvwxyzABCDEF0123456789+/=";

    [Fact]
    public void GenerateToken_matches_reference_signature()
    {
        var fixedTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var clock = new ManualClock(fixedTime);
        var provider = new ServiceBusSasTokenProvider(KeyName, KeyValue,
            ttl: TimeSpan.FromMinutes(20), clock: clock);

        const string audience = "amqps://ns.servicebus.windows.net/queue1";
        var token = provider.GetToken(audience);

        var expiry = fixedTime.Add(TimeSpan.FromMinutes(20)).ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        var resource = audience.ToLowerInvariant();
        var encodedResource = UrlEncoder.Encode(resource);
        var stringToSign = encodedResource + "\n" + expiry;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(KeyValue));
        var expectedSig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        var expected = "SharedAccessSignature sr=" + encodedResource
            + "&sig=" + UrlEncoder.Encode(expectedSig)
            + "&se=" + expiry
            + "&skn=" + UrlEncoder.Encode(KeyName);

        Assert.Equal(expected, token.Value);
        Assert.NotNull(token.ExpiresAtUtc);
        Assert.Equal(fixedTime.Add(TimeSpan.FromMinutes(20)), token.ExpiresAtUtc!.Value);
    }

    [Fact]
    public void TokenType_is_servicebus_sastoken()
    {
        var provider = new ServiceBusSasTokenProvider(KeyName, KeyValue);
        Assert.Equal("servicebus.windows.net:sastoken", provider.TokenType);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void GetToken_rejects_blank_audience(string audience)
    {
        var provider = new ServiceBusSasTokenProvider(KeyName, KeyValue);
        Assert.Throws<ArgumentException>(() => provider.GetToken(audience));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
