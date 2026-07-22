using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class SecretVersionCoordinator
{
    private const int MaxConvergenceAttempts = 8;
    private static readonly ConcurrentDictionary<string, SecretLockEntry> SecretLocks = new(StringComparer.Ordinal);

    internal static int ActiveLockCount => SecretLocks.Count;

    public static async ValueTask<IAsyncDisposable> AcquireLockAsync(string name, CancellationToken cancellationToken)
    {
        while (true)
        {
            var entry = SecretLocks.GetOrAdd(name, static _ => new SecretLockEntry());
            lock (entry.Sync)
            {
                if (entry.Retired)
                {
                    continue;
                }

                entry.ReferenceCount++;
            }

            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new SecretLockLease(name, entry);
            }
            catch
            {
                ReleaseReference(name, entry, releaseSemaphore: false);
                throw;
            }
        }
    }

    public static async Task<List<SecretVersionMetadata>?> ListVersionsAsync(
        HttpContext context,
        KeyVaultSecretClient client,
        string token,
        string name,
        CancellationToken cancellationToken)
    {
        var result = new List<SecretVersionMetadata>();
        var nextToken = string.Empty;
        do
        {
            var requestUri = client.BuildVaultUri(KeyVaultSecretClient.BuildSecretVersionsPath(name));
            if (!string.IsNullOrWhiteSpace(nextToken))
            {
                requestUri += "&$skiptoken=" + Uri.EscapeDataString(nextToken);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await WriteBackendErrorAsync(context, response.StatusCode).ConfigureAwait(false);
                return null;
            }

            using var document = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("value", out var versions) && versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var version in versions.EnumerateArray())
                {
                    result.Add(ReadMetadata(version));
                }
            }

            nextToken = document.RootElement.TryGetProperty("nextLink", out var nextLink) && nextLink.ValueKind == JsonValueKind.String
                ? KeyVaultSecretClient.ExtractSkipToken(nextLink.GetString()) ?? string.Empty
                : string.Empty;
        }
        while (!string.IsNullOrWhiteSpace(nextToken));

        return result;
    }

    public static StageResolution ResolveStage(IReadOnlyList<SecretVersionMetadata> versions, string stage)
    {
        SecretVersionMetadata? match = null;
        var explicitMatches = 0;
        foreach (var version in versions)
        {
            if (!version.HasStoredStages || version.IsPendingPublication)
            {
                continue;
            }

            if (ContainsStage(version.VersionStages, stage))
            {
                explicitMatches++;
                if (match is null || CompareNewest(version, match) < 0)
                {
                    match = version;
                }
            }
        }

        if (explicitMatches > 1)
        {
            return new StageResolution(null, true);
        }

        if (match is not null)
        {
            return new StageResolution(match, false);
        }

        if (!string.Equals(stage, "AWSCURRENT", StringComparison.Ordinal))
        {
            return new StageResolution(null, false);
        }

        foreach (var version in versions)
        {
            if (!version.HasStoredStages
                && !version.IsPendingPublication
                && (match is null || CompareNewest(version, match) < 0))
            {
                match = version;
            }
        }

        return new StageResolution(match, false);
    }

    public static TokenResolution ResolveToken(
        IReadOnlyList<SecretVersionMetadata> versions,
        string clientRequestToken,
        string? expectedPayloadSha256)
    {
        SecretVersionMetadata? physicalCollision = null;
        SecretVersionMetadata? winner = null;
        string? observedPayloadSha256 = null;
        var candidateCount = 0;
        var missingPayloadSha256 = false;
        foreach (var version in versions)
        {
            if (string.Equals(version.VersionId, clientRequestToken, StringComparison.Ordinal))
            {
                physicalCollision = version;
            }

            if (!version.Tags.TryGetValue(KeyVaultSecretClient.ClientRequestTokenTag, out var candidateToken)
                || !string.Equals(candidateToken, clientRequestToken, StringComparison.Ordinal))
            {
                continue;
            }

            candidateCount++;
            if (!version.Tags.TryGetValue(KeyVaultSecretClient.PayloadSha256Tag, out var candidatePayloadSha256))
            {
                missingPayloadSha256 = true;
                if (expectedPayloadSha256 is not null)
                {
                    return new TokenResolution(null, true);
                }
            }
            else if ((observedPayloadSha256 is not null
                    && !string.Equals(observedPayloadSha256, candidatePayloadSha256, StringComparison.Ordinal))
                || (expectedPayloadSha256 is not null
                    && !string.Equals(expectedPayloadSha256, candidatePayloadSha256, StringComparison.Ordinal)))
            {
                return new TokenResolution(null, true);
            }

            observedPayloadSha256 = candidatePayloadSha256;
            if (winner is null || CompareOldest(version, winner) < 0)
            {
                winner = version;
            }
        }

        if (candidateCount > 1 && missingPayloadSha256)
        {
            return new TokenResolution(null, true);
        }

        if (physicalCollision is not null)
        {
            if (physicalCollision.Tags.TryGetValue(
                    KeyVaultSecretClient.ClientRequestTokenTag,
                    out var physicalToken)
                && !string.Equals(
                    physicalToken,
                    clientRequestToken,
                    StringComparison.Ordinal))
            {
                return new TokenResolution(null, true);
            }

            if (winner is not null
                && !string.Equals(winner.VersionId, physicalCollision.VersionId, StringComparison.Ordinal))
            {
                return new TokenResolution(null, true);
            }

            if (!physicalCollision.Tags.TryGetValue(
                    KeyVaultSecretClient.PayloadSha256Tag,
                    out var physicalPayloadSha256)
                || (expectedPayloadSha256 is not null
                    && !string.Equals(
                        expectedPayloadSha256,
                        physicalPayloadSha256,
                        StringComparison.Ordinal)))
            {
                return new TokenResolution(null, true);
            }

            winner = physicalCollision;
        }

        return new TokenResolution(winner, false);
    }

    public static async Task<PublishedVersionResult?> PublishVersionAsync(
        HttpContext context,
        KeyVaultSecretClient client,
        string token,
        string name,
        string createdVersionId,
        string? clientRequestToken,
        string payloadSha256,
        IReadOnlyList<string> requestedStages,
        bool defaultStageTransition,
        CancellationToken cancellationToken)
    {
        HttpStatusCode? lastFailure = null;
        string? reconciliationWinnerId = null;
        IReadOnlyList<string>? reconciliationStages = null;
        var reconciliationDefaultTransition = false;
        for (var attempt = 0; attempt < MaxConvergenceAttempts; attempt++)
        {
            var versions = await ListVersionsAsync(context, client, token, name, cancellationToken).ConfigureAwait(false);
            if (versions is null)
            {
                return null;
            }

            SecretVersionMetadata? winner;
            if (!string.IsNullOrWhiteSpace(clientRequestToken))
            {
                var tokenResolution = ResolveToken(versions, clientRequestToken, payloadSha256);
                if (tokenResolution.Conflict)
                {
                    await WriteConflictAsync(context, "ClientRequestToken is already associated with a different secret value.").ConfigureAwait(false);
                    return null;
                }

                winner = tokenResolution.Version;
            }
            else
            {
                winner = FindByVersionId(versions, createdVersionId);
            }

            if (winner is null)
            {
                await DelayForRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var pendingPublication = winner.IsPendingPublication;
            if (!pendingPublication)
            {
                if (reconciliationWinnerId is null)
                {
                    return new PublishedVersionResult(winner.VersionId, winner.VersionStages);
                }

                if (!string.Equals(
                        reconciliationWinnerId,
                        winner.VersionId,
                        StringComparison.Ordinal))
                {
                    await DelayForRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            var effectiveStages = reconciliationStages
                ?? (winner.IntendedVersionStages.Length > 0
                    ? winner.IntendedVersionStages
                    : requestedStages);
            var effectiveDefaultTransition = reconciliationStages is not null
                ? reconciliationDefaultTransition
                : winner.HasDefaultStageTransition
                    ? winner.DefaultStageTransition
                    : defaultStageTransition;
            reconciliationWinnerId ??= winner.VersionId;
            reconciliationStages ??= effectiveStages;
            reconciliationDefaultTransition = effectiveDefaultTransition;
            if (effectiveStages.Count == 0)
            {
                await WriteConflictAsync(context, "The deterministic winner has no observable or intended staging labels.").ConfigureAwait(false);
                return null;
            }

            var predecessor = effectiveDefaultTransition
                ? FindPredecessor(versions, winner.VersionId)
                : null;

            var losersUpdated = true;
            foreach (var version in versions)
            {
                if (string.Equals(version.VersionId, winner.VersionId, StringComparison.Ordinal))
                {
                    continue;
                }

                var desiredStages = new List<string>(version.VersionStages);
                RemoveAny(desiredStages, effectiveStages);
                if (effectiveDefaultTransition)
                {
                    if (string.Equals(version.VersionId, predecessor?.VersionId, StringComparison.Ordinal))
                    {
                        AddIfMissing(desiredStages, "AWSPREVIOUS");
                    }
                    else
                    {
                        RemoveStage(desiredStages, "AWSPREVIOUS");
                    }
                }

                var update = await UpdateStagesFreshAsync(
                    context,
                    client,
                    token,
                    name,
                    version.VersionId,
                    desiredStages,
                    finalizePublication: false,
                    cancellationToken).ConfigureAwait(false);
                if (update.Fatal)
                {
                    return null;
                }

                if (!update.Success)
                {
                    lastFailure = update.StatusCode;
                    losersUpdated = false;
                }
            }

            if (losersUpdated)
            {
                var publication = await UpdateStagesFreshAsync(
                    context,
                    client,
                    token,
                    name,
                    winner.VersionId,
                    effectiveStages,
                    finalizePublication: pendingPublication,
                    cancellationToken).ConfigureAwait(false);
                if (publication.Fatal)
                {
                    return null;
                }

                if (!publication.Success)
                {
                    lastFailure = publication.StatusCode;
                }
            }

            var verified = await ListVersionsAsync(context, client, token, name, cancellationToken).ConfigureAwait(false);
            if (verified is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(clientRequestToken))
            {
                var verifiedToken = ResolveToken(verified, clientRequestToken, payloadSha256);
                if (verifiedToken.Conflict)
                {
                    await WriteConflictAsync(context, "ClientRequestToken is already associated with a different secret value.").ConfigureAwait(false);
                    return null;
                }

                if (verifiedToken.Version is null
                    || !string.Equals(verifiedToken.Version.VersionId, winner.VersionId, StringComparison.Ordinal))
                {
                    await DelayForRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            if (VerifyLabels(verified, winner.VersionId, effectiveStages, effectiveDefaultTransition, predecessor?.VersionId))
            {
                return new PublishedVersionResult(winner.VersionId, effectiveStages);
            }

            await DelayForRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
        }

        if (lastFailure is not null)
        {
            if (lastFailure is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            {
                await WriteConflictAsync(
                    context,
                    "Unable to observe a unique staging-label assignment after bounded reconciliation.")
                    .ConfigureAwait(false);
                return null;
            }

            await WriteBackendErrorAsync(context, lastFailure.Value).ConfigureAwait(false);
            return null;
        }

        await WriteConflictAsync(
            context,
            "Unable to observe a unique staging-label assignment after bounded reconciliation.")
            .ConfigureAwait(false);
        return null;
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildVersionStageMap(
        IReadOnlyList<SecretVersionMetadata> versions,
        out bool tokenConflict)
    {
        tokenConflict = false;
        var tokenWinners = new Dictionary<string, SecretVersionMetadata>(StringComparer.Ordinal);
        foreach (var version in versions)
        {
            if (!version.Tags.TryGetValue(KeyVaultSecretClient.ClientRequestTokenTag, out var clientRequestToken))
            {
                continue;
            }

            var resolution = ResolveToken(versions, clientRequestToken, expectedPayloadSha256: null);
            if (resolution.Conflict)
            {
                tokenConflict = true;
                return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            }

            if (resolution.Version is not null)
            {
                tokenWinners[clientRequestToken] = resolution.Version;
            }
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var currentFallback = ResolveStage(versions, "AWSCURRENT").Version;
        foreach (var version in versions)
        {
            if (version.IsPendingPublication)
            {
                continue;
            }

            string responseVersionId;
            if (version.Tags.TryGetValue(KeyVaultSecretClient.ClientRequestTokenTag, out var clientRequestToken))
            {
                if (!tokenWinners.TryGetValue(clientRequestToken, out var winner)
                    || !string.Equals(winner.VersionId, version.VersionId, StringComparison.Ordinal))
                {
                    continue;
                }

                responseVersionId = clientRequestToken;
            }
            else
            {
                responseVersionId = version.VersionId;
            }

            var stages = version.HasStoredStages
                ? version.VersionStages
                : string.Equals(currentFallback?.VersionId, version.VersionId, StringComparison.Ordinal)
                    ? ["AWSCURRENT"]
                    : [];
            if (stages.Length > 0)
            {
                result[responseVersionId] = stages;
            }
        }

        return result;
    }

    private static async Task<StageUpdateResult> UpdateStagesFreshAsync(
        HttpContext context,
        KeyVaultSecretClient client,
        string token,
        string name,
        string versionId,
        IReadOnlyList<string> stages,
        bool finalizePublication,
        CancellationToken cancellationToken)
    {
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretVersionPath(name, versionId)));
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResponse = await client.SendAsync(getRequest, cancellationToken).ConfigureAwait(false);
        if (!getResponse.IsSuccessStatusCode)
        {
            return await ClassifyUpdateFailureAsync(context, getResponse.StatusCode).ConfigureAwait(false);
        }

        using var document = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(getResponse.Content, cancellationToken).ConfigureAwait(false);
        var freshTags = KeyVaultSecretClient.GetRawTags(document.RootElement);
        var currentStages = KeyVaultSecretClient.TryGetRawTag(document.RootElement, KeyVaultSecretClient.VersionStagesTag, out var encodedStages)
            ? KeyVaultSecretClient.DecodeStoredVersionStages(encodedStages)
            : ["AWSCURRENT"];
        var alreadyPublished = KeyVaultSecretClient.TryGetRawTag(
                document.RootElement,
                KeyVaultSecretClient.PublicationStateTag,
                out var publicationState)
            && string.Equals(publicationState, "published", StringComparison.Ordinal);
        if (StagesEqual(currentStages, stages)
            && (!finalizePublication || alreadyPublished))
        {
            return StageUpdateResult.Succeeded;
        }

        using var patchRequest = new HttpRequestMessage(HttpMethod.Patch, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretVersionPath(name, versionId)));
        patchRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var updatedTags = finalizePublication
            ? KeyVaultSecretClient.WithPublishedVersionStages(freshTags, stages)
            : KeyVaultSecretClient.WithVersionStages(freshTags, stages);
        patchRequest.Content = new StringContent(
            KeyVaultSecretClient.BuildTagsJsonBody(updatedTags),
            Encoding.UTF8,
            "application/json");
        using var patchResponse = await client.SendAsync(patchRequest, cancellationToken).ConfigureAwait(false);
        return patchResponse.IsSuccessStatusCode
            ? StageUpdateResult.Succeeded
            : await ClassifyUpdateFailureAsync(context, patchResponse.StatusCode).ConfigureAwait(false);
    }

    private static async Task<StageUpdateResult> ClassifyUpdateFailureAsync(HttpContext context, HttpStatusCode statusCode)
    {
        if (statusCode is HttpStatusCode.NotFound
            or HttpStatusCode.Conflict
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            || statusCode >= HttpStatusCode.InternalServerError)
        {
            return new StageUpdateResult(false, false, statusCode);
        }

        await WriteBackendErrorAsync(context, statusCode).ConfigureAwait(false);
        return new StageUpdateResult(false, true, statusCode);
    }

    internal static SecretVersionMetadata ReadMetadata(JsonElement version)
    {
        var id = version.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? string.Empty
            : string.Empty;
        return new SecretVersionMetadata(
            KeyVaultSecretClient.GetVersionId(id),
            KeyVaultSecretClient.GetRawTags(version),
            KeyVaultSecretClient.GetCreatedDate(version).ToUnixTimeSeconds(),
            KeyVaultSecretClient.TryGetRawTag(version, KeyVaultSecretClient.VersionStagesTag, out _));
    }

    private static SecretVersionMetadata? FindByVersionId(IReadOnlyList<SecretVersionMetadata> versions, string versionId)
    {
        foreach (var version in versions)
        {
            if (string.Equals(version.VersionId, versionId, StringComparison.Ordinal))
            {
                return version;
            }
        }

        return null;
    }

    private static SecretVersionMetadata? FindPredecessor(IReadOnlyList<SecretVersionMetadata> versions, string winnerVersionId)
    {
        SecretVersionMetadata? current = null;
        SecretVersionMetadata? previous = null;
        foreach (var version in versions)
        {
            if (string.Equals(version.VersionId, winnerVersionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (ContainsStage(version.VersionStages, "AWSCURRENT"))
            {
                if (current is null || CompareNewest(version, current) < 0)
                {
                    current = version;
                }
            }
            else if (ContainsStage(version.VersionStages, "AWSPREVIOUS")
                     && (previous is null || CompareNewest(version, previous) < 0))
            {
                previous = version;
            }
        }

        return current ?? previous;
    }

    private static bool VerifyLabels(
        IReadOnlyList<SecretVersionMetadata> versions,
        string winnerVersionId,
        IReadOnlyList<string> stages,
        bool defaultStageTransition,
        string? predecessorVersionId)
    {
        foreach (var stage in stages)
        {
            var holders = 0;
            var winnerHolds = false;
            foreach (var version in versions)
            {
                if (ContainsStage(version.VersionStages, stage))
                {
                    holders++;
                    winnerHolds |= string.Equals(version.VersionId, winnerVersionId, StringComparison.Ordinal);
                }
            }

            if (holders != 1 || !winnerHolds)
            {
                return false;
            }
        }

        if (!defaultStageTransition)
        {
            return true;
        }

        var previousHolders = 0;
        var predecessorHolds = false;
        foreach (var version in versions)
        {
            if (ContainsStage(version.VersionStages, "AWSPREVIOUS"))
            {
                previousHolders++;
                predecessorHolds |= string.Equals(version.VersionId, predecessorVersionId, StringComparison.Ordinal);
            }
        }

        return predecessorVersionId is null
            ? previousHolders == 0
            : previousHolders == 1 && predecessorHolds;
    }

    private static int CompareOldest(SecretVersionMetadata left, SecretVersionMetadata right)
    {
        var created = left.Created.CompareTo(right.Created);
        return created != 0 ? created : StringComparer.Ordinal.Compare(left.VersionId, right.VersionId);
    }

    private static int CompareNewest(SecretVersionMetadata left, SecretVersionMetadata right)
    {
        var created = right.Created.CompareTo(left.Created);
        return created != 0 ? created : StringComparer.Ordinal.Compare(right.VersionId, left.VersionId);
    }

    private static bool ContainsStage(IReadOnlyList<string> stages, string stage)
    {
        foreach (var candidate in stages)
        {
            if (string.Equals(candidate, stage, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void RemoveAny(List<string> stages, IReadOnlyList<string> labels)
    {
        for (var i = stages.Count - 1; i >= 0; i--)
        {
            if (ContainsStage(labels, stages[i]))
            {
                stages.RemoveAt(i);
            }
        }
    }

    private static void RemoveStage(List<string> stages, string stage)
    {
        for (var i = stages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(stages[i], stage, StringComparison.Ordinal))
            {
                stages.RemoveAt(i);
            }
        }
    }

    private static void AddIfMissing(List<string> stages, string stage)
    {
        if (!ContainsStage(stages, stage))
        {
            stages.Add(stage);
        }
    }

    private static bool StagesEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task DelayForRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        if (attempt >= MaxConvergenceAttempts - 1)
        {
            return;
        }

        var milliseconds = Math.Min(50 << Math.Min(attempt, 4), 1_000);
        await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteBackendErrorAsync(HttpContext context, HttpStatusCode statusCode)
        => SecretsManagerOperationSupport.WriteAwsErrorAsync(
            context,
            SecretsManagerOperationSupport.MapStatusCode(statusCode),
            SecretsManagerOperationSupport.MapErrorCode(statusCode),
            "Key Vault request failed.");

    internal static Task WriteConflictAsync(HttpContext context, string message)
        => SecretsManagerOperationSupport.WriteAwsErrorAsync(
            context,
            StatusCodes.Status400BadRequest,
            "ResourceExistsException",
            message);

    private static void ReleaseReference(string name, SecretLockEntry entry, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            entry.Semaphore.Release();
        }

        lock (entry.Sync)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0
                && SecretLocks.TryRemove(new KeyValuePair<string, SecretLockEntry>(name, entry)))
            {
                entry.Retired = true;
            }
        }
    }

    internal sealed record SecretVersionMetadata(
        string VersionId,
        IReadOnlyDictionary<string, string> Tags,
        long Created,
        bool HasStoredStages)
    {
        public string[] VersionStages => Tags.TryGetValue(KeyVaultSecretClient.VersionStagesTag, out var stages)
            ? KeyVaultSecretClient.DecodeStoredVersionStages(stages)
            : HasStoredStages ? [] : ["AWSCURRENT"];

        public string[] IntendedVersionStages => Tags.TryGetValue(KeyVaultSecretClient.IntendedVersionStagesTag, out var stages)
            ? KeyVaultSecretClient.DecodeStoredVersionStages(stages)
            : [];

        public bool HasDefaultStageTransition => Tags.ContainsKey(KeyVaultSecretClient.DefaultStageTransitionTag);

        public bool DefaultStageTransition => Tags.TryGetValue(KeyVaultSecretClient.DefaultStageTransitionTag, out var value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

        public bool IsPendingPublication =>
            Tags.TryGetValue(KeyVaultSecretClient.PublicationStateTag, out var state)
                ? string.Equals(state, "pending", StringComparison.Ordinal)
                : Tags.ContainsKey(KeyVaultSecretClient.IntendedVersionStagesTag);
    }

    internal readonly record struct StageResolution(SecretVersionMetadata? Version, bool Conflict);
    internal readonly record struct TokenResolution(SecretVersionMetadata? Version, bool Conflict);
    internal readonly record struct PublishedVersionResult(string VersionId, IReadOnlyList<string> VersionStages);
    private readonly record struct StageUpdateResult(bool Success, bool Fatal, HttpStatusCode? StatusCode)
    {
        public static StageUpdateResult Succeeded => new(true, false, null);
    }

    private sealed class SecretLockEntry
    {
        public object Sync { get; } = new();
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class SecretLockLease(string name, SecretLockEntry entry) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ReleaseReference(name, entry, releaseSemaphore: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
