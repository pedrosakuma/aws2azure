using System.Globalization;
using System.Security.Cryptography;
using Aws2Azure.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.Kinesis.ShardIterators;

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

        KinesisLog.UsingEphemeralListShardsCursorSigningKey(_logger);
        return new ListShardsCursorCodec(ProcessSigningKey.Value, _timeProvider);
    }
}

public sealed class ListShardsCursorCodec
{
    internal const string Prefix = "aws2az-ls-";
    private const int MaxAgeSeconds = 300;
    private const int FieldCount = 3;

    private readonly HmacTokenCodec _codec;

    public ListShardsCursorCodec(ReadOnlySpan<byte> signingKey, TimeProvider? timeProvider = null)
    {
        _codec = new HmacTokenCodec(Prefix, signingKey);
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    public TimeProvider TimeProvider { get; }

    public string Encode(ListShardsCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        Span<string> fields =
        [
            cursor.StreamName,
            cursor.StartAfterShardId,
            cursor.IssuedAtUnixSeconds.ToString(CultureInfo.InvariantCulture),
        ];
        return _codec.Encode(fields);
    }

    public bool TryDecode(string encoded, out ListShardsCursor cursor, out ListShardsCursorVerifyError error)
    {
        cursor = new ListShardsCursor(string.Empty, string.Empty, 0);
        error = ListShardsCursorVerifyError.None;

        if (!_codec.TryDecode(encoded, FieldCount, out var fields, out var decodeError))
        {
            error = MapDecodeError(decodeError);
            return false;
        }

        if (!long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var issuedAtUnixSeconds))
        {
            error = ListShardsCursorVerifyError.MalformedPayload;
            return false;
        }

        var decoded = new ListShardsCursor(fields[0], fields[1], issuedAtUnixSeconds);
        if (string.IsNullOrWhiteSpace(decoded.StreamName) || string.IsNullOrWhiteSpace(decoded.StartAfterShardId))
        {
            error = ListShardsCursorVerifyError.MalformedPayload;
            return false;
        }

        var ageSeconds = TimeProvider.GetUtcNow().ToUnixTimeSeconds() - decoded.IssuedAtUnixSeconds;
        if (ageSeconds < 0 || ageSeconds > MaxAgeSeconds)
        {
            error = ListShardsCursorVerifyError.Expired;
            return false;
        }

        cursor = decoded;
        return true;
    }

    private static ListShardsCursorVerifyError MapDecodeError(HmacTokenDecodeError error) => error switch
    {
        HmacTokenDecodeError.BadSignature => ListShardsCursorVerifyError.BadSignature,
        HmacTokenDecodeError.MalformedPayload => ListShardsCursorVerifyError.MalformedPayload,
        _ => ListShardsCursorVerifyError.MalformedFormat,
    };
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
