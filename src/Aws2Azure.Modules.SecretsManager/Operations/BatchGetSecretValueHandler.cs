using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class BatchGetSecretValueHandler
{
    internal const int MaxSecretIds = 20;
    internal const int MaxConcurrentSecrets = 4;
    private const int MaxSecretIdLength = 2048;

    public static async Task HandleAsync(
        HttpContext context,
        KeyVaultSecretClient client,
        JsonDocument document,
        CancellationToken cancellationToken)
    {
        var secretIds = ReadSecretIds(document, out var filtersRequested);
        if (filtersRequested)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                context,
                StatusCodes.Status501NotImplemented,
                "NotImplementedException",
                "BatchGetSecretValue Filters pagination is not implemented. Supply SecretIdList with 1 to 20 secret identifiers.").ConfigureAwait(false);
            return;
        }

        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var results = new GetSecretValueHandler.GetSecretValueResult[secretIds.Length];
        var nextIndex = -1;
        GetSecretValueHandler.GetSecretValueError? requestError = null;
        ExceptionDispatchInfo? requestException = null;

        using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workerCount = Math.Min(MaxConcurrentSecrets, secretIds.Length);
        var workers = new Task[workerCount];
        for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            workers[workerIndex] = RunWorkerAsync();
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        requestException?.Throw();

        if (requestError is not null)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                context,
                requestError.StatusCode,
                requestError.Code,
                requestError.Message).ConfigureAwait(false);
            return;
        }

        var values = new List<GetSecretValueResponse>(secretIds.Length);
        var errors = new List<BatchGetSecretValueError>();
        for (var index = 0; index < results.Length; index++)
        {
            var result = results[index];
            if (result.Value is { } value)
            {
                values.Add(value);
            }
            else
            {
                var error = result.Error!;
                errors.Add(new BatchGetSecretValueError(error.Code, error.Message, secretIds[index]));
            }
        }

        var response = new BatchGetSecretValueResponse(errors, values);
        await SecretsManagerOperationSupport.WriteJsonAsync(
            context,
            response,
            SecretsManagerJsonContext.Default.BatchGetSecretValueResponse,
            cancellationToken).ConfigureAwait(false);

        async Task RunWorkerAsync()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= secretIds.Length)
                {
                    return;
                }

                try
                {
                    var result = await GetSecretValueHandler.RetrieveAsync(
                        client,
                        token,
                        secretIds[index],
                        requestedVersionId: null,
                        requestedVersionStage: null,
                        batchCts.Token).ConfigureAwait(false);
                    results[index] = result;
                    if (result.Error is not { RequestLevel: true } error)
                    {
                        continue;
                    }

                    if (Interlocked.CompareExchange(ref requestError, error, null) is null)
                    {
                        batchCts.Cancel();
                    }

                    return;
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested
                    && batchCts.IsCancellationRequested)
                {
                    return;
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    CaptureRequestError(new GetSecretValueHandler.GetSecretValueError(
                        StatusCodes.Status503ServiceUnavailable,
                        "InternalServiceError",
                        "Key Vault request timed out.",
                        RequestLevel: true));
                    return;
                }
                catch (ArgumentException ex)
                {
                    results[index] = new GetSecretValueHandler.GetSecretValueResult(
                        null,
                        new GetSecretValueHandler.GetSecretValueError(
                            StatusCodes.Status400BadRequest,
                            "InvalidParameterException",
                            ex.Message,
                            RequestLevel: false));
                }
                catch (HttpRequestException ex)
                {
                    CaptureRequestException(ex);
                    return;
                }
                catch (JsonException ex)
                {
                    CaptureRequestException(ex);
                    return;
                }
                catch (FormatException ex)
                {
                    CaptureRequestException(ex);
                    return;
                }
            }
        }

        void CaptureRequestError(GetSecretValueHandler.GetSecretValueError error)
        {
            if (Interlocked.CompareExchange(ref requestError, error, null) is null)
            {
                batchCts.Cancel();
            }
        }

        void CaptureRequestException(Exception exception)
        {
            var captured = ExceptionDispatchInfo.Capture(exception);
            if (Interlocked.CompareExchange(ref requestException, captured, null) is null)
            {
                batchCts.Cancel();
            }
        }
    }

    private static string[] ReadSecretIds(JsonDocument document, out bool filtersRequested)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("BatchGetSecretValue request must be a JSON object.");
        }

        var hasSecretIdList = root.TryGetProperty("SecretIdList", out var secretIdList);
        var hasFilters = root.TryGetProperty("Filters", out var filters);
        filtersRequested = false;

        if (hasSecretIdList && hasFilters)
        {
            throw new ArgumentException("Specify either SecretIdList or Filters, but not both.");
        }

        if (hasFilters)
        {
            if (filters.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("Filters must be a JSON array.");
            }

            filtersRequested = true;
            return [];
        }

        if (root.TryGetProperty("MaxResults", out _)
            || root.TryGetProperty("NextToken", out _))
        {
            throw new ArgumentException("MaxResults and NextToken can be used only with Filters.");
        }

        if (!hasSecretIdList || secretIdList.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("SecretIdList is required and must be a JSON array.");
        }

        var count = secretIdList.GetArrayLength();
        if (count is < 1 or > MaxSecretIds)
        {
            throw new ArgumentException($"SecretIdList must contain between 1 and {MaxSecretIds} items.");
        }

        var result = new string[count];
        var index = 0;
        foreach (var item in secretIdList.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("Every SecretIdList item must be a string.");
            }

            var secretId = item.GetString();
            if (string.IsNullOrWhiteSpace(secretId) || secretId.Length > MaxSecretIdLength)
            {
                throw new ArgumentException($"Every SecretIdList item must contain between 1 and {MaxSecretIdLength} characters.");
            }

            result[index++] = secretId;
        }

        return result;
    }
}
