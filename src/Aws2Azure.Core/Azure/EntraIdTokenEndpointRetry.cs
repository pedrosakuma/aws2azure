using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Aws2Azure.Core.Azure;

internal static class EntraIdTokenEndpointRetry
{
    // issue #922 recurred 3+ times (last on the v1.1.0 release cycle) despite a
    // single bounded 750ms retry: a fixed one-shot retry is too narrow a window
    // for the transient Entra ID token-endpoint 401s observed in practice.
    // Widen the retry budget with exponential backoff (750ms, 1.5s, 3s) instead
    // of adding more fixed-delay attempts, so a longer transient blip has a
    // realistic chance to clear without unboundedly extending CI runtime.
    internal static readonly TimeSpan UnauthorizedRetryDelay = TimeSpan.FromMilliseconds(750);
    internal static readonly TimeSpan MaxUnauthorizedRetryDelay = TimeSpan.FromMilliseconds(3000);
    private const int UnauthorizedRetryAttempts = 4;

    internal static ValueTask<string> SendAsync(
        AzureHttpClient http,
        Uri url,
        IReadOnlyList<KeyValuePair<string, string>> form,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, ValueTask>? delayAsync = null)
        => SendCoreAsync(http, url, form, cancellationToken, delayAsync ?? DelayAsyncDefault);

    private static async ValueTask<string> SendCoreAsync(
        AzureHttpClient http,
        Uri url,
        IReadOnlyList<KeyValuePair<string, string>> form,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, ValueTask> delayAsync)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(form)
            };

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return body;
            }

            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt >= UnauthorizedRetryAttempts)
            {
                throw new EntraIdTokenException(response.StatusCode, body);
            }

            // AzureHttpClient intentionally never retries 401 for general Azure REST
            // calls because a data-plane 401 usually means bad/revoked credentials.
            // The Entra token endpoint is narrower: otherwise-valid client_credentials
            // and federated-JWT exchanges can occasionally get a transient STS 401, so
            // these token-acquisition call sites get a bounded, exponentially-backed-off
            // retry budget BEFORE a terminal EntraIdTokenException is created. If the
            // budget is exhausted, the existing terminal 401->403 mapping remains
            // unchanged.
            await delayAsync(DelayForAttempt(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Computes the backoff delay before retrying the attempt-th failed request
    /// (1-based). Doubles <see cref="UnauthorizedRetryDelay"/> per attempt, capped
    /// at <see cref="MaxUnauthorizedRetryDelay"/>.
    /// </summary>
    internal static TimeSpan DelayForAttempt(int attempt)
    {
        var shift = Math.Min(attempt - 1, 30);
        var scaled = UnauthorizedRetryDelay * (1L << shift);
        return scaled > MaxUnauthorizedRetryDelay ? MaxUnauthorizedRetryDelay : scaled;
    }

    private static ValueTask DelayAsyncDefault(TimeSpan delay, CancellationToken cancellationToken)
        => new(Task.Delay(delay, cancellationToken));
}
