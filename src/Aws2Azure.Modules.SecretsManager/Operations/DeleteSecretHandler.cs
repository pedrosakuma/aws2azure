using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class DeleteSecretHandler
{
    private static readonly TimeSpan PurgeRetryTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxPurgeRetryDelay = TimeSpan.FromSeconds(1);

    public static async Task HandleAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
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
            var purgeResult = await PurgeDeletedSecretAsync(
                context,
                client,
                token,
                name,
                initialRecoveryLevel: deletedSecretDocument is null ? null : TryReadRecoveryLevel(deletedSecretDocument.RootElement),
                allowNotFoundSuccess: !response.IsSuccessStatusCode,
                cancellationToken).ConfigureAwait(false);
            if (!purgeResult)
            {
                deletedSecretDocument?.Dispose();
                return;
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

    private static DateTimeOffset? TryReadUnixTime(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(property.GetInt64())
            : null;

    private static async Task<bool> PurgeDeletedSecretAsync(
        HttpContext context,
        KeyVaultSecretClient client,
        string token,
        string name,
        string? initialRecoveryLevel,
        bool allowNotFoundSuccess,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            using var purgeRequest = new HttpRequestMessage(HttpMethod.Delete, client.BuildVaultUri(KeyVaultSecretClient.BuildDeletedSecretPath(name)));
            purgeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var purgeResponse = await client.SendAsync(purgeRequest, cancellationToken).ConfigureAwait(false);
            if (purgeResponse.IsSuccessStatusCode)
            {
                return true;
            }
            if (purgeResponse.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Conflict)
            {
                if (allowNotFoundSuccess
                    && purgeResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return true;
                }

                if (purgeResponse.StatusCode == System.Net.HttpStatusCode.Conflict
                    && IsKnownNonPurgeable(initialRecoveryLevel))
                {
                    await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "InvalidRequestException",
                        "ForceDeleteWithoutRecovery could not be honored because the target Key Vault still enforces soft-delete retention (for example, purge protection is enabled).").ConfigureAwait(false);
                    return false;
                }

                var deletedSecretState = await GetDeletedSecretStateAsync(context, client, token, name, cancellationToken).ConfigureAwait(false);
                if (deletedSecretState.IsNonPurgeable)
                {
                    await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "InvalidRequestException",
                        "ForceDeleteWithoutRecovery could not be honored because the target Key Vault still enforces soft-delete retention (for example, purge protection is enabled).").ConfigureAwait(false);
                    return false;
                }

                if (!deletedSecretState.ContinueRetrying)
                {
                    return deletedSecretState.TreatAsSuccess;
                }

                if (purgeResponse.StatusCode == System.Net.HttpStatusCode.NotFound
                    && deletedSecretState.IsMissing)
                {
                    return true;
                }

                if (stopwatch.Elapsed >= PurgeRetryTimeout)
                {
                    await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                        context,
                        StatusCodes.Status503ServiceUnavailable,
                        "InternalServiceError",
                        "Deleted Key Vault secret did not become purgeable before the bounded retry window expired.").ConfigureAwait(false);
                    return false;
                }

                var delay = TimeSpan.FromMilliseconds(Math.Min(50 << Math.Min(attempt, 4), (int)MaxPurgeRetryDelay.TotalMilliseconds));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                attempt++;
                continue;
            }

            if (purgeResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "AccessDeniedException",
                    "ForceDeleteWithoutRecovery requires Key Vault purge permission on the target vault.").ConfigureAwait(false);
                return false;
            }

            await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                context,
                SecretsManagerOperationSupport.MapStatusCode(purgeResponse.StatusCode),
                SecretsManagerOperationSupport.MapErrorCode(purgeResponse.StatusCode),
                "Key Vault request failed.").ConfigureAwait(false);
            return false;
        }
    }

    private static async Task<DeletedSecretState> GetDeletedSecretStateAsync(
        HttpContext context,
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
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                context,
                SecretsManagerOperationSupport.MapStatusCode(getResponse.StatusCode),
                SecretsManagerOperationSupport.MapErrorCode(getResponse.StatusCode),
                "Key Vault request failed.").ConfigureAwait(false);
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
}
