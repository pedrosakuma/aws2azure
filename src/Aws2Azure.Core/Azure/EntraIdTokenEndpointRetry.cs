using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Aws2Azure.Core.Azure;

internal static class EntraIdTokenEndpointRetry
{
    internal static readonly TimeSpan UnauthorizedRetryDelay = TimeSpan.FromMilliseconds(750);
    private const int UnauthorizedRetryAttempts = 2;

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
            // these token-acquisition call sites get one bounded retry BEFORE a
            // terminal EntraIdTokenException is created. If the retry still sees 401,
            // the existing terminal 401->403 mapping remains unchanged.
            await delayAsync(UnauthorizedRetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ValueTask DelayAsyncDefault(TimeSpan delay, CancellationToken cancellationToken)
        => new(Task.Delay(delay, cancellationToken));
}
