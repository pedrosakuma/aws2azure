using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.SigV4;
using Microsoft.AspNetCore.Http;
using System.Globalization;

namespace Aws2Azure.Modules.S3.Streaming;

/// <summary>
/// Shared detection and decoder wiring for AWS chunked (<c>aws-chunked</c>)
/// request bodies, used by both PutObject and UploadPart. Recognizes every
/// <c>STREAMING-*</c> framing emitted via the <c>x-amz-content-sha256</c>
/// header — signed or unsigned, with or without a trailing checksum section —
/// and builds the matching <see cref="AwsChunkedDecoder"/>.
/// </summary>
internal static class AwsChunkedRequest
{
    /// <summary>
    /// The AWS chunked framing variant indicated by <c>x-amz-content-sha256</c>.
    /// </summary>
    public readonly record struct Format(bool Detected, bool Signed, bool Trailer)
    {
        public static Format None => default;
    }

    public readonly record struct PreparedBody(Stream Body, long? ContentLength, AwsChunkedDecoder? Decoder);

    public static PreparedBody PrepareBodyStream(HttpRequest request, ICredentialResolver? credentials)
    {
        var body = request.Body;
        var contentLength = request.ContentLength;
        AwsChunkedDecoder? decoder = null;

        var format = Detect(request);
        if (format.Detected)
        {
            if (request.Headers.TryGetValue("x-amz-decoded-content-length", out var decodedLenHeader)
                && long.TryParse(decodedLenHeader.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var decodedLen))
            {
                contentLength = decodedLen;
            }
            else
            {
                contentLength = null;
            }

            decoder = CreateDecoder(request, format, credentials);
            body = decoder;
        }

        return new PreparedBody(body, contentLength, decoder);
    }

    public static Format Detect(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("x-amz-content-sha256", out var contentSha))
            return Format.None;

        foreach (var raw in contentSha)
        {
            var v = (raw ?? string.Empty).Trim();
            switch (v)
            {
                // Signed chunk framings (each chunk carries a chunk-signature).
                case "STREAMING-AWS4-HMAC-SHA256-PAYLOAD":
                    return new Format(Detected: true, Signed: true, Trailer: false);
                case "STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER":
                    return new Format(Detected: true, Signed: true, Trailer: true);

                // Unsigned chunk framings (no per-chunk signature; the default
                // for modern AWS SDKs over HTTPS, optionally with a checksum
                // trailer). The -ECDSA-* signed variants (SigV4a) are not
                // recognized here and fall through.
                case "STREAMING-UNSIGNED-PAYLOAD":
                    return new Format(Detected: true, Signed: false, Trailer: false);
                case "STREAMING-UNSIGNED-PAYLOAD-TRAILER":
                    return new Format(Detected: true, Signed: false, Trailer: true);
            }
        }

        return Format.None;
    }

    /// <summary>
    /// Wraps <paramref name="request"/>.Body in an <see cref="AwsChunkedDecoder"/>
    /// for the given detected <paramref name="format"/>. Chunk-signature
    /// verification is configured only for signed framings.
    /// </summary>
    public static AwsChunkedDecoder CreateDecoder(HttpRequest request, Format format, ICredentialResolver? credentials)
    {
        ChunkSigningContext? signingContext = null;
        if (format.Signed && credentials is not null)
        {
            TryBuildChunkSigningContext(request, credentials, out signingContext);
        }

        return new AwsChunkedDecoder(
            request.Body, signingContext, leaveOpen: true, expectTrailer: format.Trailer);
    }

    private static bool TryBuildChunkSigningContext(HttpRequest request, ICredentialResolver credentials, out ChunkSigningContext? context)
    {
        context = null;

        // Parse Authorization header to get seed signature and credential scope
        var authHeader = request.Headers["Authorization"].ToString();
        if (string.IsNullOrEmpty(authHeader) || !AuthorizationHeader.TryParse(authHeader, out var parsed))
            return false;

        // Get amz-date
        var amzDate = request.Headers["x-amz-date"].ToString();
        if (string.IsNullOrEmpty(amzDate))
            amzDate = request.Headers["Date"].ToString();
        if (string.IsNullOrEmpty(amzDate))
            return false;

        // Get AWS secret for the access key
        if (!credentials.TryGetAwsSecret(parsed.Credential.AccessKeyId, out var secret))
            return false;

        // Derive signing key
        var signingKey = SigningKey.Derive(
            secret,
            parsed.Credential.Date,
            parsed.Credential.Region,
            parsed.Credential.Service);

        context = new ChunkSigningContext
        {
            SigningKey = signingKey,
            AmzDate = amzDate,
            CredentialScope = parsed.Credential.ToScopeString(),
            SeedSignature = parsed.Signature,
        };
        return true;
    }
}
