using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Xml;

namespace Aws2Azure.TestSupport.OperationalQualification;

public static class UnauthenticatedBlobConnectivityProbe
{
    public const int MaximumErrorBodyBytes = 8 * 1024;
    private static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] ForbiddenAuthenticationCodes =
    [
        "AuthenticationFailed",
        "AuthorizationFailure",
        "AuthorizationPermissionMismatch",
    ];

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
            using var response = await client.GetAsync(
                target,
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

        var expectedCode = response.StatusCode switch
        {
            HttpStatusCode.Forbidden => ForbiddenAuthenticationCodes,
            HttpStatusCode.Conflict => ["PublicAccessNotPermitted"],
            _ => throw new InvalidDataException(
                "Unauthenticated Blob connectivity-header probe expected an authentication " +
                $"denial but received HTTP {(int)response.StatusCode}."),
        };

        var errorCode = await ReadErrorCodeAsync(response.Content, cancellationToken)
            .ConfigureAwait(false);
        if (!expectedCode.Contains(errorCode, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "Unauthenticated Blob connectivity-header probe received unexpected " +
                $"HTTP {(int)response.StatusCode} error code '{errorCode}'.");
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

            return await ParseErrorCodeAsync(buffer, length).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string> ParseErrorCodeAsync(byte[] buffer, int length)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = MaximumErrorBodyBytes,
            XmlResolver = null,
        };

        try
        {
            using var body = new MemoryStream(buffer, 0, length, writable: false);
            using var reader = XmlReader.Create(body, settings);
            await reader.MoveToContentAsync().ConfigureAwait(false);
            if (reader.NodeType != XmlNodeType.Element
                || !string.Equals(reader.LocalName, "Error", StringComparison.Ordinal))
            {
                throw MalformedBody();
            }

            var rootDepth = reader.Depth;
            string? errorCode = null;
            var hasNode = await reader.ReadAsync().ConfigureAwait(false);
            while (hasNode)
            {
                if (reader.NodeType == XmlNodeType.Element
                    && string.Equals(reader.LocalName, "Code", StringComparison.Ordinal))
                {
                    if (reader.Depth != rootDepth + 1 || errorCode is not null)
                    {
                        throw MalformedBody();
                    }

                    errorCode = await reader.ReadElementContentAsStringAsync()
                        .ConfigureAwait(false);
                    hasNode = !reader.EOF;
                    continue;
                }

                hasNode = await reader.ReadAsync().ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(errorCode))
            {
                throw MalformedBody();
            }

            return errorCode;
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(
                "Unauthenticated Blob connectivity-header probe returned malformed XML.",
                exception);
        }
    }

    private static InvalidDataException MalformedBody()
    {
        return new InvalidDataException(
            "Unauthenticated Blob connectivity-header probe returned malformed XML.");
    }

    private static InvalidDataException OversizedBody()
    {
        return new InvalidDataException(
            "Unauthenticated Blob connectivity-header probe error body exceeded " +
            $"{MaximumErrorBodyBytes} bytes.");
    }
}
