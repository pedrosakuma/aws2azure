using System;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Aws2Azure.Core.Azure;
using Xunit;

namespace Aws2Azure.UnitTests.Azure;

public class ServiceBusSasAuthenticatorTests
{
    [Fact]
    public void GenerateToken_MatchesExpectedFormat()
    {
        const string keyName = "RootManageSharedAccessKey";
        const string key = "SAS_KEY_VALUE";
        var auth = new ServiceBusSasAuthenticator(keyName, key);
        var resource = new Uri("https://my-namespace.servicebus.windows.net/my-queue");
        var expiry = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        var token = auth.GenerateToken(resource, expiry);

        var expectedEncoded = HttpUtility.UrlEncode("https://my-namespace.servicebus.windows.net/my-queue");
        var stringToSign = expectedEncoded + "\n1700000000";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var expectedSig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        Assert.Equal(
            "SharedAccessSignature sr=" + expectedEncoded +
            "&sig=" + HttpUtility.UrlEncode(expectedSig) +
            "&se=1700000000&skn=" + HttpUtility.UrlEncode(keyName),
            token);
    }
}
