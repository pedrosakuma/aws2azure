using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.Errors;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.Kinesis.Operations;

internal static class ListShardsHandler
{
    public static async Task HandleAsync(
        HttpContext context,
        KinesisParseResult parseResult,
        EventHubsCredentials credentials,
        IEventHubsManagementClient managementClient,
        ListShardsCursorCodecFactory cursorCodecFactory,
        CancellationToken cancellationToken)
    {
        if (!KinesisMetadataSupport.TryDeserialize(
                parseResult.Body,
                KinesisJsonSerializerContext.Default.ListShardsRequest,
                out ListShardsRequest? request,
                out var parseError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "SerializationException", parseError!)
                .ConfigureAwait(false);
            return;
        }

        if (!KinesisMetadataSupport.TryValidateListShardsFilter(request?.ShardFilter, out var validationError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", validationError!)
                .ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(request?.NextToken)
            && !string.IsNullOrWhiteSpace(request.ExclusiveStartShardId))
        {
            await KinesisErrorResponse.WriteAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "ValidationException",
                    "ExclusiveStartShardId cannot be used together with NextToken.")
                .ConfigureAwait(false);
            return;
        }

        var cursorCodec = cursorCodecFactory.Create(credentials);
        string streamName;
        string? startAfterShardId;

        if (!string.IsNullOrWhiteSpace(request?.NextToken))
        {
            if (!cursorCodec.TryDecode(request.NextToken!, out var decodedCursor, out _))
            {
                await KinesisErrorResponse.WriteAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "ExpiredNextTokenException",
                        "The supplied NextToken is expired or invalid.")
                    .ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrWhiteSpace(request.StreamName)
                && !string.Equals(request.StreamName, decodedCursor.StreamName, StringComparison.Ordinal))
            {
                await KinesisErrorResponse.WriteAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "ValidationException",
                        "StreamName does not match the supplied NextToken.")
                    .ConfigureAwait(false);
                return;
            }

            streamName = decodedCursor.StreamName;
            startAfterShardId = decodedCursor.StartAfterShardId;
        }
        else if (!KinesisMetadataSupport.TryResolveStreamName(request?.StreamName, streamArn: null, out streamName, out validationError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", validationError!)
                .ConfigureAwait(false);
            return;
        }
        else
        {
            startAfterShardId = request?.ExclusiveStartShardId;
        }

        var namespaceFqdn = KinesisMetadataSupport.ResolveNamespaceFqdn(credentials);
        var eventHubName = KinesisMetadataSupport.ResolveEventHubName(credentials, streamName);

        EventHubDescription eventHub;
        try
        {
            eventHub = await managementClient.GetEventHubAsync(credentials, namespaceFqdn, eventHubName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EventHubsManagementException ex)
        {
            await KinesisMetadataSupport.WriteManagementErrorAsync(context, ex, streamName).ConfigureAwait(false);
            return;
        }

        var mappedShards = ShardMapper.MapShards(eventHub.PartitionIds);
        if (!KinesisMetadataSupport.TryApplyShardPagination(
                mappedShards,
                startAfterShardId,
                request?.MaxResults,
                out var page,
                out var hasMore,
                out validationError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", validationError!)
                .ConfigureAwait(false);
            return;
        }

        string? nextToken = null;
        if (hasMore && page.Length > 0)
        {
            nextToken = cursorCodec.Encode(new ListShardsCursor(
                streamName,
                page[^1].ShardId,
                cursorCodec.TimeProvider.GetUtcNow().ToUnixTimeSeconds()));
        }

        var response = new ListShardsResponse
        {
            Shards = page,
            NextToken = nextToken,
        };

        await KinesisMetadataSupport.WriteJsonAsync(context, response, KinesisJsonSerializerContext.Default.ListShardsResponse)
            .ConfigureAwait(false);
    }
}

public sealed class ListShardsCursorCodecFactory
{
    private static readonly Lazy<byte[]> ProcessSigningKey = new(static () => RandomNumberGenerator.GetBytes(32));

    private readonly ILogger<ListShardsCursorCodecFactory> _logger;
    private readonly TimeProvider _timeProvider;

    public ListShardsCursorCodecFactory(ILogger<ListShardsCursorCodecFactory> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ListShardsCursorCodec Create(EventHubsCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        if (!string.IsNullOrWhiteSpace(credentials.ShardIteratorSigningKey))
        {
            var decoded = Convert.FromBase64String(credentials.ShardIteratorSigningKey);
            if (decoded.Length < 32)
            {
                throw new ArgumentException(
                    "Event Hubs list-shards signing key must decode to at least 32 bytes.",
                    nameof(credentials));
            }

            return new ListShardsCursorCodec(decoded, _timeProvider);
        }

        ListShardsCursorCodecFactoryLog.UsingEphemeralSigningKey(_logger);
        return new ListShardsCursorCodec(ProcessSigningKey.Value, _timeProvider);
    }
}

public sealed class ListShardsCursorCodec
{
    internal const string Prefix = "aws2az-ls-";
    private const string Version = "v1";
    private const int MaxAgeSeconds = 300;
    private static readonly Encoding Utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] _signingKey;

    public ListShardsCursorCodec(ReadOnlySpan<byte> signingKey, TimeProvider? timeProvider = null)
    {
        if (signingKey.IsEmpty)
        {
            throw new ArgumentException("Signing key must not be empty.", nameof(signingKey));
        }

        _signingKey = signingKey.ToArray();
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    public TimeProvider TimeProvider { get; }

    public string Encode(ListShardsCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        var payload = string.Concat(
            Version,
            "|",
            Escape(cursor.StreamName),
            "|",
            Escape(cursor.StartAfterShardId),
            "|",
            cursor.IssuedAtUnixSeconds.ToString(CultureInfo.InvariantCulture));

        var payloadBytes = Utf8Strict.GetBytes(payload);
        var signature = ComputeSignature(payloadBytes);
        return Prefix + Base64UrlEncode(payloadBytes) + "." + Base64UrlEncode(signature);
    }

    public bool TryDecode(string encoded, out ListShardsCursor cursor, out ListShardsCursorVerifyError error)
    {
        cursor = new ListShardsCursor(string.Empty, string.Empty, 0);
        error = ListShardsCursorVerifyError.None;

        if (string.IsNullOrEmpty(encoded)
            || !encoded.StartsWith(Prefix, StringComparison.Ordinal)
            || !TrySplitWire(encoded.AsSpan(Prefix.Length), out var payloadPart, out var signaturePart)
            || !TryBase64UrlDecode(payloadPart, out var payloadBytes)
            || !TryBase64UrlDecode(signaturePart, out var providedSignature))
        {
            error = ListShardsCursorVerifyError.MalformedFormat;
            return false;
        }

        var expectedSignature = ComputeSignature(payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
        {
            error = ListShardsCursorVerifyError.BadSignature;
            return false;
        }

        string payload;
        try
        {
            payload = Utf8Strict.GetString(payloadBytes);
        }
        catch (DecoderFallbackException)
        {
            error = ListShardsCursorVerifyError.MalformedPayload;
            return false;
        }

        if (!TryParsePayload(payload, out cursor))
        {
            error = ListShardsCursorVerifyError.MalformedPayload;
            return false;
        }

        var ageSeconds = TimeProvider.GetUtcNow().ToUnixTimeSeconds() - cursor.IssuedAtUnixSeconds;
        if (ageSeconds < 0 || ageSeconds > MaxAgeSeconds)
        {
            cursor = new ListShardsCursor(string.Empty, string.Empty, 0);
            error = ListShardsCursorVerifyError.Expired;
            return false;
        }

        return true;
    }

    private byte[] ComputeSignature(ReadOnlySpan<byte> payloadBytes)
    {
        using var hmac = new HMACSHA256(_signingKey);
        return hmac.ComputeHash(payloadBytes.ToArray());
    }

    private static bool TrySplitWire(ReadOnlySpan<char> encoded, out string payloadPart, out string signaturePart)
    {
        payloadPart = string.Empty;
        signaturePart = string.Empty;

        var separatorIndex = encoded.IndexOf('.');
        if (separatorIndex <= 0 || separatorIndex == encoded.Length - 1)
        {
            return false;
        }

        if (encoded[(separatorIndex + 1)..].IndexOf('.') >= 0)
        {
            return false;
        }

        payloadPart = encoded[..separatorIndex].ToString();
        signaturePart = encoded[(separatorIndex + 1)..].ToString();
        return true;
    }

    private static bool TryParsePayload(string payload, out ListShardsCursor cursor)
    {
        cursor = new ListShardsCursor(string.Empty, string.Empty, 0);
        if (!TrySplitPayloadFields(payload, out var fields)
            || !string.Equals(fields[0], Version, StringComparison.Ordinal)
            || !long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var issuedAtUnixSeconds))
        {
            return false;
        }

        cursor = new ListShardsCursor(fields[1], fields[2], issuedAtUnixSeconds);
        return !string.IsNullOrWhiteSpace(cursor.StreamName) && !string.IsNullOrWhiteSpace(cursor.StartAfterShardId);
    }

    private static bool TrySplitPayloadFields(string payload, out string[] fields)
    {
        fields = new string[4];
        var current = new StringBuilder(payload.Length);
        var fieldIndex = 0;
        var escaping = false;

        foreach (var c in payload)
        {
            if (escaping)
            {
                current.Append(c);
                escaping = false;
                continue;
            }

            if (c == '\\')
            {
                escaping = true;
                continue;
            }

            if (c == '|')
            {
                if (fieldIndex >= fields.Length - 1)
                {
                    return false;
                }

                fields[fieldIndex++] = current.ToString();
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (escaping || fieldIndex != fields.Length - 1)
        {
            return false;
        }

        fields[fieldIndex] = current.ToString();
        return true;
    }

    private static string Escape(string value)
    {
        var needsEscape = false;
        foreach (var c in value)
        {
            if (c is '|' or '\\')
            {
                needsEscape = true;
                break;
            }
        }

        if (!needsEscape)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            if (c is '|' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string encoded, out byte[] data)
    {
        data = [];
        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        var builder = new StringBuilder(encoded.Length + 3);
        foreach (var c in encoded)
        {
            builder.Append(c switch
            {
                '-' => '+',
                '_' => '/',
                _ => c,
            });
        }

        switch (builder.Length % 4)
        {
            case 2:
                builder.Append("==");
                break;
            case 3:
                builder.Append('=');
                break;
            case 1:
                return false;
        }

        try
        {
            data = Convert.FromBase64String(builder.ToString());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record ListShardsCursor(string StreamName, string StartAfterShardId, long IssuedAtUnixSeconds);

public enum ListShardsCursorVerifyError
{
    None = 0,
    MalformedFormat,
    BadSignature,
    MalformedPayload,
    Expired,
}

internal static partial class ListShardsCursorCodecFactoryLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Event Hubs list-shards cursor signing key is not configured; using an ephemeral process key.")]
    public static partial void UsingEphemeralSigningKey(ILogger logger);
}
