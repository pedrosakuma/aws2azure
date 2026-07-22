using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Operations;

namespace Aws2Azure.UnitTests.Sns;

public sealed class SnsSubscriptionPaginationTokenTests
{
    [Fact]
    public void Token_is_deterministic_versioned_signed_and_restart_stable()
    {
        var first = SnsSubscriptionSupport.EncodeNextToken(
            SigningKey,
            SnsOperation.ListSubscriptionsByTopic,
            "orders",
            topicSkip: 0,
            subscriptionSkipWithinTopic: 100);
        var afterRestart = SnsSubscriptionSupport.EncodeNextToken(
            SigningKey,
            SnsOperation.ListSubscriptionsByTopic,
            "orders",
            topicSkip: 0,
            subscriptionSkipWithinTopic: 100);

        Assert.Equal(first, afterRestart);
        Assert.StartsWith("sns-sub.", first, StringComparison.Ordinal);
        Assert.True(SnsSubscriptionSupport.TryDecodeNextToken(
            first,
            SigningKey,
            SnsOperation.ListSubscriptionsByTopic,
            "orders",
            out var decoded));
        Assert.Equal(1, decoded.Version);
        Assert.Equal(100, decoded.SubscriptionSkipWithinTopic);
    }

    [Fact]
    public void Token_rejects_tampering_wrong_key_operation_topic_and_version()
    {
        var token = SnsSubscriptionSupport.EncodeNextToken(
            SigningKey,
            SnsOperation.ListSubscriptionsByTopic,
            "orders",
            topicSkip: 0,
            subscriptionSkipWithinTopic: 100);
        var tampered = token[..^1] + (token[^1] == 'a' ? "b" : "a");

        Assert.False(TryDecode(tampered, SigningKey, SnsOperation.ListSubscriptionsByTopic, "orders"));
        Assert.False(TryDecode(token, "different-key", SnsOperation.ListSubscriptionsByTopic, "orders"));
        Assert.False(TryDecode(token, SigningKey, SnsOperation.ListSubscriptions, null));
        Assert.False(TryDecode(token, SigningKey, SnsOperation.ListSubscriptionsByTopic, "payments"));
        Assert.False(TryDecode(CreateVersionTwoToken(), SigningKey, SnsOperation.ListSubscriptionsByTopic, "orders"));
    }

    private static bool TryDecode(string token, string key, SnsOperation operation, string? topicName)
        => SnsSubscriptionSupport.TryDecodeNextToken(token, key, operation, topicName, out _);

    private static string CreateVersionTwoToken()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new SnsListSubscriptionsNextToken
            {
                Version = 2,
                Operation = SnsOperation.ListSubscriptionsByTopic.ToString(),
                TopicName = "orders",
                SubscriptionSkipWithinTopic = 100,
            },
            SnsSubscriptionJsonContext.Default.SnsListSubscriptionsNextToken);
        Span<byte> signature = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningKey), payload, signature);
        return "sns-sub." + Base64Url(payload) + "." + Base64Url(signature);
    }

    private static string Base64Url(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private const string SigningKey = "unit-test-pagination-signing-key";
}
