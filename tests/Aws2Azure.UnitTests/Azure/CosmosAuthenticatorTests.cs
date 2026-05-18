using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Aws2Azure.Core.Azure;
using Xunit;

namespace Aws2Azure.UnitTests.Azure;

public class CosmosAuthenticatorTests
{
    [Fact]
    public void BuildAuthorizationHeader_MatchesRestSpec()
    {
        var key = Convert.ToBase64String(Encoding.UTF8.GetBytes("super-secret-cosmos-key-12345678"));
        var auth = new CosmosAuthenticator(key);
        var date = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var header = auth.BuildAuthorizationHeader("GET", "dbs", "dbs/ToDoList", date);

        var dateStr = date.UtcDateTime.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        var payload = "get\ndbs\ndbs/ToDoList\n" + dateStr + "\n\n";
        using var hmac = new HMACSHA256(Convert.FromBase64String(key));
        var expected = HttpUtility.UrlEncode("type=master&ver=1.0&sig=" + Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))));
        Assert.Equal(expected, header);
    }

    [Fact]
    public void Apply_SetsAllRequiredHeaders()
    {
        var key = Convert.ToBase64String(Encoding.UTF8.GetBytes("another-secret-key-abcdefghijkl"));
        var auth = new CosmosAuthenticator(key);
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://acct.documents.azure.com/dbs/ToDoList");
        auth.Apply(request, "dbs", "dbs/ToDoList");
        Assert.True(request.Headers.Contains("x-ms-date"));
        Assert.True(request.Headers.Contains("x-ms-version"));
        Assert.True(request.Headers.Contains("Authorization"));
    }
}
