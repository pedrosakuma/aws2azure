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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.DynamoDb.Internal;

internal static class SprocResponseParser
{
    private static bool TryReadObject(string? body, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }
        try
        {
            document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return true;
            }
            document.Dispose();
            document = null;
        }
        catch (JsonException)
        {
        }
        return false;
    }

    private static bool TryReadSuccess(JsonElement root, out bool success)
    {
        success = false;
        if (!root.TryGetProperty("success", out var value)
            || (value.ValueKind != JsonValueKind.True
                && value.ValueKind != JsonValueKind.False))
        {
            return false;
        }

        success = value.GetBoolean();
        return true;
    }

    public static async Task<SprocExecuteResult> ParseSingleWriteAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            if (TryReadObject(body, out var document))
            {
                using var parsed = document!;
                {
                    var root = parsed.RootElement;
                    if (TryReadSuccess(root, out var success))
                    {
                        if (success)
                        {
                            if (!root.TryGetProperty("conditionFailed", out _))
                            {
                                return new SprocExecuteResult
                                {
                                    Success = true,
                                    ResponseBody = body,
                                };
                            }
                        }
                        if (!success
                            && root.TryGetProperty("conditionFailed", out var conditionFailed)
                            && conditionFailed.ValueKind == JsonValueKind.True)
                        {
                            return new SprocExecuteResult
                            {
                                ConditionFailed = true,
                                ResponseBody = body,
                            };
                        }
                    }
                }
            }

            return new SprocExecuteResult
            {
                StatusCode = StatusCodes.Status502BadGateway,
                ErrorBody = "Stored procedure returned a malformed success response.",
                ResponseBody = body,
            };
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
            if (TryReadObject(body, out var document))
            {
                using var parsed = document!;
                {
                    var root = parsed.RootElement;
                    if (TryReadSuccess(root, out var success))
                    {
                        if (success)
                        {
                            if (!root.TryGetProperty("reasons", out _))
                            {
                                return new SprocTransactResult
                                {
                                    Attempted = true,
                                    Success = true,
                                    ResponseBody = body,
                                };
                            }
                        }
                        if (!success
                            && root.TryGetProperty("validationError", out _))
                        {
                            if (TryReadValidationError(
                                    root,
                                    out var validationError))
                            {
                                return new SprocTransactResult
                                {
                                    Attempted = true,
                                    ValidationFailed = true,
                                    ValidationError = validationError,
                                    ResponseBody = body,
                                };
                            }
                            return new SprocTransactResult
                            {
                                Attempted = true,
                                StatusCode = StatusCodes.Status502BadGateway,
                                ErrorBody =
                                    "Transaction stored procedure returned a malformed validation response.",
                                ResponseBody = body,
                            };
                        }
                        if (!success
                            && root.TryGetProperty("reasons", out var reasons)
                            && reasons.ValueKind == JsonValueKind.Array)
                        {
                            return new SprocTransactResult
                            {
                                Attempted = true,
                                ConditionFailed = true,
                                ResponseBody = body,
                            };
                        }
                    }
                }
            }

            return new SprocTransactResult
            {
                Attempted = true,
                StatusCode = StatusCodes.Status502BadGateway,
                ErrorBody = "Transaction stored procedure returned a malformed success response.",
                ResponseBody = body,
            };
        }

        return new SprocTransactResult
        {
            Attempted = true,
            StatusCode = (int)response.StatusCode,
            ErrorBody = body,
        };
    }

    private static bool TryReadValidationError(
        JsonElement root,
        out string? message)
    {
        message = null;
        if (!root.TryGetProperty(
                "validationError",
                out var validationError)
            || validationError.ValueKind != JsonValueKind.Object
            || !validationError.TryGetProperty("code", out var code)
            || code.ValueKind != JsonValueKind.String
            || !string.Equals(
                code.GetString(),
                "ValidationException",
                StringComparison.Ordinal)
            || !validationError.TryGetProperty("message", out var messageElement)
            || messageElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(messageElement.GetString())
            || root.TryGetProperty("reasons", out _))
        {
            return false;
        }

        message = messageElement.GetString();
        return true;
    }

    public static async Task<SprocTransactGetResult> ParseTransactGetAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            if (TryReadObject(body, out var document))
            {
                using var parsed = document!;
                {
                    var root = parsed.RootElement;
                    if (TryReadSuccess(root, out var success)
                        && success
                        && root.TryGetProperty("items", out var items)
                        && items.ValueKind == JsonValueKind.Array)
                    {
                        return new SprocTransactGetResult
                        {
                            Attempted = true,
                            Success = true,
                            ResponseBody = body,
                        };
                    }
                }
            }

            return new SprocTransactGetResult
            {
                Attempted = true,
                StatusCode = StatusCodes.Status502BadGateway,
                ErrorBody = "Snapshot stored procedure returned a malformed success response.",
                ResponseBody = body,
            };
        }

        return new SprocTransactGetResult
        {
            Attempted = true,
            StatusCode = (int)response.StatusCode,
            ErrorBody = body,
        };
    }
}
