using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Errors;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static partial class CosmosOpsShared
{
    /// <summary>
    /// True when a Cosmos 404 response carries the <c>x-ms-substatus</c>
    /// header indicating the container itself is missing (sub-status 1003)
    /// rather than just the requested document. Lets item handlers tell
    /// "item not found" (DynamoDB-success) apart from "table deleted
    /// between metadata read and op" (ResourceNotFoundException).
    /// </summary>
    public static bool Is404ContainerMissing(HttpResponseMessage resp)
    {
        if (resp.StatusCode != System.Net.HttpStatusCode.NotFound) return false;
        if (!resp.Headers.TryGetValues("x-ms-substatus", out var values)) return false;
        foreach (var v in values)
        {
            if (string.Equals(v?.Trim(), "1003", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static async Task WriteCosmosErrorAsync(
        HttpContext ctx, HttpResponseMessage cosmosResp, CancellationToken ct)
    {
        var status = (int)cosmosResp.StatusCode;
        var (awsStatus, code) = status switch
        {
            401 or 403 => (400, "AccessDeniedException"),
            408 => (500, "InternalServerError"),
            429 => (400, "ProvisionedThroughputExceededException"),
            503 => (500, "InternalServerError"),
            _ when status >= 500 => (500, "InternalServerError"),
            _ => (400, "ValidationException"),
        };
        string body = string.Empty;
        try { body = await cosmosResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { }
        var message = string.IsNullOrEmpty(body) ? cosmosResp.ReasonPhrase ?? "Cosmos request failed." : body;
        await WriteErrorAsync(ctx, awsStatus, code, message).ConfigureAwait(false);
    }

    public static Task WriteErrorAsync(HttpContext ctx, int status, string code, string message)
        => DynamoDbErrorResponse.WriteAsync(ctx, status, code, message);
}
