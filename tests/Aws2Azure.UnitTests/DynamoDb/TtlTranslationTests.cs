using System.Buffers;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

/// <summary>
/// Unit coverage for the DynamoDB→Cosmos TTL translation (#465): the pure
/// epoch-to-relative computation and the encoder threading that emits the
/// Cosmos reserved <c>ttl</c> property.
/// </summary>
public class TtlTranslationTests
{
    private const long Now = 1_700_000_000;

    private static JsonElement Item(string json) => JsonDocument.Parse(json).RootElement;

    private static TableTimeToLive Enabled(string attr = "expiresAt")
        => new() { Enabled = true, AttributeName = attr };

    [Fact]
    public void Future_expiry_maps_to_relative_seconds()
    {
        var item = Item("{\"expiresAt\":{\"N\":\"" + (Now + 3600) + "\"}}");
        Assert.Equal(3600, TtlTranslation.ComputeItemTtlSeconds(item, Enabled(), Now));
    }

    [Fact]
    public void Past_expiry_within_five_years_maps_to_one()
    {
        var item = Item("{\"expiresAt\":{\"N\":\"" + (Now - 60) + "\"}}");
        Assert.Equal(1, TtlTranslation.ComputeItemTtlSeconds(item, Enabled(), Now));
    }

    [Fact]
    public void Expiry_more_than_five_years_in_past_is_ignored()
    {
        long sixYears = 6L * 365 * 24 * 60 * 60;
        var item = Item("{\"expiresAt\":{\"N\":\"" + (Now - sixYears) + "\"}}");
        Assert.Null(TtlTranslation.ComputeItemTtlSeconds(item, Enabled(), Now));
    }

    [Fact]
    public void Fractional_epoch_is_floored()
    {
        var item = Item("{\"expiresAt\":{\"N\":\"" + (Now + 100) + ".9\"}}");
        Assert.Equal(100, TtlTranslation.ComputeItemTtlSeconds(item, Enabled(), Now));
    }

    [Fact]
    public void Delta_above_int_max_is_clamped()
    {
        long far = Now + (long)int.MaxValue + 10_000;
        var item = Item("{\"expiresAt\":{\"N\":\"" + far + "\"}}");
        Assert.Equal(int.MaxValue, TtlTranslation.ComputeItemTtlSeconds(item, Enabled(), Now));
    }

    [Fact]
    public void Extreme_past_epoch_does_not_overflow_into_future()
    {
        // long.MinValue would overflow `epoch - now` into a positive delta if the
        // subtraction ran before the past-due classification; it must be treated
        // as more-than-five-years-past (non-expiring).
        var item = Item("{\"expiresAt\":{\"N\":\"" + long.MinValue + "\"}}");
        Assert.Null(TtlTranslation.ComputeItemTtlSeconds(item, Enabled(), Now));
    }

    [Fact]
    public void Missing_attribute_yields_no_ttl()
    {
        var item = Item("{\"name\":{\"S\":\"widget\"}}");
        Assert.Null(TtlTranslation.ComputeItemTtlSeconds(item, Enabled(), Now));
    }

    [Fact]
    public void Non_number_attribute_yields_no_ttl()
    {
        var item = Item("{\"expiresAt\":{\"S\":\"soon\"}}");
        Assert.Null(TtlTranslation.ComputeItemTtlSeconds(item, Enabled(), Now));
    }

    [Fact]
    public void Disabled_config_yields_no_ttl()
    {
        var item = Item("{\"expiresAt\":{\"N\":\"" + (Now + 3600) + "\"}}");
        var disabled = new TableTimeToLive { Enabled = false, AttributeName = "expiresAt" };
        Assert.Null(TtlTranslation.ComputeItemTtlSeconds(item, disabled, Now));
    }

    [Fact]
    public void Null_config_yields_no_ttl()
    {
        var item = Item("{\"expiresAt\":{\"N\":\"" + (Now + 3600) + "\"}}");
        Assert.Null(TtlTranslation.ComputeItemTtlSeconds(item, null, Now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Encoder_emits_ttl_property_when_set(bool fromWireBytes)
    {
        var itemJson = "{\"expiresAt\":{\"N\":\"" + (Now + 3600) + "\"},\"name\":{\"S\":\"widget\"}}";
        var output = new ArrayBufferWriter<byte>();
        if (fromWireBytes)
        {
            InferredAttributeStorage.WriteCosmosDocument(output, "id1", "pk1",
                System.Text.Encoding.UTF8.GetBytes(itemJson).AsSpan(), ttlSeconds: 3600);
        }
        else
        {
            InferredAttributeStorage.WriteCosmosDocument(output, "id1", "pk1", Item(itemJson), ttlSeconds: 3600);
        }

        using var doc = JsonDocument.Parse(output.WrittenMemory);
        Assert.Equal(3600, doc.RootElement.GetProperty("ttl").GetInt32());
        Assert.Equal("widget", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("id1", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void Encoder_omits_ttl_property_when_unset()
    {
        var itemJson = "{\"name\":{\"S\":\"widget\"}}";
        var output = new ArrayBufferWriter<byte>();
        InferredAttributeStorage.WriteCosmosDocument(output, "id1", "pk1", Item(itemJson), ttlSeconds: null);

        using var doc = JsonDocument.Parse(output.WrittenMemory);
        Assert.False(doc.RootElement.TryGetProperty("ttl", out _));
    }
}
