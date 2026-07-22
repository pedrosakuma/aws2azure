using System;
using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// DynamoDB TTL endpoints (UpdateTimeToLive / DescribeTimeToLive), mapped onto
/// Azure Cosmos DB's native TTL. DynamoDB designates one item attribute (a
/// Number holding an <b>absolute</b> epoch-seconds expiry) and a background
/// sweep deletes items once that time passes. Cosmos uses a container-level
/// <c>defaultTtl</c> to arm TTL plus a per-item <c>ttl</c> that is a
/// <b>relative</b> duration from the item's last-write <c>_ts</c>.
///
/// <para>
/// The proxy bridges the two: <c>UpdateTimeToLive</c> records the attribute name
/// in the table metadata sidecar and sets the container's <c>defaultTtl</c> to
/// <c>-1</c> (TTL armed, no blanket expiry); every item write then translates
/// the named attribute into a relative <c>ttl</c> (see
/// <see cref="TtlTranslation"/>). Because the relative ttl is recomputed on each
/// write, the absolute expiry stays correct across updates.
/// </para>
///
/// <para>
/// Behaviour differences from DynamoDB are documented in
/// <c>docs/gaps/dynamodb/UpdateTimeToLive.yaml</c> and
/// <c>DescribeTimeToLive.yaml</c>: Cosmos's sweep cadence differs from
/// DynamoDB's best-effort window (up to ~48h), and items written before TTL was
/// enabled are not retroactively given a per-item ttl until they are next
/// rewritten.
/// </para>
/// </summary>
internal static class TimeToLiveHandlers
{
    public static async Task HandleDescribeTimeToLiveAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        DescribeTimeToLiveRequest? req;
        try
        {
            req = body.Length > 0
                ? JsonSerializer.Deserialize(body, TimeToLiveJsonContext.Default.DescribeTimeToLiveRequest)
                : new DescribeTimeToLiveRequest();
        }
        catch (JsonException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "SerializationException",
                "Malformed JSON: " + ex.Message).ConfigureAwait(false);
            return;
        }
        if (req is null || !DynamoDbNames.IsValidTableName(req.TableName))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "TableName is required and must match [a-zA-Z0-9_.-]{3,255}.").ConfigureAwait(false);
            return;
        }

        using var metaResult = await CosmosOpsShared.TryReadTableMetadataAsync(cosmos, req.TableName!, ct).ConfigureAwait(false);
        if (metaResult.Status == CosmosOpsShared.TableMetadataReadStatus.CosmosError)
        {
            await CosmosOpsShared.WriteCosmosErrorAsync(ctx, metaResult.ErrorResponse!, ct).ConfigureAwait(false);
            return;
        }
        if (metaResult.Status == CosmosOpsShared.TableMetadataReadStatus.NotFound)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                $"Requested resource not found: Table: {req.TableName} not found").ConfigureAwait(false);
            return;
        }

        var ttl = metaResult.Metadata!.TimeToLive;
        var description = ttl is { Enabled: true }
            ? new TimeToLiveDescription { TimeToLiveStatus = "ENABLED", AttributeName = ttl.AttributeName }
            : new TimeToLiveDescription { TimeToLiveStatus = "DISABLED" };

        await CosmosOpsShared.WriteJsonAsync(ctx, 200,
            new DescribeTimeToLiveResponse { TimeToLiveDescription = description },
            TimeToLiveJsonContext.Default.DescribeTimeToLiveResponse).ConfigureAwait(false);
    }

    public static async Task HandleUpdateTimeToLiveAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        UpdateTimeToLiveRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, TimeToLiveJsonContext.Default.UpdateTimeToLiveRequest);
        }
        catch (JsonException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "SerializationException",
                "Malformed JSON: " + ex.Message).ConfigureAwait(false);
            return;
        }
        if (req is null || !DynamoDbNames.IsValidTableName(req.TableName))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "TableName is required and must match [a-zA-Z0-9_.-]{3,255}.").ConfigureAwait(false);
            return;
        }
        var spec = req.TimeToLiveSpecification;
        if (spec is null)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "TimeToLiveSpecification is required.").ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrEmpty(spec.AttributeName))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "TimeToLiveSpecification.AttributeName is required.").ConfigureAwait(false);
            return;
        }

        bool enabling = spec.Enabled;
        string attributeName = spec.AttributeName!;

        // 1. Arm (defaultTtl = -1) or disarm (remove defaultTtl) the container
        //    FIRST. Doing this before the metadata write keeps the failure modes
        //    benign: if the metadata write then fails, the proxy keeps reporting
        //    the prior TTL state and does not write a per-item ttl, so nothing
        //    expires unexpectedly.
        //
        //    Note: the container replace and the metadata write are not a single
        //    atomic unit, so racing concurrent enable/disable calls for the SAME
        //    table can interleave (one call's container replace landing between
        //    the other's container replace and metadata write). This is an
        //    accepted limitation: TTL is a rare control-plane op and a single
        //    DynamoDB client does not issue concurrent UpdateTimeToLive for one
        //    table (real DynamoDB serialises via transient ENABLING/DISABLING
        //    states); cross-sidecar coordination is out of scope. Documented in
        //    docs/gaps/dynamodb/UpdateTimeToLive.yaml.
        if (!await TryReplaceContainerDefaultTtlAsync(ctx, cosmos, req.TableName!, enabling ? -1 : (int?)null, ct).ConfigureAwait(false))
        {
            return;
        }

        // 2. Persist the TTL config (attribute name + enabled flag) into the
        //    sidecar so write paths and DescribeTimeToLive can see it.
        bool persisted = await CosmosOpsShared.MutateTableMetadataAsync(
            ctx, cosmos, req.TableName!,
            meta =>
            {
                meta.TimeToLive ??= new TableTimeToLive();
                meta.TimeToLive.Enabled = enabling;
                meta.TimeToLive.AttributeName = attributeName;
            },
            ct).ConfigureAwait(false);
        if (!persisted)
        {
            return;
        }

        await CosmosOpsShared.WriteJsonAsync(ctx, 200,
            new UpdateTimeToLiveResponse
            {
                TimeToLiveSpecification = new TimeToLiveSpecification
                {
                    AttributeName = attributeName,
                    Enabled = enabling,
                },
            },
            TimeToLiveJsonContext.Default.UpdateTimeToLiveResponse).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the Cosmos container's <c>defaultTtl</c> by reading the current
    /// collection definition, rewriting only the <c>defaultTtl</c> field (set to
    /// <paramref name="defaultTtl"/>, or removed when null), and PUTting it back.
    /// Every non-system top-level property (id, partitionKey, indexingPolicy,
    /// uniqueKeyPolicy, …) is preserved verbatim so the replace does not clobber
    /// the container's other settings. Returns false and writes an AWS error
    /// response on any failure.
    /// </summary>
    private static async Task<bool> TryReplaceContainerDefaultTtlAsync(
        HttpContext ctx, CosmosClient cosmos, string tableName, int? defaultTtl, CancellationToken ct)
    {
        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + tableName;

        using var getResp = await cosmos.SendAsync(
            HttpMethod.Get, "colls", collLink, "/" + collLink,
            content: null, extraHeaders: null, ct).ConfigureAwait(false);
        if (getResp.StatusCode == HttpStatusCode.NotFound)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                $"Requested resource not found: Table: {tableName} not found").ConfigureAwait(false);
            return false;
        }
        if (!getResp.IsSuccessStatusCode)
        {
            await CosmosOpsShared.WriteCosmosErrorAsync(ctx, getResp, ct).ConfigureAwait(false);
            return false;
        }

        using var bodyBuf = await CosmosOpsShared.ReadCosmosJsonBodyAsync(getResp.Content, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bodyBuf.WrittenMemory);

        var replaceBody = new ArrayBufferWriter<byte>(bodyBuf.WrittenMemory.Length + 32);
        using (var writer = new Utf8JsonWriter(replaceBody))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Skip Cosmos system metadata (_rid/_ts/_self/_etag/_docs/…) — it
                // must not be echoed back on a replace — and the existing
                // defaultTtl, which we set explicitly below.
                if (prop.Name.Length > 0 && prop.Name[0] == '_')
                {
                    continue;
                }
                if (prop.NameEquals("defaultTtl"))
                {
                    continue;
                }
                prop.WriteTo(writer);
            }
            if (defaultTtl.HasValue)
            {
                writer.WriteNumber("defaultTtl", defaultTtl.Value);
            }
            writer.WriteEndObject();
        }

        using var putResp = await cosmos.SendAsync(
            HttpMethod.Put, "colls", collLink, "/" + collLink,
            replaceBody.WrittenMemory, "application/json", extraHeaders: null, ct).ConfigureAwait(false);
        if (!putResp.IsSuccessStatusCode)
        {
            await CosmosOpsShared.WriteCosmosErrorAsync(ctx, putResp, ct).ConfigureAwait(false);
            return false;
        }

        return true;
    }
}

internal sealed class DescribeTimeToLiveRequest
{
    [JsonPropertyName("TableName")]
    public string? TableName { get; set; }
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

internal sealed class UpdateTimeToLiveRequest
{
    [JsonPropertyName("TableName")]
    public string? TableName { get; set; }

    [JsonPropertyName("TimeToLiveSpecification")]
    public TimeToLiveSpecification? TimeToLiveSpecification { get; set; }
}

internal sealed class UpdateTimeToLiveResponse
{
    [JsonPropertyName("TimeToLiveSpecification")]
    public TimeToLiveSpecification? TimeToLiveSpecification { get; set; }
}

internal sealed class TimeToLiveSpecification
{
    [JsonPropertyName("AttributeName")]
    public string? AttributeName { get; set; }

    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; }
}

[JsonSerializable(typeof(DescribeTimeToLiveRequest))]
[JsonSerializable(typeof(DescribeTimeToLiveResponse))]
[JsonSerializable(typeof(UpdateTimeToLiveRequest))]
[JsonSerializable(typeof(UpdateTimeToLiveResponse))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class TimeToLiveJsonContext : JsonSerializerContext
{
}
