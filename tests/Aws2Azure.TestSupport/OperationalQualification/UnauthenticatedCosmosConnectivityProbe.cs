using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Aws2Azure.TestSupport.OperationalQualification;

/// <summary>
/// Cosmos DB analogue of <see cref="UnauthenticatedBlobConnectivityProbe"/>: an
/// anonymous (no Authorization header) request against a live Cosmos DB SQL
/// endpoint always fails authentication, but the TLS handshake plus response
/// header round trip is still a genuine network-noise sample for the
/// representative-load signal. The Cosmos DB data-plane REST API replies with
/// HTTP 401 and a JSON <c>{"code":"Unauthorized", ...}</c> body (as opposed to
/// Blob Storage's XML <c>&lt;Error&gt;</c> envelope), so the response shape is
/// validated separately from <see cref="UnauthenticatedBlobConnectivityProbe"/>.
/// </summary>
public static class UnauthenticatedCosmosConnectivityProbe
{
    public const int MaximumErrorBodyBytes = 8 * 1024;
    private static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(10);

    public static async Task<double[]> MeasureHeaderLatenciesAsync(
        Uri target,
        int samples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samples);

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        return await MeasureHeaderLatenciesAsync(
            client,
            target,
            samples,
            SampleTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<double[]> MeasureHeaderLatenciesAsync(
        HttpClient client,
        Uri target,
        int samples,
        TimeSpan sampleTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samples);
        if (sampleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleTimeout),
                sampleTimeout,
                "The per-sample timeout must be positive.");
        }

        var latencies = new double[samples];
        for (var index = 0; index < samples; index++)
        {
            using var sampleCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sampleCancellation.CancelAfter(sampleTimeout);
            var sampleToken = sampleCancellation.Token;
            var started = Stopwatch.GetTimestamp();
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.TryAddWithoutValidation("x-ms-version", "2018-12-31");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                sampleToken).ConfigureAwait(false);
            latencies[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            await ValidateResponseAsync(response, sampleToken).ConfigureAwait(false);
        }

        return latencies;
    }

    public static async Task ValidateResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            throw new InvalidDataException(
                "Unauthenticated Cosmos connectivity-header probe expected an authentication " +
                $"denial but received HTTP {(int)response.StatusCode}.");
        }

        var errorCode = await ReadErrorCodeAsync(response.Content, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(errorCode, "Unauthorized", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Unauthenticated Cosmos connectivity-header probe received unexpected " +
                $"error code '{errorCode}'.");
        }
    }

    private static async Task<string> ReadErrorCodeAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var contentLength = content.Headers.ContentLength;
        if (contentLength > MaximumErrorBodyBytes)
        {
            throw OversizedBody();
        }

        var buffer = ArrayPool<byte>.Shared.Rent(MaximumErrorBodyBytes + 1);
        try
        {
            using var body = await content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var length = 0;
            while (length <= MaximumErrorBodyBytes)
            {
                var read = await body.ReadAsync(
                    buffer.AsMemory(length, MaximumErrorBodyBytes + 1 - length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            if (length > MaximumErrorBodyBytes)
            {
                throw OversizedBody();
            }

            return ParseErrorCode(buffer, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string ParseErrorCode(byte[] buffer, int length)
    {
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("code", out var code)
                || code.ValueKind != JsonValueKind.String)
            {
                throw MalformedBody();
            }

            return code.GetString() ?? throw MalformedBody();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Unauthenticated Cosmos connectivity-header probe returned malformed JSON.",
                exception);
        }
    }

    private static InvalidDataException MalformedBody()
    {
        return new InvalidDataException(
            "Unauthenticated Cosmos connectivity-header probe returned malformed JSON.");
    }

    private static InvalidDataException OversizedBody()
    {
        return new InvalidDataException(
            "Unauthenticated Cosmos connectivity-header probe error body exceeded " +
            $"{MaximumErrorBodyBytes} bytes.");
    }
}
