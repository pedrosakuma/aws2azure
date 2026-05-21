using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// DynamoDB TTL endpoints (stubbed). DynamoDB TTL semantics
/// (per-table attribute name flagged as expiry epoch, background sweep)
/// have no direct Cosmos equivalent without rewriting every PutItem and
/// UpdateItem to translate the named attribute into Cosmos' <c>ttl</c>
/// field. That translation is intentionally deferred to a later slice.
///
/// <para>
/// <c>DescribeTimeToLive</c> returns <c>DISABLED</c> so SDK callers that
/// probe TTL on every connection get a clean response instead of a 501.
/// <c>UpdateTimeToLive</c> fails loudly with <c>ValidationException</c>
/// so callers don't silently assume TTL is honoured.
/// </para>
/// </summary>
internal static class TimeToLiveHandlers
{
    public static Task HandleDescribeTimeToLiveAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        // DDB allows describing TTL on any existing table; we don't even
        // bother to validate the table exists since the response shape is
        // a constant. SDK callers will fail later on real ops if the
        // table is missing.
        var resp = new DescribeTimeToLiveResponse
        {
            TimeToLiveDescription = new TimeToLiveDescription
            {
                TimeToLiveStatus = "DISABLED",
            },
        };
        return CosmosOpsShared.WriteJsonAsync(ctx, 200, resp,
            TimeToLiveJsonContext.Default.DescribeTimeToLiveResponse);
    }

    public static Task HandleUpdateTimeToLiveAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
            "UpdateTimeToLive is not supported by aws2azure: Azure Cosmos DB requires item-level TTL translation that is not yet implemented. Items written through this proxy will not be expired.");
    }
}

internal sealed class DescribeTimeToLiveResponse
{
    [JsonPropertyName("TimeToLiveDescription")]
    public TimeToLiveDescription? TimeToLiveDescription { get; set; }
}

internal sealed class TimeToLiveDescription
{
    [JsonPropertyName("TimeToLiveStatus")]
    public string? TimeToLiveStatus { get; set; }

    [JsonPropertyName("AttributeName")]
    public string? AttributeName { get; set; }
}

[JsonSerializable(typeof(DescribeTimeToLiveResponse))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class TimeToLiveJsonContext : JsonSerializerContext
{
}
