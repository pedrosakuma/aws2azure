using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Unicode;
using System.Xml;
using Aws2Azure.Core.Azure;
using Aws2Azure.Modules.S3.Errors;
using Aws2Azure.Modules.S3.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Aws2Azure.Modules.S3.Internal;

internal sealed class MultipartUploadStateStore
{
    private const string Magic = "A2MP1";
    private const int MaxRecordBytes = 16 * 1024;
    private const int CreateCleanupDeleteLimit = 32;
    private const int ListCleanupDeleteLimit = 256;
    private const int MaxPageSize = 1000;
    private const int LeaseDurationSeconds = 60;
    private const string KeyMetadataName = "aws2azurekey";
    private const string UploadIdMetadataName = "aws2azureuploadid";
    private const string InitiatedMsMetadataName = "aws2azureinitiatedms";
    private const string ContainerGenerationMetadataName = "aws2azurecontaineretag";
    private const string ContainerGenerationMarkerMetadataName = "aws2azuregeneration";
    private const string StateContainerOwnerMetadataName = "aws2azureowner";
    private const string StateContainerOwnerMetadataValue = "multipart-state-v1";
    private const int ContainerGenerationMutationMaxAttempts = 4;

    private readonly BlobClient _blob;

    public MultipartUploadStateStore(BlobClient blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        _blob = blob;
    }

    public readonly record struct MultipartUploadStateSummary(
        string Bucket,
        string Key,
        string UploadId,
        DateTimeOffset Initiated,
        string ContainerGeneration,
        string RecordName);

    public readonly record struct MultipartUploadStateRecord(
        MultipartUploadStateSummary Summary,
        CapturedHeaders Headers);

    public enum ResultKind
    {
        Success,
        NotFound,
        Conflict,
        BucketMissing,
        Error,
    }

    public readonly record struct ReadResult(
        ResultKind Kind,
        MultipartUploadStateRecord? Record,
        S3ErrorMapping.Mapping? Error);

    public readonly record struct SummaryResult(
        ResultKind Kind,
        MultipartUploadStateSummary? Summary,
        S3ErrorMapping.Mapping? Error);

    public readonly record struct AcquireResult(
        ResultKind Kind,
        MultipartUploadStateRecord? Record,
        string? LeaseId,
        S3ErrorMapping.Mapping? Error);

    public readonly record struct ListResult(
        ResultKind Kind,
        IReadOnlyList<S3XmlWriter.ListedUpload> Uploads,
        IReadOnlyList<string> CommonPrefixes,
        bool IsTruncated,
        string? NextKeyMarker,
        string? NextUploadIdMarker,
        S3ErrorMapping.Mapping? Error);

    public readonly record struct VerificationResult(
        ResultKind Kind,
        S3ErrorMapping.Mapping? Error);

    public readonly record struct GenerationResult(
        ResultKind Kind,
        string? Generation,
        string? ETag,
        S3ErrorMapping.Mapping? Error);

    private readonly record struct VisibleSummariesResult(
        ResultKind Kind,
        List<MultipartUploadStateSummary> Summaries,
        S3ErrorMapping.Mapping? Error);

    private readonly record struct LegacyStateContainerCheckResult(
        bool IsLegacyStateContainer,
        S3ErrorMapping.Mapping? Error);

    public async Task<S3ErrorMapping.Mapping?> CreateAsync(
        string bucket,
        string key,
        string uploadId,
        DateTimeOffset initiated,
        string containerGeneration,
        HttpRequest request,
        IReadOnlyList<S3XmlWriter.Tag> tags,
        CancellationToken cancellationToken)
    {
        if (await EnsureStateContainerExistsAsync(cancellationToken).ConfigureAwait(false) is { } ensureError)
        {
            return ensureError;
        }
        await CleanupExpiredRecordsAsync(bucket, CreateCleanupDeleteLimit, cancellationToken).ConfigureAwait(false);

        var body = SerializeCapturedHeaders(request, tags);
        var recordName = BuildRecordName(bucket, initiated, uploadId);
        using var put = new HttpRequestMessage(HttpMethod.Put, _blob.BuildBlobUri(_blob.MultipartStateContainerName, recordName))
        {
            Content = new ByteArrayContent(body),
        };
        put.Options.Set(AzureHttpClient.NoRetryOption, true);
        put.Content.Headers.ContentLength = body.LongLength;
        put.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        put.Headers.TryAddWithoutValidation("x-ms-meta-" + KeyMetadataName, EncodeMetadataString(key));
        put.Headers.TryAddWithoutValidation("x-ms-meta-" + UploadIdMetadataName, uploadId);
        put.Headers.TryAddWithoutValidation("x-ms-meta-" + InitiatedMsMetadataName, initiated.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        put.Headers.TryAddWithoutValidation("x-ms-meta-" + ContainerGenerationMetadataName, containerGeneration);

        using var response = await _blob.SendBlobRequestAsync(put, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? null
            : S3ErrorMapping.FromAzure(response, S3Operation.CreateMultipartUpload);
    }

    public async Task<SummaryResult> ReadSummaryAsync(
        string bucket,
        string uploadId,
        CancellationToken cancellationToken)
    {
        if (!TryBuildRecordName(bucket, uploadId, out var recordName))
        {
            return new SummaryResult(ResultKind.NotFound, null, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Head, _blob.BuildBlobUri(_blob.MultipartStateContainerName, recordName));
        using var response = await _blob.SendBlobRequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new SummaryResult(ResultKind.NotFound, null, null);
        }
        if (!response.IsSuccessStatusCode)
        {
            return new SummaryResult(ResultKind.Error, null, S3ErrorMapping.FromAzure(response, S3Operation.UploadPart));
        }

        if (!TryReadSummary(bucket, recordName, response, out var summary))
        {
            return new SummaryResult(ResultKind.NotFound, null, null);
        }

        var generation = await VerifyContainerGenerationAsync(bucket, summary.ContainerGeneration, cancellationToken).ConfigureAwait(false);
        return generation.Kind switch
        {
            ResultKind.Success => new SummaryResult(ResultKind.Success, summary, null),
            ResultKind.BucketMissing => new SummaryResult(ResultKind.BucketMissing, null, null),
            ResultKind.NotFound => new SummaryResult(ResultKind.NotFound, null, null),
            _ => new SummaryResult(ResultKind.Error, null, generation.Error)
        };
    }

    public async Task<ReadResult> ReadAsync(
        string bucket,
        string uploadId,
        CancellationToken cancellationToken)
    {
        if (!TryBuildRecordName(bucket, uploadId, out var recordName))
        {
            return new ReadResult(ResultKind.NotFound, null, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _blob.BuildBlobUri(_blob.MultipartStateContainerName, recordName));
        using var response = await _blob.SendBlobRequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ReadResult(ResultKind.NotFound, null, null);
        }
        if (!response.IsSuccessStatusCode)
        {
            return new ReadResult(ResultKind.Error, null, S3ErrorMapping.FromAzure(response, S3Operation.CompleteMultipartUpload));
        }

        if (!TryReadSummary(bucket, recordName, response, out var summary))
        {
            return new ReadResult(ResultKind.NotFound, null, null);
        }

        var generation = await VerifyContainerGenerationAsync(bucket, summary.ContainerGeneration, cancellationToken).ConfigureAwait(false);
        if (generation.Kind != ResultKind.Success)
        {
            return generation.Kind == ResultKind.Error
                ? new ReadResult(ResultKind.Error, null, generation.Error)
                : new ReadResult(generation.Kind == ResultKind.BucketMissing ? ResultKind.BucketMissing : ResultKind.NotFound, null, null);
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!TryDeserializeCapturedHeaders(body, out var headers))
        {
            return new ReadResult(ResultKind.NotFound, null, null);
        }

        return new ReadResult(ResultKind.Success, new MultipartUploadStateRecord(summary, headers), null);
    }

    public async Task<AcquireResult> AcquireAsync(
        string bucket,
        string uploadId,
        CancellationToken cancellationToken)
    {
        var read = await ReadAsync(bucket, uploadId, cancellationToken).ConfigureAwait(false);
        if (read.Kind != ResultKind.Success || read.Record is null)
        {
            return new AcquireResult(read.Kind, null, null, read.Error);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            _blob.BuildBlobUri(_blob.MultipartStateContainerName, read.Record.Value.Summary.RecordName, "?comp=lease"));
        request.Options.Set(AzureHttpClient.NoRetryOption, true);
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        request.Content.Headers.ContentLength = 0;
        request.Headers.TryAddWithoutValidation("x-ms-lease-action", "acquire");
        request.Headers.TryAddWithoutValidation("x-ms-lease-duration", LeaseDurationSeconds.ToString(CultureInfo.InvariantCulture));

        using var response = await _blob.SendBlobRequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return new AcquireResult(
                ResultKind.Success,
                read.Record.Value,
                ReadHeader(response, "x-ms-lease-id"),
                null);
        }

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return new AcquireResult(ResultKind.NotFound, null, null, null);
        }

        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return new AcquireResult(ResultKind.Conflict, null, null, null);
        }

        return new AcquireResult(ResultKind.Error, null, null, S3ErrorMapping.FromAzure(response, S3Operation.AbortMultipartUpload));
    }

    public async Task<S3ErrorMapping.Mapping?> ReleaseAsync(
        string bucket,
        string uploadId,
        string leaseId,
        CancellationToken cancellationToken)
    {
        return await SendLeaseRequestAsync(bucket, uploadId, leaseId, "release", S3Operation.AbortMultipartUpload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<S3ErrorMapping.Mapping?> DeleteLeasedAsync(
        string bucket,
        string uploadId,
        string leaseId,
        CancellationToken cancellationToken)
    {
        if (!TryBuildRecordName(bucket, uploadId, out var recordName))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, _blob.BuildBlobUri(_blob.MultipartStateContainerName, recordName));
        request.Options.Set(AzureHttpClient.NoRetryOption, true);
        request.Headers.TryAddWithoutValidation("x-ms-lease-id", leaseId);
        using var response = await _blob.SendBlobRequestAsync(request, cancellationToken).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : response.IsSuccessStatusCode
                ? null
                : S3ErrorMapping.FromAzure(response, S3Operation.AbortMultipartUpload);
    }

    public async Task<S3ErrorMapping.Mapping?> InvalidateLeasedAsync(
        MultipartUploadStateSummary summary,
        string leaseId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            _blob.BuildBlobUri(_blob.MultipartStateContainerName, summary.RecordName, "?comp=metadata"));
        request.Options.Set(AzureHttpClient.NoRetryOption, true);
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        request.Content.Headers.ContentLength = 0;
        request.Headers.TryAddWithoutValidation("x-ms-lease-id", leaseId);
        request.Headers.TryAddWithoutValidation("x-ms-meta-" + KeyMetadataName, EncodeMetadataString(summary.Key));
        request.Headers.TryAddWithoutValidation("x-ms-meta-" + UploadIdMetadataName, summary.UploadId);
        request.Headers.TryAddWithoutValidation("x-ms-meta-" + InitiatedMsMetadataName, summary.Initiated.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("x-ms-meta-" + ContainerGenerationMetadataName, "retired-" + Guid.NewGuid().ToString("N"));
        using var response = await _blob.SendBlobRequestAsync(request, cancellationToken).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : response.IsSuccessStatusCode
                ? null
                : S3ErrorMapping.FromAzure(response, S3Operation.CompleteMultipartUpload);
    }

    public async Task<S3ErrorMapping.Mapping?> DeleteAsync(
        string bucket,
        string uploadId,
        CancellationToken cancellationToken)
    {
        if (!TryBuildRecordName(bucket, uploadId, out var recordName))
        {
            return null;
        }

        return await DeleteRecordByNameAsync(recordName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ListResult> ListAsync(
        string bucket,
        string? keyMarker,
        string? uploadIdMarker,
        string? prefix,
        string? delimiter,
        int maxUploads,
        CancellationToken cancellationToken)
    {
        await CleanupExpiredRecordsAsync(bucket, ListCleanupDeleteLimit, cancellationToken).ConfigureAwait(false);

        var currentGeneration = await ReadCurrentContainerGenerationAsync(bucket, cancellationToken).ConfigureAwait(false);
        if (currentGeneration.Kind != ResultKind.Success || currentGeneration.Generation is null)
        {
            return new ListResult(currentGeneration.Kind, Array.Empty<S3XmlWriter.ListedUpload>(), Array.Empty<string>(), false, null, null, currentGeneration.Error);
        }

        var visibleSummaries = await ListVisibleSummariesAsync(bucket, currentGeneration.Generation, currentGeneration.ETag, cancellationToken).ConfigureAwait(false);
        if (visibleSummaries.Kind != ResultKind.Success)
        {
            return new ListResult(visibleSummaries.Kind, Array.Empty<S3XmlWriter.ListedUpload>(), Array.Empty<string>(), false, null, null, visibleSummaries.Error);
        }

        var summaries = visibleSummaries.Summaries;
        summaries.Sort(static (left, right) =>
        {
            var keyCompare = CompareUtf8(left.Key, right.Key);
            if (keyCompare != 0)
            {
                return keyCompare;
            }

            var initiatedCompare = left.Initiated.CompareTo(right.Initiated);
            if (initiatedCompare != 0)
            {
                return initiatedCompare;
            }

            return string.CompareOrdinal(left.UploadId, right.UploadId);
        });

        var uploads = new List<S3XmlWriter.ListedUpload>(Math.Min(maxUploads, 64));
        var commonPrefixes = new List<string>();
        var seenPrefixes = new HashSet<string>(StringComparer.Ordinal);
        string? nextKeyMarker = null;
        string? nextUploadIdMarker = null;
        var truncated = false;
        var markerKey = string.IsNullOrEmpty(keyMarker) ? null : keyMarker;
        var markerUploadId = string.IsNullOrEmpty(uploadIdMarker) ? null : uploadIdMarker;
        var prefixFilter = string.IsNullOrEmpty(prefix) ? null : prefix;
        var delimiterValue = string.IsNullOrEmpty(delimiter) ? null : delimiter;

        for (var i = 0; i < summaries.Count; i++)
        {
            var summary = summaries[i];
            if (!IsAfterMarker(summary, markerKey, markerUploadId))
            {
                continue;
            }
            if (prefixFilter is not null && !summary.Key.StartsWith(prefixFilter, StringComparison.Ordinal))
            {
                continue;
            }

            if (delimiterValue is not null && TryGetCommonPrefix(summary.Key, prefixFilter, delimiterValue, out var commonPrefix))
            {
                var lastIndex = i;
                while (lastIndex + 1 < summaries.Count
                    && summaries[lastIndex + 1].Key.StartsWith(commonPrefix, StringComparison.Ordinal))
                {
                    lastIndex++;
                }

                if (!IsAfterKeyMarker(commonPrefix, markerKey))
                {
                    i = lastIndex;
                    continue;
                }

                if (seenPrefixes.Add(commonPrefix))
                {
                    commonPrefixes.Add(commonPrefix);
                    if (uploads.Count + commonPrefixes.Count == maxUploads && HasFurtherMatches(summaries, lastIndex + 1, markerKey, markerUploadId, prefixFilter))
                    {
                        truncated = true;
                        nextKeyMarker = summaries[lastIndex].Key;
                        nextUploadIdMarker = summaries[lastIndex].UploadId;
                        break;
                    }
                }

                i = lastIndex;
                continue;
            }

            uploads.Add(new S3XmlWriter.ListedUpload(summary.Key, summary.UploadId, summary.Initiated));
            if (uploads.Count + commonPrefixes.Count == maxUploads && HasFurtherMatches(summaries, i + 1, markerKey, markerUploadId, prefixFilter))
            {
                truncated = true;
                nextKeyMarker = summary.Key;
                nextUploadIdMarker = summary.UploadId;
                break;
            }
        }

        return new ListResult(ResultKind.Success, uploads, commonPrefixes, truncated, nextKeyMarker, nextUploadIdMarker, null);
    }

    public async Task DeleteBucketRecordsAsync(string bucket, string onlyGeneration, CancellationToken cancellationToken)
    {
        await DeleteBucketRecordsCoreAsync(
            bucket,
            static (recordGeneration, arg) => string.Equals(recordGeneration, arg, StringComparison.Ordinal),
            onlyGeneration,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteStaleBucketRecordsAsync(string bucket, string exceptGeneration, CancellationToken cancellationToken)
    {
        await DeleteBucketRecordsCoreAsync(
            bucket,
            static (recordGeneration, arg) => !string.Equals(recordGeneration, arg, StringComparison.Ordinal),
            exceptGeneration,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VerificationResult> VerifyContainerGenerationAsync(
        string bucket,
        string expectedGeneration,
        CancellationToken cancellationToken)
    {
        var current = await ReadCurrentContainerGenerationAsync(bucket, cancellationToken).ConfigureAwait(false);
        if (current.Kind != ResultKind.Success)
        {
            return new VerificationResult(current.Kind, current.Error);
        }

        var actual = IsLegacyContainerGeneration(expectedGeneration)
            ? current.ETag
            : current.Generation;
        if (string.IsNullOrEmpty(actual))
        {
            return new VerificationResult(ResultKind.Error, new S3ErrorMapping.Mapping(
                StatusCodes.Status500InternalServerError,
                "InternalError",
                "aws2azure: Azure container generation marker is missing."));
        }

        return string.Equals(actual, expectedGeneration, StringComparison.Ordinal)
            ? new VerificationResult(ResultKind.Success, null)
            : new VerificationResult(ResultKind.NotFound, null);
    }

    public Task<GenerationResult> ReadOrCreateContainerGenerationAsync(string bucket, CancellationToken cancellationToken) =>
        ReadCurrentContainerGenerationCoreAsync(bucket, createIfMissing: true, cancellationToken);

    public Task<GenerationResult> ReadContainerGenerationAsync(string bucket, CancellationToken cancellationToken) =>
        ReadCurrentContainerGenerationAsync(bucket, cancellationToken);

    public static int CompareUtf8(string left, string right)
    {
        var leftRunes = left.EnumerateRunes();
        var rightRunes = right.EnumerateRunes();
        while (true)
        {
            var leftHas = leftRunes.MoveNext();
            var rightHas = rightRunes.MoveNext();
            if (!leftHas || !rightHas)
            {
                if (leftHas == rightHas)
                {
                    return 0;
                }

                return leftHas ? 1 : -1;
            }

            var compare = leftRunes.Current.Value.CompareTo(rightRunes.Current.Value);
            if (compare != 0)
            {
                return compare;
            }
        }
    }

    private async Task<S3ErrorMapping.Mapping?> EnsureStateContainerExistsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(_blob.BuildContainerUri(_blob.MultipartStateContainerName).AbsoluteUri + "?restype=container"));
        request.Options.Set(AzureHttpClient.NoRetryOption, true);
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        request.Content.Headers.ContentLength = 0;
        request.Headers.TryAddWithoutValidation("x-ms-meta-" + StateContainerOwnerMetadataName, StateContainerOwnerMetadataValue);
        using var response = await _blob.SendBlobRequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict)
        {
            return S3ErrorMapping.FromAzure(response, S3Operation.CreateMultipartUpload);
        }

        using var properties = await _blob.GetContainerPropertiesAsync(_blob.MultipartStateContainerName, cancellationToken).ConfigureAwait(false);
        if (!properties.IsSuccessStatusCode)
        {
            return S3ErrorMapping.FromAzure(properties, S3Operation.CreateMultipartUpload);
        }

        var metadata = BlobClient.ReadContainerMetadata(properties);
        if (metadata.TryGetValue(StateContainerOwnerMetadataName, out var owner))
        {
            if (!string.Equals(owner, StateContainerOwnerMetadataValue, StringComparison.Ordinal))
            {
                return new S3ErrorMapping.Mapping(
                    StatusCodes.Status500InternalServerError,
                    "InternalError",
                    "aws2azure: Reserved multipart state container exists but is not proxy-owned.");
            }

            return null;
        }

        var etag = ReadHeader(properties, "ETag");
        if (string.IsNullOrEmpty(etag))
        {
            return new S3ErrorMapping.Mapping(
                StatusCodes.Status500InternalServerError,
                "InternalError",
                "aws2azure: Reserved multipart state container is missing an ETag for ownership migration.");
        }

        var legacyContainer = await CheckLegacyStateContainerAsync(cancellationToken).ConfigureAwait(false);
        if (legacyContainer.Error is not null)
        {
            return legacyContainer.Error;
        }
        if (!legacyContainer.IsLegacyStateContainer)
        {
            return new S3ErrorMapping.Mapping(
                StatusCodes.Status500InternalServerError,
                "InternalError",
                "aws2azure: Reserved multipart state container exists but is not proxy-owned.");
        }

        metadata[StateContainerOwnerMetadataName] = StateContainerOwnerMetadataValue;
        using var update = await _blob.SetContainerMetadataAsync(
            _blob.MultipartStateContainerName,
            new Dictionary<string, string>(metadata, StringComparer.Ordinal),
            etag,
            cancellationToken).ConfigureAwait(false);
        if (!update.IsSuccessStatusCode && update.StatusCode != HttpStatusCode.PreconditionFailed)
        {
            return S3ErrorMapping.FromAzure(update, S3Operation.CreateMultipartUpload);
        }

        using var verified = await _blob.GetContainerPropertiesAsync(_blob.MultipartStateContainerName, cancellationToken).ConfigureAwait(false);
        if (!verified.IsSuccessStatusCode)
        {
            return S3ErrorMapping.FromAzure(verified, S3Operation.CreateMultipartUpload);
        }

        metadata = BlobClient.ReadContainerMetadata(verified);
        if (!metadata.TryGetValue(StateContainerOwnerMetadataName, out owner) ||
            !string.Equals(owner, StateContainerOwnerMetadataValue, StringComparison.Ordinal))
        {
            return new S3ErrorMapping.Mapping(
                StatusCodes.Status500InternalServerError,
                "InternalError",
                "aws2azure: Reserved multipart state container exists but could not be claimed for proxy ownership.");
        }

        return null;
    }

    private async Task<LegacyStateContainerCheckResult> CheckLegacyStateContainerAsync(CancellationToken cancellationToken)
    {
        using var response = await _blob.ListBlobsWithMetadataAsync(
            _blob.MultipartStateContainerName,
            prefix: null,
            marker: null,
            maxResults: 16,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new LegacyStateContainerCheckResult(false, null);
        }
        if (!response.IsSuccessStatusCode)
        {
            return new LegacyStateContainerCheckResult(false, S3ErrorMapping.FromAzure(response, S3Operation.CreateMultipartUpload));
        }

        var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var page = ParseBlobMetadataPage(xml);
        if (page.Blobs.Count == 0)
        {
            return new LegacyStateContainerCheckResult(false, null);
        }

        for (var i = 0; i < page.Blobs.Count; i++)
        {
            if (!LooksLikeMultipartStateBlob(page.Blobs[i].Name, page.Blobs[i].Metadata))
            {
                return new LegacyStateContainerCheckResult(false, null);
            }
        }

        return new LegacyStateContainerCheckResult(true, null);
    }

    private async Task CleanupExpiredRecordsAsync(string bucket, int deleteLimit, CancellationToken cancellationToken)
    {
        var thresholdMs = DateTimeOffset.UtcNow.Subtract(UploadIdCodec.MaxAge).ToUnixTimeMilliseconds();
        var prefix = bucket + "/";
        string? marker = null;
        var deletes = 0;

        while (deletes < deleteLimit)
        {
            using var response = await _blob.ListBlobsWithMetadataAsync(
                _blob.MultipartStateContainerName,
                prefix,
                marker,
                maxResults: Math.Min(deleteLimit, 256),
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var page = ParseBlobMetadataPage(xml);
            var stop = false;
            for (var i = 0; i < page.Blobs.Count && deletes < deleteLimit; i++)
            {
                if (!TryReadRecordInitiatedMs(page.Blobs[i].Name, out var initiatedMs))
                {
                    continue;
                }

                if (initiatedMs >= thresholdMs)
                {
                    stop = true;
                    break;
                }

                _ = await DeleteRecordByNameAsync(page.Blobs[i].Name, cancellationToken).ConfigureAwait(false);
                deletes++;
            }

            if (stop || string.IsNullOrEmpty(page.NextMarker))
            {
                return;
            }

            marker = page.NextMarker;
        }
    }

    private async Task DeleteBucketRecordsCoreAsync(
        string bucket,
        Func<string, string, bool> shouldDelete,
        string generationArgument,
        CancellationToken cancellationToken)
    {
        string? marker = null;
        var prefix = bucket + "/";
        while (true)
        {
            using var response = await _blob.ListBlobsWithMetadataAsync(
                _blob.MultipartStateContainerName,
                prefix,
                marker,
                maxResults: 512,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var page = ParseBlobMetadataPage(xml);
            for (var i = 0; i < page.Blobs.Count; i++)
            {
                if (!page.Blobs[i].Metadata.TryGetValue(ContainerGenerationMetadataName, out var recordGeneration))
                {
                    continue;
                }

                if (shouldDelete(recordGeneration, generationArgument))
                {
                    _ = await DeleteRecordByNameAsync(page.Blobs[i].Name, cancellationToken).ConfigureAwait(false);
                }
            }

            if (string.IsNullOrEmpty(page.NextMarker))
            {
                return;
            }

            marker = page.NextMarker;
        }
    }

    private async Task<VisibleSummariesResult> ListVisibleSummariesAsync(
        string bucket,
        string currentGeneration,
        string? currentEtag,
        CancellationToken cancellationToken)
    {
        var results = new List<MultipartUploadStateSummary>();
        var expiryThreshold = DateTimeOffset.UtcNow.Subtract(UploadIdCodec.MaxAge);
        string? marker = null;
        var prefix = bucket + "/";
        while (true)
        {
            using var response = await _blob.ListBlobsWithMetadataAsync(
                _blob.MultipartStateContainerName,
                prefix,
                marker,
                maxResults: 512,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new VisibleSummariesResult(ResultKind.Success, results, null);
            }
            if (!response.IsSuccessStatusCode)
            {
                return new VisibleSummariesResult(ResultKind.Error, results, S3ErrorMapping.FromAzure(response, S3Operation.ListMultipartUploads));
            }

            var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var page = ParseBlobMetadataPage(xml);
            for (var i = 0; i < page.Blobs.Count; i++)
            {
                if (!TryReadSummary(bucket, page.Blobs[i].Name, page.Blobs[i].Metadata, out var summary))
                {
                    continue;
                }

                if (summary.Initiated < expiryThreshold)
                {
                    continue;
                }

                if (string.Equals(summary.ContainerGeneration, currentGeneration, StringComparison.Ordinal) ||
                    (IsLegacyContainerGeneration(summary.ContainerGeneration) &&
                     !string.IsNullOrEmpty(currentEtag) &&
                     string.Equals(summary.ContainerGeneration, currentEtag, StringComparison.Ordinal)))
                {
                    results.Add(summary);
                }
            }

            if (string.IsNullOrEmpty(page.NextMarker))
            {
                return new VisibleSummariesResult(ResultKind.Success, results, null);
            }

            marker = page.NextMarker;
        }
    }

    private Task<GenerationResult> ReadCurrentContainerGenerationAsync(string bucket, CancellationToken cancellationToken) =>
        ReadCurrentContainerGenerationCoreAsync(bucket, createIfMissing: false, cancellationToken);

    private async Task<GenerationResult> ReadCurrentContainerGenerationCoreAsync(
        string bucket,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < ContainerGenerationMutationMaxAttempts; attempt++)
        {
            using var response = await _blob.GetContainerPropertiesAsync(bucket, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new GenerationResult(ResultKind.BucketMissing, null, null, null);
            }
            if (!response.IsSuccessStatusCode)
            {
                return new GenerationResult(ResultKind.Error, null, null, S3ErrorMapping.FromAzure(response, S3Operation.ListMultipartUploads));
            }

            var etag = ReadHeader(response, "ETag");
            var metadata = BlobClient.ReadContainerMetadata(response);
            if (metadata.TryGetValue(ContainerGenerationMarkerMetadataName, out var generation) &&
                !string.IsNullOrEmpty(generation))
            {
                return new GenerationResult(ResultKind.Success, generation, etag, null);
            }

            if (!createIfMissing)
            {
                return new GenerationResult(ResultKind.Success, etag, etag, null);
            }

            if (string.IsNullOrEmpty(etag))
            {
                return new GenerationResult(ResultKind.Error, null, null, new S3ErrorMapping.Mapping(
                    StatusCodes.Status500InternalServerError,
                    "InternalError",
                    "aws2azure: Azure container probe did not return an ETag for multipart generation binding."));
            }

            generation = Guid.NewGuid().ToString("N");
            var updatedMetadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal)
            {
                [ContainerGenerationMarkerMetadataName] = generation,
            };

            using var write = await _blob.SetContainerMetadataAsync(bucket, updatedMetadata, etag, cancellationToken).ConfigureAwait(false);
            if (write.IsSuccessStatusCode)
            {
                return new GenerationResult(ResultKind.Success, generation, ReadHeader(write, "ETag") ?? etag, null);
            }

            if (write.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                continue;
            }

            return new GenerationResult(ResultKind.Error, null, null, S3ErrorMapping.FromAzure(write, S3Operation.CreateMultipartUpload));
        }

        return new GenerationResult(ResultKind.Error, null, null, new S3ErrorMapping.Mapping(
            StatusCodes.Status409Conflict,
            "OperationAborted",
            "A conflicting conditional operation is currently in progress against this resource."));
    }

    private async Task<S3ErrorMapping.Mapping?> SendLeaseRequestAsync(
        string bucket,
        string uploadId,
        string leaseId,
        string action,
        S3Operation operation,
        CancellationToken cancellationToken)
    {
        if (!TryBuildRecordName(bucket, uploadId, out var recordName))
        {
            return null;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            _blob.BuildBlobUri(_blob.MultipartStateContainerName, recordName, "?comp=lease"));
        request.Options.Set(AzureHttpClient.NoRetryOption, true);
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        request.Content.Headers.ContentLength = 0;
        request.Headers.TryAddWithoutValidation("x-ms-lease-action", action);
        request.Headers.TryAddWithoutValidation("x-ms-lease-id", leaseId);
        using var response = await _blob.SendBlobRequestAsync(request, cancellationToken).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : response.IsSuccessStatusCode
                ? null
                : S3ErrorMapping.FromAzure(response, operation);
    }

    private async Task<S3ErrorMapping.Mapping?> DeleteRecordByNameAsync(string recordName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, _blob.BuildBlobUri(_blob.MultipartStateContainerName, recordName));
        request.Options.Set(AzureHttpClient.NoRetryOption, true);
        using var response = await _blob.SendBlobRequestAsync(request, cancellationToken).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : response.IsSuccessStatusCode
                ? null
                : S3ErrorMapping.FromAzure(response, S3Operation.AbortMultipartUpload);
    }

    private static bool TryBuildRecordName(string bucket, string uploadId, out string recordName)
    {
        recordName = string.Empty;
        if (!UploadIdCodec.TryReadCreatedAt(uploadId, out var createdAt))
        {
            return false;
        }

        recordName = BuildRecordName(bucket, createdAt, uploadId);
        return true;
    }

    private static string BuildRecordName(string bucket, DateTimeOffset initiated, string uploadId) =>
        string.Create(
            bucket.Length + 1 + 13 + 1 + uploadId.Length,
            (bucket, uploadId, initiated),
            static (span, state) =>
            {
                var (bucket, uploadId, initiated) = state;
                bucket.AsSpan().CopyTo(span);
                var index = bucket.Length;
                span[index++] = '/';
                initiated.ToUnixTimeMilliseconds().TryFormat(span.Slice(index, 13), out _, "D13", CultureInfo.InvariantCulture);
                index += 13;
                span[index++] = '-';
                uploadId.AsSpan().CopyTo(span[index..]);
            });

    private static byte[] SerializeCapturedHeaders(HttpRequest request, IReadOnlyList<S3XmlWriter.Tag> tags)
    {
        var contentType = HeaderForwarding.ReadFirstHeader(request, HeaderNames.ContentType);
        var contentEncoding = HeaderForwarding.ReadFirstHeader(request, HeaderNames.ContentEncoding);
        var contentLanguage = HeaderForwarding.ReadFirstHeader(request, HeaderNames.ContentLanguage);
        var contentDisposition = HeaderForwarding.ReadFirstHeader(request, HeaderNames.ContentDisposition);
        var cacheControl = HeaderForwarding.ReadFirstHeader(request, HeaderNames.CacheControl);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in request.Headers)
        {
            if (!header.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    metadata[header.Key["x-amz-meta-".Length..].ToLowerInvariant()] = value;
                    break;
                }
            }
        }

        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes(Magic));
        var flags = 0;
        if (contentType is not null) flags |= 1 << 0;
        if (contentEncoding is not null) flags |= 1 << 1;
        if (contentLanguage is not null) flags |= 1 << 2;
        if (contentDisposition is not null) flags |= 1 << 3;
        if (cacheControl is not null) flags |= 1 << 4;
        stream.WriteByte((byte)flags);
        if (contentType is not null) WriteUtf8String(stream, contentType);
        if (contentEncoding is not null) WriteUtf8String(stream, contentEncoding);
        if (contentLanguage is not null) WriteUtf8String(stream, contentLanguage);
        if (contentDisposition is not null) WriteUtf8String(stream, contentDisposition);
        if (cacheControl is not null) WriteUtf8String(stream, cacheControl);

        Span<byte> countBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(countBytes, checked((ushort)metadata.Count));
        stream.Write(countBytes);
        foreach (var entry in metadata)
        {
            WriteUtf8String(stream, entry.Key);
            WriteUtf8String(stream, entry.Value);
        }

        // Tags captured from x-amz-tagging on initiate (issue #799), applied
        // to the committed blob via Set Blob Tags once CompleteMultipartUpload
        // finalizes it. Always written (even when empty) as a trailing
        // section so older, pre-#799 records — read while this section is
        // absent — still deserialize cleanly (see TryDeserializeCapturedHeaders).
        Span<byte> tagCountBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(tagCountBytes, checked((ushort)tags.Count));
        stream.Write(tagCountBytes);
        foreach (var tag in tags)
        {
            WriteUtf8String(stream, tag.Key);
            WriteUtf8String(stream, tag.Value);
        }

        if (stream.Length > MaxRecordBytes)
        {
            throw new InvalidOperationException("Multipart upload initiation headers exceeded the 16 KiB state cap.");
        }

        return stream.ToArray();
    }

    private static bool TryDeserializeCapturedHeaders(byte[] data, out CapturedHeaders headers)
    {
        headers = default;
        var span = data.AsSpan();
        if (span.Length < Magic.Length + 1 || !span[..Magic.Length].SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
        {
            return false;
        }

        var offset = Magic.Length;
        var flags = span[offset++];
        string? contentType = null;
        string? contentEncoding = null;
        string? contentLanguage = null;
        string? contentDisposition = null;
        string? cacheControl = null;
        if ((flags & (1 << 0)) != 0 && !TryReadUtf8String(span, ref offset, out contentType)) return false;
        if ((flags & (1 << 1)) != 0 && !TryReadUtf8String(span, ref offset, out contentEncoding)) return false;
        if ((flags & (1 << 2)) != 0 && !TryReadUtf8String(span, ref offset, out contentLanguage)) return false;
        if ((flags & (1 << 3)) != 0 && !TryReadUtf8String(span, ref offset, out contentDisposition)) return false;
        if ((flags & (1 << 4)) != 0 && !TryReadUtf8String(span, ref offset, out cacheControl)) return false;
        if (offset + 2 > span.Length) return false;

        var metadataCount = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset, 2));
        offset += 2;
        var metadata = new Dictionary<string, string>(metadataCount, StringComparer.Ordinal);
        for (var i = 0; i < metadataCount; i++)
        {
            if (!TryReadUtf8String(span, ref offset, out var key)
                || !TryReadUtf8String(span, ref offset, out var value)
                || key is null
                || value is null)
            {
                return false;
            }

            metadata[key] = value;
        }

        // Trailing tags section (issue #799). Absent on records written
        // before this change — treat that as "no tags" rather than a
        // deserialization failure.
        var tags = new List<S3XmlWriter.Tag>();
        if (offset < span.Length)
        {
            if (offset + 2 > span.Length) return false;
            var tagCount = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset, 2));
            offset += 2;
            for (var i = 0; i < tagCount; i++)
            {
                if (!TryReadUtf8String(span, ref offset, out var tagKey)
                    || !TryReadUtf8String(span, ref offset, out var tagValue)
                    || tagKey is null
                    || tagValue is null)
                {
                    return false;
                }

                tags.Add(new S3XmlWriter.Tag(tagKey, tagValue));
            }
        }

        headers = new CapturedHeaders(contentType, contentEncoding, contentLanguage, contentDisposition, cacheControl, metadata, tags);
        return offset == span.Length;
    }

    private static bool TryReadSummary(string bucket, string recordName, HttpResponseMessage response, out MultipartUploadStateSummary summary)
        => TryReadSummary(bucket, recordName, ReadBlobMetadata(response), out summary);

    private static bool TryReadSummary(
        string bucket,
        string recordName,
        IReadOnlyDictionary<string, string> metadata,
        out MultipartUploadStateSummary summary)
    {
        summary = default;
        if (!metadata.TryGetValue(KeyMetadataName, out var encodedKey)
            || !metadata.TryGetValue(UploadIdMetadataName, out var uploadId)
            || !metadata.TryGetValue(InitiatedMsMetadataName, out var initiatedMsRaw)
            || !metadata.TryGetValue(ContainerGenerationMetadataName, out var generation)
            || !UploadIdCodec.Base64Url.TryDecode(encodedKey, out var keyBytes)
            || !long.TryParse(initiatedMsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var initiatedMs))
        {
            return false;
        }

        string key;
        try
        {
            key = Encoding.UTF8.GetString(keyBytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        summary = new MultipartUploadStateSummary(
            bucket,
            key,
            uploadId,
            DateTimeOffset.FromUnixTimeMilliseconds(initiatedMs),
            generation,
            recordName);
        return true;
    }

    private static IReadOnlyDictionary<string, string> ReadBlobMetadata(HttpResponseMessage response)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in response.Headers)
        {
            if (!header.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                metadata[header.Key["x-ms-meta-".Length..].ToLowerInvariant()] = value;
                break;
            }
        }

        return metadata;
    }

    private static string EncodeMetadataString(string value) =>
        UploadIdCodec.Base64Url.Encode(Encoding.UTF8.GetBytes(value));

    private static void WriteUtf8String(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> lengthBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lengthBytes, checked((ushort)bytes.Length));
        stream.Write(lengthBytes);
        stream.Write(bytes);
    }

    private static bool TryReadUtf8String(ReadOnlySpan<byte> span, ref int offset, out string? value)
    {
        value = null;
        if (offset + 2 > span.Length)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset, 2));
        offset += 2;
        if (offset + length > span.Length)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(span.Slice(offset, length));
        offset += length;
        return true;
    }

    private static bool IsAfterMarker(MultipartUploadStateSummary summary, string? keyMarker, string? uploadIdMarker)
    {
        if (string.IsNullOrEmpty(keyMarker))
        {
            return true;
        }

        var keyCompare = CompareUtf8(summary.Key, keyMarker);
        if (keyCompare > 0)
        {
            return true;
        }
        if (keyCompare < 0)
        {
            return false;
        }

        if (string.IsNullOrEmpty(uploadIdMarker))
        {
            return false;
        }

        if (UploadIdCodec.TryReadCreatedAt(uploadIdMarker, out var markerCreatedAt))
        {
            var initiatedCompare = summary.Initiated.CompareTo(markerCreatedAt);
            if (initiatedCompare > 0)
            {
                return true;
            }
            if (initiatedCompare < 0)
            {
                return false;
            }
        }

        return string.CompareOrdinal(summary.UploadId, uploadIdMarker) > 0;
    }

    private static bool IsAfterKeyMarker(string value, string? keyMarker) =>
        string.IsNullOrEmpty(keyMarker) || CompareUtf8(value, keyMarker) > 0;

    private static bool IsLegacyContainerGeneration(string generation) =>
        !string.IsNullOrEmpty(generation) && generation[0] == '"';

    private static bool TryGetCommonPrefix(string key, string? prefix, string delimiter, out string commonPrefix)
    {
        commonPrefix = string.Empty;
        var start = prefix?.Length ?? 0;
        if (start > key.Length)
        {
            return false;
        }

        var index = key.IndexOf(delimiter, start, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        commonPrefix = key[..(index + delimiter.Length)];
        return true;
    }

    private static bool LooksLikeMultipartStateBlob(string recordName, IReadOnlyDictionary<string, string> metadata) =>
        TryReadRecordInitiatedMs(recordName, out _)
        && metadata.ContainsKey(KeyMetadataName)
        && metadata.ContainsKey(UploadIdMetadataName)
        && metadata.ContainsKey(InitiatedMsMetadataName)
        && metadata.ContainsKey(ContainerGenerationMetadataName);

    private static bool HasFurtherMatches(
        IReadOnlyList<MultipartUploadStateSummary> summaries,
        int startIndex,
        string? keyMarker,
        string? uploadIdMarker,
        string? prefix)
    {
        for (var i = startIndex; i < summaries.Count; i++)
        {
            if (!IsAfterMarker(summaries[i], keyMarker, uploadIdMarker))
            {
                continue;
            }

            if (prefix is null || summaries[i].Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadRecordInitiatedMs(string recordName, out long initiatedMs)
    {
        initiatedMs = 0;
        var slash = recordName.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || slash + 15 > recordName.Length)
        {
            return false;
        }

        return long.TryParse(recordName.AsSpan(slash + 1, 13), NumberStyles.Integer, CultureInfo.InvariantCulture, out initiatedMs);
    }

    private static string? ReadHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
        }

        if (response.Content.Headers.TryGetValues(name, out var contentValues))
        {
            foreach (var value in contentValues)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    public readonly record struct CapturedHeaders(
        string? ContentType,
        string? ContentEncoding,
        string? ContentLanguage,
        string? ContentDisposition,
        string? CacheControl,
        IReadOnlyDictionary<string, string> Metadata,
        IReadOnlyList<S3XmlWriter.Tag> Tags)
    {
        public void ApplyHeaders(HttpRequestMessage request)
        {
            if (!string.IsNullOrEmpty(ContentType))
            {
                request.Headers.TryAddWithoutValidation("x-ms-blob-content-type", ContentType);
            }
            if (!string.IsNullOrEmpty(ContentEncoding))
            {
                request.Headers.TryAddWithoutValidation("x-ms-blob-content-encoding", ContentEncoding);
            }
            if (!string.IsNullOrEmpty(ContentLanguage))
            {
                request.Headers.TryAddWithoutValidation("x-ms-blob-content-language", ContentLanguage);
            }
            if (!string.IsNullOrEmpty(ContentDisposition))
            {
                request.Headers.TryAddWithoutValidation("x-ms-blob-content-disposition", ContentDisposition);
            }
            if (!string.IsNullOrEmpty(CacheControl))
            {
                request.Headers.TryAddWithoutValidation("x-ms-blob-cache-control", CacheControl);
            }

            foreach (var entry in Metadata)
            {
                request.Headers.TryAddWithoutValidation("x-ms-meta-" + entry.Key, entry.Value);
            }
        }
    }

    private readonly record struct BlobMetadataEntry(
        string Name,
        IReadOnlyDictionary<string, string> Metadata);

    private readonly record struct BlobMetadataPage(
        IReadOnlyList<BlobMetadataEntry> Blobs,
        string? NextMarker);

    private static BlobMetadataPage ParseBlobMetadataPage(string xml)
    {
        var blobs = new List<BlobMetadataEntry>();
        string? nextMarker = null;

        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
        });

        var inBlob = false;
        var inMetadata = false;
        string? blobName = null;
        Dictionary<string, string>? metadata = null;
        string? currentMetadataName = null;

        if (!reader.Read())
        {
            return new BlobMetadataPage(blobs, nextMarker);
        }

        while (!reader.EOF)
        {
            var shouldAdvance = true;
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "Blob")
                {
                    inBlob = true;
                    blobName = null;
                    metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                else if (reader.LocalName == "Metadata" && inBlob)
                {
                    inMetadata = true;
                }
                else if (reader.LocalName == "Name" && inBlob && !inMetadata)
                {
                    blobName = reader.ReadElementContentAsString();
                    shouldAdvance = false;
                }
                else if (reader.LocalName == "NextMarker" && !inBlob)
                {
                    var marker = reader.ReadElementContentAsString();
                    if (!string.IsNullOrEmpty(marker))
                    {
                        nextMarker = marker;
                    }
                    shouldAdvance = false;
                }
                else if (inMetadata)
                {
                    currentMetadataName = reader.LocalName.ToLowerInvariant();
                    var value = reader.ReadElementContentAsString();
                    metadata![currentMetadataName] = value;
                    shouldAdvance = false;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "Metadata")
                {
                    inMetadata = false;
                    currentMetadataName = null;
                }
                else if (reader.LocalName == "Blob")
                {
                    if (!string.IsNullOrEmpty(blobName))
                    {
                        blobs.Add(new BlobMetadataEntry(blobName!, metadata ?? new Dictionary<string, string>(StringComparer.Ordinal)));
                    }

                    inBlob = false;
                    inMetadata = false;
                }
            }

            if (shouldAdvance)
            {
                reader.Read();
            }
        }

        return new BlobMetadataPage(blobs, nextMarker);
    }
}
