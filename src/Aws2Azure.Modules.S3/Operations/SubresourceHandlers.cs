using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.S3.Errors;
using Aws2Azure.Modules.S3.Internal;
using Aws2Azure.Modules.S3.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.S3.Operations;

/// <summary>
/// Handlers for the Phase-1 Slice-9 long-tail S3 subresources:
/// <list type="bullet">
///   <item>Tagging (object → Azure Blob Index Tags real translation;
///         bucket → opaque container metadata blob).</item>
///   <item>ACL (ownership-only — always reports the configured account as
///         the FULL_CONTROL owner; rejects non-trivial PutAcl requests).</item>
///   <item>Bucket/Object configuration stubs (lifecycle, cors, website,
///         replication, encryption, logging, versioning, requestPayment,
///         object-lock, publicAccessBlock, policy, notification, accelerate,
///         ownershipControls, torrent, restore, legal-hold, retention) that
///         GET → proper AWS error / default-empty body, PUT → NotImplemented,
///         DELETE → 204 idempotent.</item>
/// </list>
/// </summary>
internal static class SubresourceHandlers
{
    private const long MaxTagBodyBytes = 64 * 1024;
    private const int MaxObjectTags = 10;
    private const int MaxBucketTags = 50;
    private const int MaxTagKeyLength = 128;
    private const int MaxTagValueLength = 256;

    // Single opaque metadata key the proxy uses to round-trip the S3
    // <Tagging> envelope on a container. Azure metadata names must match
    // C# identifier rules (no hyphens), so we collapse everything into a
    // single base64-encoded XML blob instead of one metadata name per tag.
    private const string BucketTagsMetadataKey = "aws2azurebuckettags";

    public static async Task HandleAsync(
        HttpContext context, S3RouteResult route, BlobClient blob, CancellationToken ct)
    {
        var op = route.Operation;
        var bucket = route.Bucket;
        var key = route.Key;

        if (string.IsNullOrEmpty(bucket))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidBucketName()).ConfigureAwait(false);
            return;
        }
        if (S3ErrorMapping.ClassifyLookupBucketName(bucket) is { } bucketError)
        {
            await S3ErrorMapping.WriteAsync(context, bucketError).ConfigureAwait(false);
            return;
        }

        // Local-only stubs (ACL, configuration GET/PUT/DELETE that never touch
        // Azure) must still verify the bucket exists; otherwise the proxy
        // would return a 200 ACL or a config-specific 404 (e.g.
        // NoSuchCORSConfiguration) for a bucket that doesn't exist. The
        // tagging ops are *not* in this set because their Azure call already
        // surfaces ContainerNotFound → NoSuchBucket.
        if (RequiresBucketExistenceProbe(op))
        {
            var exists = await BucketExistsAsync(blob, bucket, ct).ConfigureAwait(false);
            if (!exists)
            {
                await S3ErrorMapping.WriteAsync(context, new S3ErrorMapping.Mapping(
                    404, "NoSuchBucket", "The specified bucket does not exist.")).ConfigureAwait(false);
                return;
            }
        }

        // Object-scope stubs (ACL, torrent, restore, legal-hold, retention)
        // must also confirm the blob exists.
        if (RequiresObjectExistenceProbe(op))
        {
            var (ok, err) = await ObjectExistsAsync(blob, bucket, key!, ct).ConfigureAwait(false);
            if (!ok)
            {
                await S3ErrorMapping.WriteAsync(context, err!.Value).ConfigureAwait(false);
                return;
            }
        }

        switch (op)
        {
            // ── Tagging (real translation) ──
            case S3Operation.GetObjectTagging:
                await GetObjectTaggingAsync(context, blob, bucket, key!, ct).ConfigureAwait(false);
                return;
            case S3Operation.PutObjectTagging:
                await PutObjectTaggingAsync(context, blob, bucket, key!, ct).ConfigureAwait(false);
                return;
            case S3Operation.DeleteObjectTagging:
                await DeleteObjectTaggingAsync(context, blob, bucket, key!, ct).ConfigureAwait(false);
                return;
            case S3Operation.GetBucketTagging:
                await GetBucketTaggingAsync(context, blob, bucket, ct).ConfigureAwait(false);
                return;
            case S3Operation.PutBucketTagging:
                await PutBucketTaggingAsync(context, blob, bucket, ct).ConfigureAwait(false);
                return;
            case S3Operation.DeleteBucketTagging:
                await DeleteBucketTaggingAsync(context, blob, bucket, ct).ConfigureAwait(false);
                return;

            // ── ACL (ownership-only stubs) ──
            case S3Operation.GetBucketAcl:
            case S3Operation.GetObjectAcl:
                await GetAclAsync(context, blob).ConfigureAwait(false);
                return;
            case S3Operation.PutBucketAcl:
            case S3Operation.PutObjectAcl:
                await PutAclAsync(context, blob).ConfigureAwait(false);
                return;

            // ── Bucket configuration: GET 404 NoSuch* ──
            case S3Operation.GetBucketLifecycleConfiguration:
                await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchConfiguration(
                    "NoSuchLifecycleConfiguration", "lifecycle configuration")).ConfigureAwait(false);
                return;
            case S3Operation.GetBucketCors:
                await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchConfiguration(
                    "NoSuchCORSConfiguration", "CORS configuration")).ConfigureAwait(false);
                return;
            case S3Operation.GetBucketWebsite:
                await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchConfiguration(
                    "NoSuchWebsiteConfiguration", "website configuration")).ConfigureAwait(false);
                return;
            case S3Operation.GetBucketReplication:
                await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchConfiguration(
                    "ReplicationConfigurationNotFoundError", "replication configuration")).ConfigureAwait(false);
                return;
            case S3Operation.GetBucketEncryption:
                await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchConfiguration(
                    "ServerSideEncryptionConfigurationNotFoundError",
                    "server side encryption configuration")).ConfigureAwait(false);
                return;
            case S3Operation.GetObjectLockConfiguration:
                await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchConfiguration(
                    "ObjectLockConfigurationNotFoundError", "object lock configuration")).ConfigureAwait(false);
                return;
            case S3Operation.GetPublicAccessBlock:
                await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchConfiguration(
                    "NoSuchPublicAccessBlockConfiguration", "public access block configuration")).ConfigureAwait(false);
                return;
            case S3Operation.GetBucketPolicy:
            case S3Operation.GetBucketPolicyStatus:
                await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchConfiguration(
                    "NoSuchBucketPolicy", "bucket policy")).ConfigureAwait(false);
                return;
            case S3Operation.GetBucketOwnershipControls:
                await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchConfiguration(
                    "OwnershipControlsNotFoundError", "ownership controls configuration")).ConfigureAwait(false);
                return;

            // ── Bucket configuration: GET 200 empty/default ──
            case S3Operation.GetBucketLogging:
                await WriteXmlAsync(context, S3XmlWriter.EmptyConfiguration("BucketLoggingStatus")).ConfigureAwait(false);
                return;
            case S3Operation.GetBucketVersioning:
                await WriteXmlAsync(context, S3XmlWriter.EmptyConfiguration("VersioningConfiguration")).ConfigureAwait(false);
                return;
            case S3Operation.GetBucketRequestPayment:
                await WriteXmlAsync(context, S3XmlWriter.RequestPaymentConfigurationDefault()).ConfigureAwait(false);
                return;
            case S3Operation.GetBucketNotificationConfiguration:
                await WriteXmlAsync(context, S3XmlWriter.EmptyConfiguration("NotificationConfiguration")).ConfigureAwait(false);
                return;
            case S3Operation.GetBucketAccelerateConfiguration:
                await WriteXmlAsync(context, S3XmlWriter.EmptyConfiguration("AccelerateConfiguration")).ConfigureAwait(false);
                return;

            // ── Bucket configuration: PUT NotImplemented ──
            case S3Operation.PutBucketLifecycleConfiguration:
            case S3Operation.PutBucketCors:
            case S3Operation.PutBucketWebsite:
            case S3Operation.PutBucketReplication:
            case S3Operation.PutBucketEncryption:
            case S3Operation.PutBucketLogging:
            case S3Operation.PutBucketVersioning:
            case S3Operation.PutBucketRequestPayment:
            case S3Operation.PutObjectLockConfiguration:
            case S3Operation.PutPublicAccessBlock:
            case S3Operation.PutBucketPolicy:
            case S3Operation.PutBucketNotificationConfiguration:
            case S3Operation.PutBucketAccelerateConfiguration:
            case S3Operation.PutBucketOwnershipControls:
                await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NotImplemented(op)).ConfigureAwait(false);
                return;

            // ── Bucket configuration: DELETE 204 idempotent ──
            case S3Operation.DeleteBucketLifecycle:
            case S3Operation.DeleteBucketCors:
            case S3Operation.DeleteBucketWebsite:
            case S3Operation.DeleteBucketReplication:
            case S3Operation.DeleteBucketEncryption:
            case S3Operation.DeletePublicAccessBlock:
            case S3Operation.DeleteBucketPolicy:
            case S3Operation.DeleteBucketOwnershipControls:
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;

            // ── Object-scoped stubs ──
            case S3Operation.GetObjectTorrent:
            case S3Operation.RestoreObject:
            case S3Operation.GetObjectLegalHold:
            case S3Operation.PutObjectLegalHold:
            case S3Operation.GetObjectRetention:
            case S3Operation.PutObjectRetention:
                await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NotImplemented(op)).ConfigureAwait(false);
                return;

            default:
                await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NotImplemented(op)).ConfigureAwait(false);
                return;
        }
    }

    // ─── Object tagging ───────────────────────────────────────────────

    private static async Task GetObjectTaggingAsync(
        HttpContext context, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        if (!S3ObjectKey.IsValid(key))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidObjectKey()).ConfigureAwait(false);
            return;
        }

        using var response = await blob.GetBlobTagsAsync(bucket, key, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.FromAzure(response, S3Operation.GetObjectTagging)).ConfigureAwait(false);
            return;
        }

        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var tags = SafeParseTagSet(xml) ?? Array.Empty<S3XmlWriter.Tag>();
        await WriteXmlAsync(context, S3XmlWriter.Tagging(tags)).ConfigureAwait(false);
    }

    private static async Task PutObjectTaggingAsync(
        HttpContext context, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        if (!S3ObjectKey.IsValid(key))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidObjectKey()).ConfigureAwait(false);
            return;
        }

        var (tags, err) = await ReadTagSetAsync(context, MaxObjectTags, ct).ConfigureAwait(false);
        if (err is not null)
        {
            await S3ErrorMapping.WriteAsync(context, err.Value).ConfigureAwait(false);
            return;
        }

        var body = S3XmlWriter.AzureBlobTagsBody(tags!);
        using var response = await blob.PutBlobTagsAsync(bucket, key, body, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.FromAzure(response, S3Operation.PutObjectTagging)).ConfigureAwait(false);
            return;
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    private static async Task DeleteObjectTaggingAsync(
        HttpContext context, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        if (!S3ObjectKey.IsValid(key))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidObjectKey()).ConfigureAwait(false);
            return;
        }

        var body = S3XmlWriter.AzureBlobTagsBody(Array.Empty<S3XmlWriter.Tag>());
        using var response = await blob.PutBlobTagsAsync(bucket, key, body, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.FromAzure(response, S3Operation.DeleteObjectTagging)).ConfigureAwait(false);
            return;
        }
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    // ─── Bucket tagging (container metadata blob) ─────────────────────

    private static async Task GetBucketTaggingAsync(
        HttpContext context, BlobClient blob, string bucket, CancellationToken ct)
    {
        using var response = await blob.GetContainerMetadataAsync(bucket, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.FromAzure(response, S3Operation.GetBucketTagging)).ConfigureAwait(false);
            return;
        }

        if (!response.Headers.TryGetValues("x-ms-meta-" + BucketTagsMetadataKey, out var values))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchConfiguration(
                "NoSuchTagSet", "TagSet")).ConfigureAwait(false);
            return;
        }

        string? b64 = null;
        foreach (var v in values) { if (!string.IsNullOrEmpty(v)) { b64 = v; break; } }
        if (string.IsNullOrEmpty(b64))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchConfiguration("NoSuchTagSet", "TagSet")).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<S3XmlWriter.Tag> tags;
        try
        {
            var xml = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            tags = AzureBlobXmlReader.ParseTagSet(xml) ?? Array.Empty<S3XmlWriter.Tag>();
        }
        catch
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchConfiguration("NoSuchTagSet", "TagSet")).ConfigureAwait(false);
            return;
        }

        await WriteXmlAsync(context, S3XmlWriter.Tagging(tags)).ConfigureAwait(false);
    }

    private static async Task PutBucketTaggingAsync(
        HttpContext context, BlobClient blob, string bucket, CancellationToken ct)
    {
        var (tags, err) = await ReadTagSetAsync(context, MaxBucketTags, ct).ConfigureAwait(false);
        if (err is not null)
        {
            await S3ErrorMapping.WriteAsync(context, err.Value).ConfigureAwait(false);
            return;
        }

        // Round-trip the tag set as base64-encoded S3 <Tagging> XML stored
        // under a single Azure metadata key. Atomic Put/Delete, no key-name
        // mangling, and unaffected by the strict Azure metadata-name rules.
        var xml = S3XmlWriter.Tagging(tags!);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));

        // Azure SetContainerMetadata REPLACES all metadata. Read the
        // existing metadata first and merge our tag entry so unrelated
        // metadata set by Azure-side tooling is preserved.
        var metadata = await ReadExistingMetadataAsync(blob, bucket, S3Operation.PutBucketTagging, context, ct).ConfigureAwait(false);
        if (metadata is null) return; // error already written
        metadata[BucketTagsMetadataKey] = b64;

        using var response = await blob.SetContainerMetadataAsync(bucket, metadata, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.FromAzure(response, S3Operation.PutBucketTagging)).ConfigureAwait(false);
            return;
        }
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task DeleteBucketTaggingAsync(
        HttpContext context, BlobClient blob, string bucket, CancellationToken ct)
    {
        // Read-merge-write: drop our tag entry but keep any unrelated
        // container metadata intact.
        var metadata = await ReadExistingMetadataAsync(blob, bucket, S3Operation.DeleteBucketTagging, context, ct).ConfigureAwait(false);
        if (metadata is null) return;
        metadata.Remove(BucketTagsMetadataKey);

        using var response = await blob.SetContainerMetadataAsync(bucket, metadata, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.FromAzure(response, S3Operation.DeleteBucketTagging)).ConfigureAwait(false);
            return;
        }
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    // ─── ACL (ownership-only) ─────────────────────────────────────────

    private static async Task GetAclAsync(HttpContext context, BlobClient blob)
    {
        var owner = ComputeOwner(blob.AccountName);
        await WriteXmlAsync(context, S3XmlWriter.AccessControlPolicy(owner)).ConfigureAwait(false);
    }

    private static async Task PutAclAsync(HttpContext context, BlobClient blob)
    {
        // Accept only canned 'private' (= owner FULL_CONTROL). Any explicit
        // grant headers or any other canned ACL is rejected with
        // AccessControlListNotSupported to match the BucketOwnerEnforced
        // ownership behaviour.
        var canned = HeaderForwarding.ReadFirstHeader(context.Request, "x-amz-acl");
        var hasGrantHeader =
            HasHeader(context.Request, "x-amz-grant-read") ||
            HasHeader(context.Request, "x-amz-grant-write") ||
            HasHeader(context.Request, "x-amz-grant-read-acp") ||
            HasHeader(context.Request, "x-amz-grant-write-acp") ||
            HasHeader(context.Request, "x-amz-grant-full-control");

        var owner = ComputeOwner(blob.AccountName);
        var bodyOk = await ValidateOwnerOnlyBodyAsync(context, owner.Id, context.RequestAborted).ConfigureAwait(false);

        var ok = !hasGrantHeader
            && (string.IsNullOrEmpty(canned) || string.Equals(canned, "private", StringComparison.OrdinalIgnoreCase))
            && bodyOk;

        if (!ok)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.AccessControlListNotSupported()).ConfigureAwait(false);
            return;
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    private static async Task<bool> ValidateOwnerOnlyBodyAsync(HttpContext context, string expectedOwnerId, CancellationToken ct)
    {
        if ((context.Request.ContentLength ?? 0) == 0 && !context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            return true;
        }

        string xml;
        try
        {
            using var sr = new StreamReader(context.Request.Body, Encoding.UTF8, false, 8 * 1024, leaveOpen: true);
            xml = await sr.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(xml))
        {
            return true;
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = true,
                IgnoreComments = true,
            });
            var grants = 0;
            var sawNonCanonical = false;
            var sawNonFullControl = false;
            var sawNonOwnerId = false;
            var sawGranteeInGrant = false;
            var sawIdInGrantee = false;
            string? currentGranteeId = null;
            var insideGrant = false;
            var insideGrantee = false;

            if (!reader.Read()) return false;
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.LocalName)
                    {
                        case "Grant":
                            insideGrant = true;
                            sawGranteeInGrant = false;
                            sawIdInGrantee = false;
                            currentGranteeId = null;
                            grants++;
                            break;
                        case "Grantee" when insideGrant:
                            insideGrantee = true;
                            sawGranteeInGrant = true;
                            var t = reader.GetAttribute("type", "http://www.w3.org/2001/XMLSchema-instance");
                            if (!string.Equals(t, "CanonicalUser", StringComparison.Ordinal)) sawNonCanonical = true;
                            break;
                        case "ID" when insideGrantee:
                            currentGranteeId = reader.ReadElementContentAsString();
                            sawIdInGrantee = true;
                            if (!string.Equals(currentGranteeId, expectedOwnerId, StringComparison.Ordinal)) sawNonOwnerId = true;
                            continue;
                        case "Permission" when insideGrant:
                            var p = reader.ReadElementContentAsString();
                            if (!string.Equals(p, "FULL_CONTROL", StringComparison.Ordinal)) sawNonFullControl = true;
                            continue;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (reader.LocalName == "Grantee") insideGrantee = false;
                    else if (reader.LocalName == "Grant")
                    {
                        insideGrant = false;
                        // Every Grant must carry a Grantee with an ID equal to the owner.
                        if (!sawGranteeInGrant || !sawIdInGrantee) sawNonOwnerId = true;
                    }
                }
                reader.Read();
            }
            return grants <= 1 && !sawNonCanonical && !sawNonFullControl && !sawNonOwnerId;
        }
        catch
        {
            return false;
        }
    }

    private static S3XmlWriter.OwnerInfo ComputeOwner(string accountName)
    {
        // Stable canonical-user ID derived from the Azure account name so
        // it round-trips across processes. SHA-256 → hex, lowercase.
        Span<byte> hash = stackalloc byte[32];
        var ok = SHA256.HashData(Encoding.UTF8.GetBytes(accountName), hash);
        _ = ok; // discard length
        var sb = new StringBuilder(64);
        for (var i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return new S3XmlWriter.OwnerInfo(sb.ToString(), accountName);
    }

    // ─── Shared helpers ───────────────────────────────────────────────

    private static async Task<(IReadOnlyList<S3XmlWriter.Tag>? tags, S3ErrorMapping.Mapping? err)>
        ReadTagSetAsync(HttpContext context, int maxTags, CancellationToken ct)
    {
        var contentLength = context.Request.ContentLength ?? 0;
        if (contentLength > MaxTagBodyBytes)
        {
            return (null, new S3ErrorMapping.Mapping(413, "EntityTooLarge",
                $"Tagging request body exceeds the {MaxTagBodyBytes}-byte limit."));
        }

        // Bounded read: cap at MaxTagBodyBytes + 1 so chunked / missing
        // Content-Length bodies can't bypass the limit.
        byte[] bodyBytes;
        try
        {
            using var buffered = new MemoryStream();
            var buffer = new byte[8 * 1024];
            long total = 0;
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxTagBodyBytes)
                {
                    return (null, new S3ErrorMapping.Mapping(413, "EntityTooLarge",
                        $"Tagging request body exceeds the {MaxTagBodyBytes}-byte limit."));
                }
                buffered.Write(buffer, 0, read);
            }
            bodyBytes = buffered.ToArray();
        }
        catch (Exception ex)
        {
            return (null, S3ErrorMapping.MalformedXml(ex.Message));
        }

        // Content-MD5 enforcement (AWS documents it as required for
        // PutBucketTagging and PutObjectTagging). Validate only when
        // present so SDKs that send x-amz-sdk-checksum-* aren't rejected.
        var contentMd5 = HeaderForwarding.ReadFirstHeader(context.Request, "Content-MD5");
        if (!string.IsNullOrEmpty(contentMd5))
        {
            var digest = System.Security.Cryptography.MD5.HashData(bodyBytes);
            var actual = Convert.ToBase64String(digest);
            if (!string.Equals(actual, contentMd5.Trim(), StringComparison.Ordinal))
            {
                return (null, new S3ErrorMapping.Mapping(400, "BadDigest",
                    "The Content-MD5 you specified did not match what we received."));
            }
        }

        string xml;
        try
        {
            xml = Encoding.UTF8.GetString(bodyBytes);
        }
        catch (Exception ex)
        {
            return (null, S3ErrorMapping.MalformedXml(ex.Message));
        }

        IReadOnlyList<S3XmlWriter.Tag>? tags;
        try
        {
            tags = AzureBlobXmlReader.ParseTagSet(xml);
        }
        catch (XmlException ex)
        {
            return (null, S3ErrorMapping.MalformedXml(ex.Message));
        }
        if (tags is null)
        {
            return (null, S3ErrorMapping.MalformedXml("Tagging payload is missing a <TagSet> element."));
        }
        if (tags.Count > maxTags)
        {
            return (null, new S3ErrorMapping.Mapping(400, "BadRequest",
                $"Object tag count exceeds the allowed maximum of {maxTags}."));
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tags)
        {
            if (string.IsNullOrEmpty(t.Key) || t.Key.Length > MaxTagKeyLength
                || (t.Value?.Length ?? 0) > MaxTagValueLength)
            {
                return (null, S3ErrorMapping.InvalidArgument(
                    $"Invalid tag key/value (key length 1..{MaxTagKeyLength}, value length 0..{MaxTagValueLength})."));
            }
            if (!seen.Add(t.Key))
            {
                return (null, S3ErrorMapping.InvalidArgument(
                    $"Duplicate tag key '{t.Key}' in tag set."));
            }
        }
        return (tags, null);
    }

    private static IReadOnlyList<S3XmlWriter.Tag>? SafeParseTagSet(string xml)
    {
        try { return AzureBlobXmlReader.ParseTagSet(xml); }
        catch { return null; }
    }

    private static bool HasHeader(HttpRequest request, string name) =>
        request.Headers.ContainsKey(name);

    /// <summary>
    /// Reads the current container metadata so the caller can apply a
    /// surgical change (add/remove a single entry) and write back the full
    /// set without clobbering metadata set by external Azure-side tooling.
    /// On Azure failure, writes the S3 error and returns null.
    /// </summary>
    private static async Task<Dictionary<string, string>?> ReadExistingMetadataAsync(
        BlobClient blob, string bucket, S3Operation op, HttpContext context, CancellationToken ct)
    {
        using var response = await blob.GetContainerPropertiesAsync(bucket, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.FromAzure(response, op)).ConfigureAwait(false);
            return null;
        }
        var existing = BlobClient.ReadContainerMetadata(response);
        return new Dictionary<string, string>(existing, StringComparer.Ordinal);
    }

    private static async Task WriteXmlAsync(HttpContext context, string xml)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/xml";
        var bytes = Encoding.UTF8.GetBytes(xml);
        context.Response.ContentLength = bytes.LongLength;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    // ── Existence probes ─────────────────────────────────────────────

    /// <summary>
    /// Local-only stubs that don't otherwise touch Azure. The handler must
    /// confirm the bucket exists before returning a synthetic 200/404 — a
    /// nonexistent bucket should always surface as NoSuchBucket, not as a
    /// fake "no configuration" answer.
    /// </summary>
    private static bool RequiresBucketExistenceProbe(S3Operation op) => op is
        S3Operation.GetBucketAcl or S3Operation.PutBucketAcl or
        S3Operation.GetBucketLifecycleConfiguration or S3Operation.PutBucketLifecycleConfiguration or S3Operation.DeleteBucketLifecycle or
        S3Operation.GetBucketCors or S3Operation.PutBucketCors or S3Operation.DeleteBucketCors or
        S3Operation.GetBucketWebsite or S3Operation.PutBucketWebsite or S3Operation.DeleteBucketWebsite or
        S3Operation.GetBucketReplication or S3Operation.PutBucketReplication or S3Operation.DeleteBucketReplication or
        S3Operation.GetBucketEncryption or S3Operation.PutBucketEncryption or S3Operation.DeleteBucketEncryption or
        S3Operation.GetBucketLogging or S3Operation.PutBucketLogging or
        S3Operation.GetBucketVersioning or S3Operation.PutBucketVersioning or
        S3Operation.GetBucketRequestPayment or S3Operation.PutBucketRequestPayment or
        S3Operation.GetObjectLockConfiguration or S3Operation.PutObjectLockConfiguration or
        S3Operation.GetPublicAccessBlock or S3Operation.PutPublicAccessBlock or S3Operation.DeletePublicAccessBlock or
        S3Operation.GetBucketPolicy or S3Operation.PutBucketPolicy or S3Operation.DeleteBucketPolicy or
        S3Operation.GetBucketPolicyStatus or
        S3Operation.GetBucketNotificationConfiguration or S3Operation.PutBucketNotificationConfiguration or
        S3Operation.GetBucketAccelerateConfiguration or S3Operation.PutBucketAccelerateConfiguration or
        S3Operation.GetBucketOwnershipControls or S3Operation.PutBucketOwnershipControls or S3Operation.DeleteBucketOwnershipControls;

    private static bool RequiresObjectExistenceProbe(S3Operation op) => op is
        S3Operation.GetObjectAcl or S3Operation.PutObjectAcl or
        S3Operation.GetObjectTorrent or S3Operation.RestoreObject or
        S3Operation.GetObjectRetention or S3Operation.PutObjectRetention or
        S3Operation.GetObjectLegalHold or S3Operation.PutObjectLegalHold;

    private static async Task<bool> BucketExistsAsync(BlobClient blob, string bucket, CancellationToken ct)
    {
        using var resp = await blob.GetContainerPropertiesAsync(bucket, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }

    private static async Task<(bool ok, S3ErrorMapping.Mapping? err)> ObjectExistsAsync(
        BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key) || !S3ObjectKey.IsValid(key))
        {
            return (false, S3ErrorMapping.InvalidObjectKey());
        }
        var uri = blob.BuildBlobUri(bucket, key);
        using var req = new HttpRequestMessage(HttpMethod.Head, uri);
        using var resp = await blob.SendBlobRequestAsync(req, ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, S3ErrorMapping.FromAzure(resp, S3Operation.HeadObject));
    }
}
