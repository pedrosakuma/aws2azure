using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal static class GitHubOidcAssertion
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    internal static async Task<string> AcquireAsync(
        HttpClient client,
        string requestUrl,
        string requestToken,
        TextWriter diagnostics,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        var separator = requestUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = requestUrl + separator + "audience=api%3A%2F%2FAzureADTokenExchange";
        var started = timeProvider.GetTimestamp();
        using var deadline = new CancellationTokenSource(Budget, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;

        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);
            TimeSpan delay;
            using (var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(body);
                    if (document.RootElement.ValueKind != JsonValueKind.Object
                        || !document.RootElement.TryGetProperty("value", out var value)
                        || value.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(value.GetString()))
                        throw new InvalidDataException("GitHub OIDC response did not contain a token assertion.");
                    token.ThrowIfCancellationRequested();
                    diagnostics.WriteLine($"GitHub OIDC assertion acquired on attempt {attempt}/{MaxAttempts}.");
                    return value.GetString()!;
                }

                diagnostics.WriteLine(
                    $"GitHub OIDC assertion attempt {attempt}/{MaxAttempts} returned HTTP {(int)response.StatusCode}.");
                if (!IsTransient(response.StatusCode) || attempt == MaxAttempts)
                    throw Failure(response.StatusCode, attempt);

                var retryAfter = response.Headers.RetryAfter;
                delay = retryAfter?.Delta
                    ?? (retryAfter?.Date is { } date ? date - timeProvider.GetUtcNow() : TimeSpan.FromSeconds(attempt));
                if (delay < TimeSpan.Zero)
                    delay = TimeSpan.Zero;
                var remaining = Budget - timeProvider.GetElapsedTime(started);
                if (delay >= remaining)
                    throw new HttpRequestException(
                        "GitHub OIDC retry delay exceeds the remaining 30-second assertion budget.",
                        null, response.StatusCode);
            }

            await Task.Delay(delay, timeProvider, token).ConfigureAwait(false);
        }
    }

    private static bool IsTransient(HttpStatusCode status) => status is
        HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static HttpRequestException Failure(HttpStatusCode status, int attempt) => new(
        $"GitHub OIDC token request failed with HTTP {(int)status} after {attempt} attempt(s).", null, status);
}
