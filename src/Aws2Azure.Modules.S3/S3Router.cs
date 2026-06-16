using System.Collections.Frozen;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.S3;

/// <summary>
/// Classifies an incoming HTTP request as a specific <see cref="S3Operation"/>
/// using only HTTP method + path + query, mirroring the wire-level decisions a
/// real S3 endpoint makes. Reflection-free and allocation-light.
/// </summary>
/// <remarks>
/// Slice 1 (bucket lifecycle) supports path-style only. Virtual-hosted URLs
/// — <c>{bucket}.s3.amazonaws.com</c> — are detected and surfaced via
/// <see cref="S3RouteResult.VirtualHosted"/>; the handler returns
/// <see cref="S3Operation.Unsupported"/> until later slices opt in.
/// </remarks>
public static class S3Router
{
    public static S3RouteResult Classify(HttpContext context)
    {
        var request = context.Request;
        var host = request.Host.Host ?? string.Empty;
        var virtualHosted = IsVirtualHostedStyle(host, out var hostBucket);

        // Path-style: everything is encoded in the URI path.
        // /                → ListBuckets (host-only ops carry no bucket)
        // /{bucket}        → bucket-scoped op
        // /{bucket}/{key…} → object-scoped op
        var path = request.Path.HasValue ? request.Path.Value!.AsSpan() : ReadOnlySpan<char>.Empty;
        var (bucket, key) = SplitPath(path);

        if (virtualHosted)
        {
            // Host-encoded bucket wins for virtual-hosted requests.
            // Everything in the path is then the key (possibly empty).
            bucket = hostBucket;
            key = path.Length > 1 ? path[1..].ToString() : null;
        }

        var method = request.Method;

        if (string.IsNullOrEmpty(bucket))
        {
            return method == HttpMethods.Get
                ? new S3RouteResult(S3Operation.ListBuckets, null, null, virtualHosted)
                : new S3RouteResult(S3Operation.Unknown, null, null, virtualHosted);
        }

        if (!string.IsNullOrEmpty(key))
        {
            // Multipart upload subresources (?uploads, ?uploadId, ?partNumber)
            // are first-class operations layered on the same path. Classify
            // them before the catch-all subresource gate so they reach the
            // multipart handler instead of returning Unsupported.
            if (IsMultipartObjectRequest(method, request, out var multipartOp))
            {
                return new S3RouteResult(multipartOp, bucket, key, virtualHosted);
            }

            // Object subresources (?acl, ?tagging, ?torrent, ?restore,
            // ?legal-hold, ?retention) are distinct S3 operations. Classify
            // them to dedicated ops (Slice 9: tagging is real; the rest are
            // ownership-only / NotImplemented stubs) so clients get coherent
            // S3-shaped responses instead of generic 501.
            if (TryClassifyObjectSubresource(method, request.Query, out var objSubOp))
            {
                return new S3RouteResult(objSubOp, bucket, key, virtualHosted);
            }
            if (HasObjectSubresource(request.Query))
            {
                return new S3RouteResult(S3Operation.Unsupported, bucket, key, virtualHosted);
            }
            return method switch
            {
                var m when m == HttpMethods.Get    => new S3RouteResult(S3Operation.GetObject,    bucket, key, virtualHosted),
                var m when m == HttpMethods.Head   => new S3RouteResult(S3Operation.HeadObject,   bucket, key, virtualHosted),
                var m when m == HttpMethods.Put    => new S3RouteResult(ClassifyPutObject(request), bucket, key, virtualHosted),
                var m when m == HttpMethods.Delete => new S3RouteResult(S3Operation.DeleteObject, bucket, key, virtualHosted),
                _ => new S3RouteResult(S3Operation.Unknown, bucket, key, virtualHosted),
            };
        }

        // Bucket subresources (?tagging, ?policy, ?lifecycle, …). Slice 9
        // classifies them to dedicated ops; ?uploads stays as
        // ListMultipartUploads and is handled below.
        if (TryClassifyBucketSubresource(method, request.Query, out var bucketSubOp))
        {
            return new S3RouteResult(bucketSubOp, bucket, null, virtualHosted);
        }
        if (HasBucketSubresource(request.Query) &&
            !(method == HttpMethods.Get && request.Query.ContainsKey("uploads")))
        {
            return new S3RouteResult(S3Operation.Unsupported, bucket, null, virtualHosted);
        }

        // Bucket-scoped ops: HEAD/PUT/DELETE on /{bucket}. GET /{bucket} is
        // the object listing — V2 when the client sets ?list-type=2,
        // otherwise legacy V1. POST /{bucket}?delete is the multi-object
        // delete batch. GET /{bucket}?uploads enumerates in-progress
        // multipart uploads.
        return method switch
        {
            var m when m == HttpMethods.Head   => new S3RouteResult(S3Operation.HeadBucket,   bucket, null, virtualHosted),
            var m when m == HttpMethods.Put    => new S3RouteResult(S3Operation.CreateBucket, bucket, null, virtualHosted),
            var m when m == HttpMethods.Delete => new S3RouteResult(S3Operation.DeleteBucket, bucket, null, virtualHosted),
            var m when m == HttpMethods.Get && request.Query.ContainsKey("uploads")
                                              => new S3RouteResult(S3Operation.ListMultipartUploads, bucket, null, virtualHosted),
            var m when m == HttpMethods.Get    => new S3RouteResult(ClassifyListOperation(request.Query), bucket, null, virtualHosted),
            var m when m == HttpMethods.Post && request.Query.ContainsKey("delete")
                                              => new S3RouteResult(S3Operation.DeleteObjects, bucket, null, virtualHosted),
            _ => new S3RouteResult(S3Operation.Unknown, bucket, null, virtualHosted),
        };
    }

    public static bool MatchesHost(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        return IsPathStyleEndpoint(host) || FindVirtualHostedMarker(host) >= 0;
    }

    /// <summary>
    /// S3 multipart upload subresources hang off the object path with their
    /// own verb mapping:
    ///   POST /{b}/{k}?uploads                       → CreateMultipartUpload
    ///   PUT  /{b}/{k}?uploadId=X&amp;partNumber=N   → UploadPart (or UploadPartCopy
    ///                                                 when x-amz-copy-source is set)
    ///   POST /{b}/{k}?uploadId=X                    → CompleteMultipartUpload
    ///   DELETE /{b}/{k}?uploadId=X                  → AbortMultipartUpload
    ///   GET  /{b}/{k}?uploadId=X                    → ListParts
    /// </summary>
    private static bool IsMultipartObjectRequest(string method, HttpRequest request, out S3Operation op)
    {
        op = S3Operation.Unknown;
        var query = request.Query;
        var hasUploads = query.ContainsKey("uploads");
        var hasUploadId = query.ContainsKey("uploadId");
        var hasPartNumber = query.ContainsKey("partNumber");

        if (!hasUploads && !hasUploadId && !hasPartNumber)
        {
            return false;
        }

        if (hasUploads && method == HttpMethods.Post)
        {
            op = S3Operation.CreateMultipartUpload;
            return true;
        }
        if (hasUploadId && hasPartNumber && method == HttpMethods.Put)
        {
            op = HasCopySourceHeader(request)
                ? S3Operation.UploadPartCopy
                : S3Operation.UploadPart;
            return true;
        }
        if (hasUploadId && !hasPartNumber && method == HttpMethods.Post)
        {
            op = S3Operation.CompleteMultipartUpload;
            return true;
        }
        if (hasUploadId && !hasPartNumber && method == HttpMethods.Delete)
        {
            op = S3Operation.AbortMultipartUpload;
            return true;
        }
        if (hasUploadId && !hasPartNumber && method == HttpMethods.Get)
        {
            op = S3Operation.ListParts;
            return true;
        }
        // Recognised subresource but not a routable verb combo — fall through
        // to Unsupported so the catch-all subresource gate maps it to a
        // NotImplemented error rather than silently mishandling.
        op = S3Operation.Unsupported;
        return true;
    }

    private static S3Operation ClassifyListOperation(IQueryCollection query)
    {
        // S3 promotes a GET on the bucket to ListObjectsV2 when list-type=2
        // is present. Any other value (including absent) is the legacy V1.
        if (query.TryGetValue("list-type", out var values))
        {
            foreach (var v in values)
            {
                if (string.Equals(v, "2", StringComparison.Ordinal))
                {
                    return S3Operation.ListObjectsV2;
                }
            }
        }
        return S3Operation.ListObjects;
    }

    /// <summary>
    /// S3 overloads PUT /{bucket}/{key} on a header: when
    /// <c>x-amz-copy-source</c> is present the request is a CopyObject
    /// instead of a PutObject upload. Detecting the header at the router
    /// keeps the per-operation dispatch table flat.
    /// </summary>
    private static S3Operation ClassifyPutObject(HttpRequest request)
    {
        return HasCopySourceHeader(request) ? S3Operation.CopyObject : S3Operation.PutObject;
    }

    /// <summary>
    /// True when the request carries a non-empty <c>x-amz-copy-source</c>
    /// header. Used to disambiguate PUT-on-object (PutObject vs CopyObject)
    /// and PUT-on-multipart-part (UploadPart vs UploadPartCopy).
    /// </summary>
    private static bool HasCopySourceHeader(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("x-amz-copy-source", out var values))
        {
            return false;
        }
        foreach (var v in values)
        {
            if (!string.IsNullOrEmpty(v))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly SubresourceDefinition[] ObjectSubresourceDefinitions =
    {
        new("tagging", new(get: S3Operation.GetObjectTagging, put: S3Operation.PutObjectTagging, delete: S3Operation.DeleteObjectTagging)),
        new("acl", new(get: S3Operation.GetObjectAcl, put: S3Operation.PutObjectAcl)),
        new("torrent", new(get: S3Operation.GetObjectTorrent)),
        new("restore", new(post: S3Operation.RestoreObject)),
        new("legal-hold", new(get: S3Operation.GetObjectLegalHold, put: S3Operation.PutObjectLegalHold)),
        new("retention", new(get: S3Operation.GetObjectRetention, put: S3Operation.PutObjectRetention)),
        new("attributes", SubresourceOperations.UnsupportedOnly),
        new("uploads", SubresourceOperations.UnsupportedOnly),
        new("uploadId", SubresourceOperations.UnsupportedOnly),
        new("partNumber", SubresourceOperations.UnsupportedOnly),
        new("select", SubresourceOperations.UnsupportedOnly),
        new("select-type", SubresourceOperations.UnsupportedOnly),
        new("versions", SubresourceOperations.UnsupportedOnly),
        new("versionId", SubresourceOperations.UnsupportedOnly),
    };

    // Well-known S3 object subresources. Membership is checked case-insensitively.
    private static readonly FrozenDictionary<string, SubresourceOperations> ObjectSubresources =
        CreateSubresourceLookup(ObjectSubresourceDefinitions);

    private static bool HasObjectSubresource(IQueryCollection query)
    {
        if (query.Count == 0)
        {
            return false;
        }
        foreach (var kv in query)
        {
            if (ObjectSubresources.ContainsKey(kv.Key))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Maps an object-scoped subresource query key + HTTP method to the
    /// specific <see cref="S3Operation"/> the long-tail handler will dispatch
    /// to. Returns false when the subresource is not recognised. Recognised
    /// subresources with unsupported verbs map explicitly to
    /// <see cref="S3Operation.Unsupported"/>.
    /// </summary>
    private static bool TryClassifyObjectSubresource(string method, IQueryCollection query, out S3Operation op)
    {
        return TryClassifySubresource(method, query, ObjectSubresourceDefinitions, out op);
    }

    private static readonly SubresourceDefinition[] BucketSubresourceDefinitions =
    {
        new("tagging", new(get: S3Operation.GetBucketTagging, put: S3Operation.PutBucketTagging, delete: S3Operation.DeleteBucketTagging)),
        new("acl", new(get: S3Operation.GetBucketAcl, put: S3Operation.PutBucketAcl)),
        new("lifecycle", new(get: S3Operation.GetBucketLifecycleConfiguration, put: S3Operation.PutBucketLifecycleConfiguration, delete: S3Operation.DeleteBucketLifecycle)),
        new("cors", new(get: S3Operation.GetBucketCors, put: S3Operation.PutBucketCors, delete: S3Operation.DeleteBucketCors)),
        new("website", new(get: S3Operation.GetBucketWebsite, put: S3Operation.PutBucketWebsite, delete: S3Operation.DeleteBucketWebsite)),
        new("replication", new(get: S3Operation.GetBucketReplication, put: S3Operation.PutBucketReplication, delete: S3Operation.DeleteBucketReplication)),
        new("encryption", new(get: S3Operation.GetBucketEncryption, put: S3Operation.PutBucketEncryption, delete: S3Operation.DeleteBucketEncryption)),
        new("logging", new(get: S3Operation.GetBucketLogging, put: S3Operation.PutBucketLogging)),
        new("versioning", new(get: S3Operation.GetBucketVersioning, put: S3Operation.PutBucketVersioning)),
        new("requestPayment", new(get: S3Operation.GetBucketRequestPayment, put: S3Operation.PutBucketRequestPayment)),
        new("object-lock", new(get: S3Operation.GetObjectLockConfiguration, put: S3Operation.PutObjectLockConfiguration)),
        new("publicAccessBlock", new(get: S3Operation.GetPublicAccessBlock, put: S3Operation.PutPublicAccessBlock, delete: S3Operation.DeletePublicAccessBlock)),
        new("policy", new(get: S3Operation.GetBucketPolicy, put: S3Operation.PutBucketPolicy, delete: S3Operation.DeleteBucketPolicy)),
        new("policyStatus", new(get: S3Operation.GetBucketPolicyStatus)),
        new("notification", new(get: S3Operation.GetBucketNotificationConfiguration, put: S3Operation.PutBucketNotificationConfiguration)),
        new("accelerate", new(get: S3Operation.GetBucketAccelerateConfiguration, put: S3Operation.PutBucketAccelerateConfiguration)),
        new("ownershipControls", new(get: S3Operation.GetBucketOwnershipControls, put: S3Operation.PutBucketOwnershipControls, delete: S3Operation.DeleteBucketOwnershipControls)),
        new("analytics", SubresourceOperations.UnsupportedOnly),
        new("intelligent-tiering", SubresourceOperations.UnsupportedOnly),
        new("inventory", SubresourceOperations.UnsupportedOnly),
        new("metrics", SubresourceOperations.UnsupportedOnly),
        new("uploads", SubresourceOperations.UnsupportedOnly),
        new("versions", SubresourceOperations.UnsupportedOnly),
    };

    // Well-known S3 bucket subresources. Membership is checked case-insensitively
    // since SDKs and SigV4 canonicalization both normalise query keys.
    private static readonly FrozenDictionary<string, SubresourceOperations> BucketSubresources =
        CreateSubresourceLookup(BucketSubresourceDefinitions);

    private static bool HasBucketSubresource(IQueryCollection query)
    {
        if (query.Count == 0)
        {
            return false;
        }
        foreach (var kv in query)
        {
            if (BucketSubresources.ContainsKey(kv.Key))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Maps a bucket-scoped subresource query key + HTTP method to its
    /// specific <see cref="S3Operation"/>. Returns false when no recognised
    /// subresource is present. Recognised subresources with unsupported verbs
    /// map explicitly to <see cref="S3Operation.Unsupported"/>. Order matters
    /// when more than one recognised key is present, mirroring AWS precedence;
    /// in practice SDKs send exactly one.
    /// </summary>
    private static bool TryClassifyBucketSubresource(string method, IQueryCollection query, out S3Operation op)
    {
        op = S3Operation.Unknown;
        if (query.Count == 0) return false;

        // ?uploads belongs to multipart (handled separately).
        if (query.ContainsKey("uploads")) return false;

        return TryClassifySubresource(method, query, BucketSubresourceDefinitions, out op);
    }

    private static bool TryClassifySubresource(
        string method,
        IQueryCollection query,
        SubresourceDefinition[] definitions,
        out S3Operation op)
    {
        op = S3Operation.Unknown;
        if (query.Count == 0)
        {
            return false;
        }

        foreach (var definition in definitions)
        {
            if (!query.ContainsKey(definition.Name))
            {
                continue;
            }

            op = definition.Operations.ForMethod(method);
            return true;
        }

        return false;
    }

    private static FrozenDictionary<string, SubresourceOperations> CreateSubresourceLookup(SubresourceDefinition[] definitions)
    {
        var entries = new KeyValuePair<string, SubresourceOperations>[definitions.Length];
        for (var i = 0; i < definitions.Length; i++)
        {
            entries[i] = new KeyValuePair<string, SubresourceOperations>(
                definitions[i].Name,
                definitions[i].Operations);
        }

        return entries.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private readonly struct SubresourceDefinition
    {
        public SubresourceDefinition(string name, SubresourceOperations operations)
        {
            Name = name;
            Operations = operations;
        }

        public string Name { get; }
        public SubresourceOperations Operations { get; }
    }

    private readonly struct SubresourceOperations
    {
        public static readonly SubresourceOperations UnsupportedOnly = new(
            S3Operation.Unsupported,
            S3Operation.Unsupported,
            S3Operation.Unsupported,
            S3Operation.Unsupported);

        public SubresourceOperations(
            S3Operation get = S3Operation.Unsupported,
            S3Operation put = S3Operation.Unsupported,
            S3Operation post = S3Operation.Unsupported,
            S3Operation delete = S3Operation.Unsupported)
        {
            Get = get;
            Put = put;
            Post = post;
            Delete = delete;
        }

        private S3Operation Get { get; }
        private S3Operation Put { get; }
        private S3Operation Post { get; }
        private S3Operation Delete { get; }

        public S3Operation ForMethod(string method)
        {
            return method switch
            {
                var m when m == HttpMethods.Get => Get,
                var m when m == HttpMethods.Put => Put,
                var m when m == HttpMethods.Post => Post,
                var m when m == HttpMethods.Delete => Delete,
                _ => S3Operation.Unsupported,
            };
        }
    }

    private static (string? bucket, string? key) SplitPath(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty || (path.Length == 1 && path[0] == '/'))
        {
            return (null, null);
        }

        // Strip the leading '/'.
        var trimmed = path[0] == '/' ? path[1..] : path;
        if (trimmed.IsEmpty)
        {
            return (null, null);
        }

        var slash = trimmed.IndexOf('/');
        if (slash < 0)
        {
            return (trimmed.ToString(), null);
        }
        var bucket = trimmed[..slash].ToString();
        var key = trimmed[(slash + 1)..].ToString();
        return (bucket, string.IsNullOrEmpty(key) ? null : key);
    }

    private static bool IsVirtualHostedStyle(string host, out string? bucket)
    {
        bucket = null;
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        // Path-style anchors: host starts with "s3." or "s3-" (e.g.
        // s3.amazonaws.com, s3.us-east-1.amazonaws.com, s3-us-west-2…).
        if (IsPathStyleEndpoint(host))
        {
            return false;
        }

        // Virtual-hosted: {bucket}.s3.… or {bucket}.s3-…
        var dotS3 = FindVirtualHostedMarker(host);
        if (dotS3 > 0)
        {
            bucket = host[..dotS3];
            return true;
        }

        return false;
    }

    private static bool IsPathStyleEndpoint(string host) =>
        host.StartsWith("s3.", StringComparison.OrdinalIgnoreCase) ||
        host.StartsWith("s3-", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("s3", StringComparison.OrdinalIgnoreCase);

    private static int FindVirtualHostedMarker(string host)
    {
        var dotS3 = host.IndexOf(".s3.", StringComparison.OrdinalIgnoreCase);
        return dotS3 >= 0 ? dotS3 : host.IndexOf(".s3-", StringComparison.OrdinalIgnoreCase);
    }
}
