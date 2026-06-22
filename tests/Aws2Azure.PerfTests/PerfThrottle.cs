using System.Net;
using Amazon.Runtime;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Classifies a caught perf-action exception as <b>backend throttling</b>
/// (HTTP 429 / "request rate too large") versus a genuine proxy/transport
/// <b>defect</b> (issue #456).
///
/// <para>Against a serverless Azure backend (e.g. serverless Cosmos, which has a
/// hard RU/s ceiling) a write-heavy scenario at the Tier 2 scaled concurrency
/// (#420) can saturate the RU ceiling; the backend then returns 429 and the
/// proxy faithfully relays it, so the AWS SDK surfaces a throttle exception after
/// its retries are exhausted. That is expected <i>backpressure</i> — the backend
/// asking the client to slow down — not a translation bug, and it must not red an
/// otherwise-healthy A/B run (which would also discard the valuable read-side
/// saturation-sweep data that ran green in the same job).</para>
///
/// <para>The classification is deliberately precise: it keys off the AWS SDK's
/// own <see cref="AmazonServiceException.StatusCode"/> / <see cref="AmazonServiceException.ErrorCode"/>
/// throttle signals (and a small set of unambiguous throttle messages for wrapped
/// non-AWS exceptions), never a broad "any error" heuristic, so a real proxy 5xx /
/// translation fault is still counted as a failure and still fails the run.</para>
/// </summary>
internal static class PerfThrottle
{
    /// <summary>HTTP 429 Too Many Requests — the canonical throttle status.</summary>
    private const int TooManyRequests = 429;

    /// <summary>
    /// AWS/Azure throttle error codes. AWS surfaces throttling as one of these
    /// codes across services; DynamoDB's mapped Cosmos 429 arrives as
    /// <c>ProvisionedThroughputExceededException</c>.
    /// </summary>
    private static readonly HashSet<string> ThrottleErrorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ProvisionedThroughputExceededException",
        "RequestLimitExceeded",
        "ThrottlingException",
        "Throttling",
        "TooManyRequestsException",
        "RequestThrottledException",
        "RequestThrottled",
        "RequestRateTooLarge",
        "TooManyRequests",
    };

    /// <summary>
    /// Unambiguous throttle markers for exceptions that are not (or no longer)
    /// typed as <see cref="AmazonServiceException"/> — e.g. a wrapped Cosmos
    /// error. Kept narrow on purpose: a bare "429" substring is excluded because
    /// it can appear in unrelated payloads.
    /// </summary>
    private static readonly string[] ThrottleMessageMarkers =
    {
        "Request rate is large",
        "RequestRateTooLarge",
        "TooManyRequests",
        "ThrottlingException",
        "ProvisionedThroughputExceeded",
    };

    /// <summary>
    /// True when <paramref name="exception"/> (or any exception nested within it)
    /// represents backend throttling rather than a genuine failure.
    /// </summary>
    public static bool IsThrottle(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (MatchesThrottle(current))
            {
                return true;
            }

            // AggregateException can fan out to multiple inner faults (e.g. a
            // batched send) — any throttled branch makes the whole op a throttle.
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (IsThrottle(inner))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool MatchesThrottle(Exception exception)
    {
        if (exception is AmazonServiceException aws)
        {
            if ((int)aws.StatusCode == TooManyRequests)
            {
                return true;
            }
            if (!string.IsNullOrEmpty(aws.ErrorCode) && ThrottleErrorCodes.Contains(aws.ErrorCode))
            {
                return true;
            }
        }

        // Defensive fallback for wrapped / non-AWS throttles: match on the
        // exception type name (e.g. "...ProvisionedThroughputExceededException")
        // and a narrow set of unambiguous throttle messages.
        var typeName = exception.GetType().Name;
        if (ThrottleErrorCodes.Contains(typeName))
        {
            return true;
        }

        var message = exception.Message;
        if (!string.IsNullOrEmpty(message))
        {
            foreach (var marker in ThrottleMessageMarkers)
            {
                if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Convenience for callers that have an <see cref="HttpStatusCode"/>.</summary>
    public static bool IsThrottle(HttpStatusCode statusCode) => (int)statusCode == TooManyRequests;
}
