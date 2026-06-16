using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Operations;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.DynamoDb.Internal;

internal static class SprocResponseParser
{
    // Reads the `success` boolean from the sproc response body. Returns false
    // (flag indeterminate) when the body is missing, not JSON, or lacks a
    // boolean `success` property — callers then treat the result as a commit.
    private static bool TryReadSuccessFlag(string? body, out bool success)
    {
        success = false;
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("success", out var s)
                && (s.ValueKind == JsonValueKind.True || s.ValueKind == JsonValueKind.False))
            {
                success = s.GetBoolean();
                return true;
            }
        }
        catch (JsonException)
        {
            // Fall through — treat as indeterminate.
        }
        return false;
    }

    public static async Task<SprocExecuteResult> ParseSingleWriteAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            // Check if body contains conditionFailed: true (sproc returns 200 for condition failures now)
            if (body.Contains("\"conditionFailed\":true") || body.Contains("\"conditionFailed\": true"))
            {
                return new SprocExecuteResult { Success = false, ConditionFailed = true, ResponseBody = body };
            }
            return new SprocExecuteResult { Success = true, ResponseBody = body };
        }

        // Legacy check for thrown condition-failed response (backwards compatibility)
        if (response.StatusCode == HttpStatusCode.BadRequest && body.Contains("ConditionalCheckFailedException"))
        {
            return new SprocExecuteResult { Success = false, ConditionFailed = true };
        }

        return new SprocExecuteResult
        {
            Success = false,
            StatusCode = (int)response.StatusCode,
            ErrorBody = body,
        };
    }

    public static async Task<SprocTransactResult> ParseTransactAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            // Condition failure is reported as a 2xx with { success: false }.
            // Parse the body rather than string-matching so whitespace / property
            // ordering from the server can't be misread as a successful commit.
            if (TryReadSuccessFlag(body, out var success) && !success)
            {
                return new SprocTransactResult { Attempted = true, ConditionFailed = true, ResponseBody = body };
            }

            return new SprocTransactResult { Attempted = true, Success = true, ResponseBody = body };
        }

        return new SprocTransactResult
        {
            Attempted = true,
            StatusCode = (int)response.StatusCode,
            ErrorBody = body,
        };
    }
}
