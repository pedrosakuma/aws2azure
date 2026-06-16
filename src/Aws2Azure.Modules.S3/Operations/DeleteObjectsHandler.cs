using System.Net;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.S3.Errors;
using Aws2Azure.Modules.S3.Internal;
using Aws2Azure.Modules.S3.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.S3.Operations;

/// <summary>
/// Handler for <see cref="S3Operation.DeleteObjects"/> — POST /{bucket}?delete
/// with a <c>&lt;Delete&gt;</c> XML body. Azure Blob Storage has no native
/// batch endpoint, so the operation is fanned out as parallel single-blob
/// DELETEs against the destination container, with a per-request concurrency
/// cap to avoid swamping the upstream.
/// </summary>
internal static class DeleteObjectsHandler
{
    private const int MaxConcurrency = 16;

    // Body cap: 1 000 keys × 1 024-byte key max + ~48 KB envelope overhead
    // (XML tags, quiet flag, whitespace). 2 MiB is a safe upper bound that
    // also leaves room for percent-encoding without giving an attacker a
    // memory amplification primitive.
    private const long MaxBodyBytes = 2 * 1024 * 1024;

    public static async Task HandleAsync(HttpContext context, S3RouteResult route, BlobClient blob, CancellationToken ct)
    {
        var bucket = route.Bucket ?? string.Empty;
        if (S3ErrorMapping.ClassifyLookupBucketName(bucket) is { } bucketError)
        {
            await S3ErrorMapping.WriteAsync(context, bucketError).ConfigureAwait(false);
            return;
        }

        // Real S3 requires a checksum header on DeleteObjects. Older SDKs
        // send Content-MD5; v2/v3 SDKs may send x-amz-sdk-checksum-algorithm
        // with a matching x-amz-checksum-* trailer instead. Accept either,
        // and when Content-MD5 is supplied, validate it against the body
        // (BadDigest is a hard, non-retryable failure for the caller).
        var contentMd5 = ReadFirstHeader(context.Request, "Content-MD5");
        var hasChecksumAlgo = context.Request.Headers.ContainsKey("x-amz-sdk-checksum-algorithm");
        if (string.IsNullOrEmpty(contentMd5) && !hasChecksumAlgo)
        {
            await S3ErrorMapping.WriteAsync(context, new S3ErrorMapping.Mapping(400, "InvalidRequest",
                "Missing required header for this request: Content-MD5.")).ConfigureAwait(false);
            return;
        }

        byte[] bodyBytes;
        try
        {
            using var buffered = new MemoryStream();
            // Cap the buffered body so a hostile client can't blow up memory
            // by sending a multi-gigabyte payload before the 1 000-key check.
            var limited = new LimitedStream(context.Request.Body, MaxBodyBytes);
            await limited.CopyToAsync(buffered, ct).ConfigureAwait(false);
            bodyBytes = buffered.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            await S3ErrorMapping.WriteAsync(context, new S3ErrorMapping.Mapping(400, "EntityTooLarge",
                $"DeleteObjects request body exceeds the {MaxBodyBytes}-byte limit.")).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidArgument("MalformedXML: " + ex.Message)).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrEmpty(contentMd5))
        {
            var digest = System.Security.Cryptography.MD5.HashData(bodyBytes);
            var actual = Convert.ToBase64String(digest);
            if (!string.Equals(actual, contentMd5.Trim(), StringComparison.Ordinal))
            {
                await S3ErrorMapping.WriteAsync(context, new S3ErrorMapping.Mapping(400, "BadDigest",
                    "The Content-MD5 you specified did not match what we received.")).ConfigureAwait(false);
                return;
            }
        }

        DeleteRequestParser.ParseResult parsed;
        try
        {
            using var ms = new MemoryStream(bodyBytes, writable: false);
            parsed = DeleteRequestParser.Parse(ms);
        }
        catch (Exception ex)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidArgument("MalformedXML: " + ex.Message)).ConfigureAwait(false);
            return;
        }

        if (!parsed.Success)
        {
            await S3ErrorMapping.WriteAsync(context,
                new S3ErrorMapping.Mapping(400, "MalformedXML", parsed.Error!)).ConfigureAwait(false);
            return;
        }

        // Confirm the bucket exists up-front. S3 returns a top-level
        // NoSuchBucket error (not per-key) when the destination container
        // is missing, so SDK callers can surface a single, retryable error
        // instead of N parallel per-key failures.
        var bucketCheck = await CheckBucketAsync(blob, bucket, ct).ConfigureAwait(false);
        if (bucketCheck is { } bucketErr)
        {
            await S3ErrorMapping.WriteAsync(context, bucketErr).ConfigureAwait(false);
            return;
        }

        var deleted = new List<S3XmlWriter.DeletedEntry>(parsed.Objects.Count);
        var errors  = new List<S3XmlWriter.DeleteErrorEntry>();
        var results = new (string Key, S3ErrorMapping.Mapping? Error)[parsed.Objects.Count];

        using (var sem = new SemaphoreSlim(MaxConcurrency))
        {
            var tasks = new Task[parsed.Objects.Count];
            for (var i = 0; i < parsed.Objects.Count; i++)
            {
                var idx = i;
                var key = parsed.Objects[idx].Key;
                tasks[idx] = Task.Run(async () =>
                {
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        results[idx] = (key, await DeleteOneAsync(blob, bucket, key, ct).ConfigureAwait(false));
                    }
                    finally
                    {
                        sem.Release();
                    }
                }, ct);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        // Preserve request order in the response — SDKs match Deleted/Error
        // entries against their input by position when keys collide.
        foreach (var (key, err) in results)
        {
            if (err is { } e)
            {
                errors.Add(new S3XmlWriter.DeleteErrorEntry(key, e.Code, e.Message));
            }
            else
            {
                deleted.Add(new S3XmlWriter.DeletedEntry(key));
            }
        }

        var body = S3XmlWriter.DeleteResult(parsed.Quiet, deleted, errors);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/xml; charset=utf-8";
        await context.Response.WriteAsync(body, ct).ConfigureAwait(false);
    }

    private static async Task<S3ErrorMapping.Mapping?> CheckBucketAsync(BlobClient blob, string bucket, CancellationToken ct)
    {
        // HEAD ?restype=container is the Azure equivalent of HeadBucket and
        // returns 404 ContainerNotFound when missing. We do this once up
        // front so a single missing bucket short-circuits the fan-out and
        // matches S3's top-level NoSuchBucket response shape.
        var uri = new Uri(blob.BuildContainerUri(bucket).AbsoluteUri + "?restype=container");
        using var req = new HttpRequestMessage(HttpMethod.Head, uri);
        using var resp = await blob.SendBlobRequestAsync(req, ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
        {
            return null;
        }
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return new S3ErrorMapping.Mapping(404, "NoSuchBucket", "The specified bucket does not exist.");
        }
        return S3ErrorMapping.FromAzure(resp, S3Operation.DeleteObjects);
    }

    private static async Task<S3ErrorMapping.Mapping?> DeleteOneAsync(
        BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        if (!S3ObjectKey.IsValid(key))
        {
            return S3ErrorMapping.InvalidObjectKey();
        }

        using var req = new HttpRequestMessage(HttpMethod.Delete, blob.BuildBlobUri(bucket, key));
        using var resp = await blob.SendBlobRequestAsync(req, ct).ConfigureAwait(false);

        if (resp.IsSuccessStatusCode)
        {
            return null;
        }

        // S3 DeleteObjects treats a missing key as a successful no-op (the
        // entry is still reported as Deleted, never as an Error).
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            var mapping = S3ErrorMapping.FromAzure(resp, S3Operation.DeleteObject);
            if (mapping.Code == "NoSuchKey")
            {
                return null;
            }
            return mapping;
        }

        return S3ErrorMapping.FromAzure(resp, S3Operation.DeleteObject);
    }

    private static string? ReadFirstHeader(HttpRequest request, string name)
    {
        if (!request.Headers.TryGetValue(name, out var values))
        {
            return null;
        }
        foreach (var v in values)
        {
            if (!string.IsNullOrEmpty(v))
            {
                return v;
            }
        }
        return null;
    }

    /// <summary>
    /// Wraps an upstream <see cref="Stream"/> with a hard byte cap. Once the
    /// cap is exceeded reads throw <see cref="InvalidDataException"/>, which
    /// the handler maps to an S3 <c>EntityTooLarge</c> error rather than
    /// allowing the body buffer to grow unbounded.
    /// </summary>
    private sealed class LimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _read;

        public LimitedStream(Stream inner, long limit)
        {
            _inner = inner;
            _limit = limit;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read >= _limit)
            {
                ThrowTooLarge();
            }
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _read += read;
            if (_read > _limit)
            {
                ThrowTooLarge();
            }
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void ThrowTooLarge() =>
            throw new InvalidDataException($"Request body exceeded the {_limit}-byte limit.");
    }
}
