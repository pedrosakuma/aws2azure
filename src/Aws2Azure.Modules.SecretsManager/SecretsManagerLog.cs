using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.SecretsManager;

/// <summary>
/// LoggerMessage source-generated events for work that continues after a Secrets
/// Manager response has already been sent to the caller (see
/// <c>DeleteSecretHandler</c>'s background purge continuation). These are
/// best-effort observability only: real AWS Secrets Manager does not report
/// asynchronous purge outcomes back to the DeleteSecret caller either, so
/// failures here are logged, not surfaced over the wire.
/// </summary>
internal static partial class SecretsManagerLog
{
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
