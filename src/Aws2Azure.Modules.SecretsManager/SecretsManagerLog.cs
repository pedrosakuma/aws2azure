using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.SecretsManager;

/// <summary>
/// Source-generated diagnostics. Authentication events contain only allowlisted
/// operation names, bounded correlation identifiers, and status codes.
/// </summary>
internal static partial class SecretsManagerLog
{
    internal const string Unavailable = "unavailable";

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "Secrets Manager Entra token acquisition failed. Operation={Operation} RequestId={RequestId} TokenStatus={TokenStatus} UpstreamRequestId=unavailable")]
    public static partial void TokenAcquisitionFailed(ILogger logger, string operation, string requestId, int tokenStatus);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message = "Secrets Manager Key Vault authorization failed. Operation={Operation} RequestId={RequestId} UpstreamStatus={UpstreamStatus} UpstreamRequestId={UpstreamRequestId}")]
    public static partial void KeyVaultAuthorizationFailed(ILogger logger, string operation, string requestId, int upstreamStatus, string upstreamRequestId);

    internal static string SafeUpstreamRequestId(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("x-ms-request-id", out var values))
        {
            return Unavailable;
        }

        using var enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return Unavailable;
        }

        var value = enumerator.Current;
        return !enumerator.MoveNext() && IsGuid(value) ? value : Unavailable;
    }

    internal static string SafeRequestId(string value)
    {
        if (IsGuid(value))
        {
            return value;
        }

        // Kestrel's connection-id (base32) + request sequence (hex).
        if (value.Length != 22 || value[13] != ':')
        {
            return Unavailable;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (i == 13)
            {
                continue;
            }

            var c = value[i];
            if (!(c is >= '0' and <= '9' || c >= 'A' && c <= (i < 13 ? 'V' : 'F')))
            {
                return Unavailable;
            }
        }

        return value;
    }

    private static bool IsGuid(string value)
        => value.Length == 36 && Guid.TryParseExact(value, "D", out _)
            || value.Length == 32 && Guid.TryParseExact(value, "N", out _);

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Key Vault secret '{Name}' purge deferred to background after DeleteSecret responded; still converging to purgeable.")]
    public static partial void BackgroundPurgeDeferred(ILogger logger, string name);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Key Vault secret '{Name}' background purge completed successfully.")]
    public static partial void BackgroundPurgeSucceeded(ILogger logger, string name);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Key Vault secret '{Name}' background purge did not complete: HTTP {StatusCode} {ErrorCode} {Message}")]
    public static partial void BackgroundPurgeFailed(ILogger logger, string name, int statusCode, string errorCode, string message);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Key Vault secret '{Name}' background purge threw an unhandled exception.")]
    public static partial void BackgroundPurgeUnhandledException(ILogger logger, string name, Exception exception);
}
