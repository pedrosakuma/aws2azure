using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class DeleteSecretHandler
{
    private static readonly TimeSpan PurgeRetryTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxPurgeRetryDelay = TimeSpan.FromSeconds(1);
    private static int _pendingBackgroundPurges;

    /// <summary>
    /// Count of background purge continuations currently in flight. Test-only observability
    /// hook (mirrors <c>SecretVersionCoordinator.ActiveLockCount</c>) so unit tests can
    /// deterministically await a deferred purge's background continuation instead of racing it.
    /// </summary>
    internal static int PendingBackgroundPurgeCount => Volatile.Read(ref _pendingBackgroundPurges);

    /// <summary>
    /// Test-only helper that polls <see cref="PendingBackgroundPurgeCount"/> until it drains to
    /// zero, or throws if <paramref name="timeout"/> elapses first.
    /// </summary>
    internal static async Task WaitForBackgroundPurgesAsync(TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (PendingBackgroundPurgeCount > 0)
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException("Background DeleteSecret purge continuation did not complete within the expected timeout.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    public static async Task HandleAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, ILogger? logger, CancellationToken cancellationToken)
    {
        var name = KeyVaultSecretClient.NormalizeSecretName(SecretsManagerOperationSupport.ReadString(document, "SecretId") ?? string.Empty);
        int? recoveryWindowInDays = null;
        if (document.RootElement.TryGetProperty("RecoveryWindowInDays", out var recoveryWindowProperty))
        {
            if (recoveryWindowProperty.ValueKind != JsonValueKind.Number || !recoveryWindowProperty.TryGetInt32(out var parsedRecoveryWindowInDays))
            {
                await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "InvalidParameterException",
                    "RecoveryWindowInDays must be an integer between 7 and 30.").ConfigureAwait(false);
                return;
            }

            recoveryWindowInDays = parsedRecoveryWindowInDays;
        }

        var forceDeleteWithoutRecovery = false;
        if (document.RootElement.TryGetProperty("ForceDeleteWithoutRecovery", out var forceDeleteProperty))
        {
            if (forceDeleteProperty.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "InvalidParameterException",
                    "ForceDeleteWithoutRecovery must be a boolean.").ConfigureAwait(false);
                return;
            }

            forceDeleteWithoutRecovery = forceDeleteProperty.GetBoolean();
        }
        if (recoveryWindowInDays is not null && forceDeleteWithoutRecovery)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                "InvalidParameterException",
                "RecoveryWindowInDays and ForceDeleteWithoutRecovery are mutually exclusive.").ConfigureAwait(false);
            return;
        }

        if (recoveryWindowInDays is < 7 or > 30)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                "InvalidParameterException",
                "RecoveryWindowInDays must be between 7 and 30.").ConfigureAwait(false);
            return;
        }

        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Delete, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(name)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        JsonDocument? deletedSecretDocument = null;
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
            {
                await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "InvalidRequestException",
                    "The specified secret is certificate-backed in Azure Key Vault and must be deleted via the certificate API instead of DeleteSecret.").ConfigureAwait(false);
                return;
            }

            if (!forceDeleteWithoutRecovery
                || response.StatusCode is not System.Net.HttpStatusCode.NotFound and not System.Net.HttpStatusCode.Conflict)
            {
                await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(response.StatusCode), SecretsManagerOperationSupport.MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
                return;
            }
        }
        else
        {
            deletedSecretDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        }

        if (forceDeleteWithoutRecovery)
        {
            var initialRecoveryLevel = deletedSecretDocument is null ? null : TryReadRecoveryLevel(deletedSecretDocument.RootElement);
            var allowNotFoundSuccess = !response.IsSuccessStatusCode;
            var stopwatch = Stopwatch.StartNew();

            // Real AWS Secrets Manager documents ForceDeleteWithoutRecovery as
            // asynchronous: it makes the secret immediately inaccessible and purges
            // it via a background process, without blocking DeleteSecret's response
            // on physical removal (see API_DeleteSecret.html). Key Vault's own
            // soft-delete -> purgeable transition can take several seconds, so
            // mirror that contract: attempt the purge exactly once synchronously
            // (to surface deterministic failures like missing purge permission or
            // a purge-protected vault immediately), then hand off any "still
            // converging" case to a background continuation instead of blocking
            // the caller on Key Vault's own backend latency.
            var (outcome, cachedConflictState) = await RunPurgeAttemptsAsync(
                new HttpContextPurgeOutcomeSink(context),
                client,
                token,
                name,
                initialRecoveryLevel,
                allowNotFoundSuccess,
                stopwatch,
                startAttempt: 0,
                stopAfterFirstAttempt: true,
                cachedConflictState: null,
                cancellationToken).ConfigureAwait(false);

            if (outcome == PurgeLoopOutcome.Failed)
            {
                deletedSecretDocument?.Dispose();
                return;
            }

            if (outcome == PurgeLoopOutcome.Deferred)
            {
                if (logger is not null)
                {
                    SecretsManagerLog.BackgroundPurgeDeferred(logger, name);
                }

                SchedulePurgeContinuation(client, token, name, initialRecoveryLevel, allowNotFoundSuccess, stopwatch, cachedConflictState, logger);
            }
        }

        var deletionDate = forceDeleteWithoutRecovery
            ? deletedSecretDocument is null ? null : TryReadUnixTime(deletedSecretDocument.RootElement, "deletedDate")
            : deletedSecretDocument is null ? null : TryReadUnixTime(deletedSecretDocument.RootElement, "scheduledPurgeDate")
                ?? TryReadUnixTime(deletedSecretDocument.RootElement, "deletedDate");
        var effectiveDeletionDate = deletionDate ?? DateTimeOffset.UtcNow;
        var payload = new DeleteSecretResponse(
            Arn: KeyVaultSecretClient.BuildArn(name),
            Name: name,
            DeletionDate: effectiveDeletionDate,
            DeletedDate: null,
            VersionId: null);

        await SecretsManagerOperationSupport.WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.DeleteSecretResponse, cancellationToken).ConfigureAwait(false);
        deletedSecretDocument?.Dispose();
    }

    /// <summary>
    /// Fires the remaining purge-retry attempts on a detached background task, decoupled
    /// from the (already-completed) request's <see cref="HttpContext"/> and cancellation
    /// token. This is best-effort: outcomes are logged, never surfaced to the caller, which
    /// matches real AWS Secrets Manager not reporting asynchronous purge failures either.
    /// </summary>
    private static void SchedulePurgeContinuation(
        KeyVaultSecretClient client,
        string token,
        string name,
        string? initialRecoveryLevel,
        bool allowNotFoundSuccess,
        Stopwatch stopwatch,
        DeletedSecretState? cachedConflictState,
        ILogger? logger)
    {
        // Incremented synchronously (before scheduling) so callers observing
        // PendingBackgroundPurgeCount right after HandleAsync returns see it deterministically,
        // without racing the background task's own startup.
        Interlocked.Increment(ref _pendingBackgroundPurges);
        _ = Task.Run(async () =>
        {
            try
            {
                // The synchronous attempt (attempt 0) returned "still converging" without
                // taking its backoff delay, so take it here before resuming at attempt 1.
                await Task.Delay(ComputeRetryDelay(0), CancellationToken.None).ConfigureAwait(false);
                var sink = new BackgroundPurgeOutcomeSink(logger, name);
                var (outcome, _) = await RunPurgeAttemptsAsync(
                    sink,
                    client,
                    token,
                    name,
                    initialRecoveryLevel,
                    allowNotFoundSuccess,
                    stopwatch,
                    startAttempt: 1,
                    stopAfterFirstAttempt: false,
                    cachedConflictState,
                    CancellationToken.None).ConfigureAwait(false);

                if (outcome == PurgeLoopOutcome.Purged && logger is not null)
                {
                    SecretsManagerLog.BackgroundPurgeSucceeded(logger, name);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                if (logger is not null)
                {
                    SecretsManagerLog.BackgroundPurgeUnhandledException(logger, name, ex);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _pendingBackgroundPurges);
            }
        });
    }

    private static DateTimeOffset? TryReadUnixTime(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(property.GetInt64())
            : null;

    private enum PurgeLoopOutcome
    {
        Purged,
        Failed,
        Deferred,
    }

    private static TimeSpan ComputeRetryDelay(int attempt)
        => TimeSpan.FromMilliseconds(Math.Min(50 << Math.Min(attempt, 4), (int)MaxPurgeRetryDelay.TotalMilliseconds));

    /// <summary>
    /// Runs one or more purge attempts against Key Vault's deleted-secret purge endpoint.
    /// When <paramref name="stopAfterFirstAttempt"/> is true, only a single physical attempt
    /// is made: deterministic outcomes (purged, permission denied, non-purgeable vault, other
    /// backend error) are returned as terminal, but a "still converging" result is returned as
    /// <see cref="PurgeLoopOutcome.Deferred"/> instead of looping, so the caller can resume it
    /// on a background continuation without blocking the HTTP response.
    /// </summary>
    private static async Task<(PurgeLoopOutcome Outcome, DeletedSecretState? CachedConflictState)> RunPurgeAttemptsAsync(
        PurgeOutcomeSink sink,
        KeyVaultSecretClient client,
        string token,
        string name,
        string? initialRecoveryLevel,
        bool allowNotFoundSuccess,
        Stopwatch stopwatch,
        int startAttempt,
        bool stopAfterFirstAttempt,
        DeletedSecretState? cachedConflictState,
        CancellationToken cancellationToken)
    {
        var attempt = startAttempt;
        while (true)
        {
            using var purgeRequest = new HttpRequestMessage(HttpMethod.Delete, client.BuildVaultUri(KeyVaultSecretClient.BuildDeletedSecretPath(name)));
            purgeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var purgeResponse = await client.SendAsync(purgeRequest, cancellationToken).ConfigureAwait(false);
            if (purgeResponse.IsSuccessStatusCode)
            {
                return (PurgeLoopOutcome.Purged, cachedConflictState);
            }
            if (purgeResponse.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Conflict)
            {
                if (allowNotFoundSuccess
                    && purgeResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return (PurgeLoopOutcome.Purged, cachedConflictState);
                }

                if (purgeResponse.StatusCode == System.Net.HttpStatusCode.Conflict
                    && IsKnownNonPurgeable(initialRecoveryLevel))
                {
                    await sink.WriteAwsErrorAsync(
                        StatusCodes.Status400BadRequest,
                        "InvalidRequestException",
                        "ForceDeleteWithoutRecovery could not be honored because the target Key Vault still enforces soft-delete retention (for example, purge protection is enabled).",
                        cancellationToken).ConfigureAwait(false);
                    return (PurgeLoopOutcome.Failed, cachedConflictState);
                }

                DeletedSecretState deletedSecretState;
                if (purgeResponse.StatusCode == System.Net.HttpStatusCode.Conflict
                    && cachedConflictState is { IsNonPurgeable: false, IsMissing: false, ContinueRetrying: true } cached)
                {
                    // recoveryLevel is immutable Key Vault metadata, so once a Conflict response has
                    // been classified as "still converging to purgeable", repeated Conflicts just mean
                    // the purge hasn't landed on the backend yet. Reuse that classification instead of
                    // re-fetching the same static state every backoff cycle.
                    deletedSecretState = cached;
                }
                else
                {
                    deletedSecretState = await GetDeletedSecretStateAsync(sink, client, token, name, cancellationToken).ConfigureAwait(false);
                    if (purgeResponse.StatusCode == System.Net.HttpStatusCode.Conflict)
                    {
                        cachedConflictState = deletedSecretState;
                    }
                }

                if (deletedSecretState.IsNonPurgeable)
                {
                    await sink.WriteAwsErrorAsync(
                        StatusCodes.Status400BadRequest,
                        "InvalidRequestException",
                        "ForceDeleteWithoutRecovery could not be honored because the target Key Vault still enforces soft-delete retention (for example, purge protection is enabled).",
                        cancellationToken).ConfigureAwait(false);
                    return (PurgeLoopOutcome.Failed, cachedConflictState);
                }

                if (!deletedSecretState.ContinueRetrying)
                {
                    // TreatAsSuccess is always false today (no DeletedSecretState sets it),
                    // and the only way to reach here with ContinueRetrying=false and
                    // IsNonPurgeable=false is DeletedSecretState.Fail, whose error was
                    // already written by GetDeletedSecretStateAsync.
                    return (deletedSecretState.TreatAsSuccess ? PurgeLoopOutcome.Purged : PurgeLoopOutcome.Failed, cachedConflictState);
                }

                if (purgeResponse.StatusCode == System.Net.HttpStatusCode.NotFound
                    && deletedSecretState.IsMissing)
                {
                    return (PurgeLoopOutcome.Purged, cachedConflictState);
                }

                if (stopAfterFirstAttempt)
                {
                    return (PurgeLoopOutcome.Deferred, cachedConflictState);
                }

                if (stopwatch.Elapsed >= PurgeRetryTimeout)
                {
                    await sink.WriteAwsErrorAsync(
                        StatusCodes.Status503ServiceUnavailable,
                        "InternalServiceError",
                        "Deleted Key Vault secret did not become purgeable before the bounded retry window expired.",
                        cancellationToken).ConfigureAwait(false);
                    return (PurgeLoopOutcome.Failed, cachedConflictState);
                }

                await Task.Delay(ComputeRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
                attempt++;
                continue;
            }

            if (purgeResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                await sink.WriteAwsErrorAsync(
                    StatusCodes.Status403Forbidden,
                    "AccessDeniedException",
                    "ForceDeleteWithoutRecovery requires Key Vault purge permission on the target vault.",
                    cancellationToken).ConfigureAwait(false);
                return (PurgeLoopOutcome.Failed, cachedConflictState);
            }

            await sink.WriteAwsErrorAsync(
                SecretsManagerOperationSupport.MapStatusCode(purgeResponse.StatusCode),
                SecretsManagerOperationSupport.MapErrorCode(purgeResponse.StatusCode),
                "Key Vault request failed.",
                cancellationToken).ConfigureAwait(false);
            return (PurgeLoopOutcome.Failed, cachedConflictState);
        }
    }

    private static async Task<DeletedSecretState> GetDeletedSecretStateAsync(
        PurgeOutcomeSink sink,
        KeyVaultSecretClient client,
        string token,
        string name,
        CancellationToken cancellationToken)
    {
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, client.BuildVaultUri(KeyVaultSecretClient.BuildDeletedSecretPath(name)));
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResponse = await client.SendAsync(getRequest, cancellationToken).ConfigureAwait(false);
        if (getResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return DeletedSecretState.Missing;
        }

        if (getResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return DeletedSecretState.Unknown;
        }

        if (!getResponse.IsSuccessStatusCode)
        {
            await sink.WriteAwsErrorAsync(
                SecretsManagerOperationSupport.MapStatusCode(getResponse.StatusCode),
                SecretsManagerOperationSupport.MapErrorCode(getResponse.StatusCode),
                "Key Vault request failed.",
                cancellationToken).ConfigureAwait(false);
            return DeletedSecretState.Fail;
        }

        using var deletedSecretDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(getResponse.Content, cancellationToken).ConfigureAwait(false);
        var recoveryLevel = TryReadRecoveryLevel(deletedSecretDocument.RootElement);
        if (!string.IsNullOrEmpty(recoveryLevel)
            && recoveryLevel.Contains("Purgeable", StringComparison.OrdinalIgnoreCase))
        {
            return DeletedSecretState.Purgeable;
        }

        if (!string.IsNullOrEmpty(recoveryLevel))
        {
            return DeletedSecretState.NonPurgeable;
        }

        return DeletedSecretState.Purgeable;
    }

    private static string? TryReadRecoveryLevel(JsonElement root)
    {
        if (root.TryGetProperty("recoveryLevel", out var recoveryLevelProperty)
            && recoveryLevelProperty.ValueKind == JsonValueKind.String)
        {
            return recoveryLevelProperty.GetString();
        }

        if (root.TryGetProperty("attributes", out var attributesProperty)
            && attributesProperty.ValueKind == JsonValueKind.Object
            && attributesProperty.TryGetProperty("recoveryLevel", out recoveryLevelProperty)
            && recoveryLevelProperty.ValueKind == JsonValueKind.String)
        {
            return recoveryLevelProperty.GetString();
        }

        return null;
    }

    private static bool IsKnownNonPurgeable(string? recoveryLevel)
        => !string.IsNullOrEmpty(recoveryLevel)
            && !recoveryLevel.Contains("Purgeable", StringComparison.OrdinalIgnoreCase);

    private readonly record struct DeletedSecretState(bool ContinueRetrying, bool IsNonPurgeable, bool IsMissing, bool TreatAsSuccess)
    {
        public static DeletedSecretState Purgeable => new(true, false, false, false);
        public static DeletedSecretState NonPurgeable => new(false, true, false, false);
        public static DeletedSecretState Missing => new(true, false, true, false);
        public static DeletedSecretState Unknown => new(true, false, false, false);
        public static DeletedSecretState Fail => new(false, false, false, false);
    }

    /// <summary>
    /// Abstracts "how to report a purge-attempt failure" so the shared retry logic in
    /// <see cref="RunPurgeAttemptsAsync"/> can be reused both for the single synchronous
    /// attempt (which writes an AWS error straight to the still-open response) and for the
    /// background continuation (whose HttpContext is long gone; failures are only logged).
    /// </summary>
    private abstract class PurgeOutcomeSink
    {
        public abstract Task WriteAwsErrorAsync(int statusCode, string errorCode, string message, CancellationToken cancellationToken);
    }

    private sealed class HttpContextPurgeOutcomeSink(HttpContext context) : PurgeOutcomeSink
    {
        public override Task WriteAwsErrorAsync(int statusCode, string errorCode, string message, CancellationToken cancellationToken)
            => SecretsManagerOperationSupport.WriteAwsErrorAsync(context, statusCode, errorCode, message);
    }

    private sealed class BackgroundPurgeOutcomeSink(ILogger? logger, string name) : PurgeOutcomeSink
    {
        public override Task WriteAwsErrorAsync(int statusCode, string errorCode, string message, CancellationToken cancellationToken)
        {
            if (logger is not null)
            {
                SecretsManagerLog.BackgroundPurgeFailed(logger, name, statusCode, errorCode, message);
            }

            return Task.CompletedTask;
        }
    }
}
