using System.Globalization;

namespace Aws2Azure.Modules.Kinesis.ShardIterators;

public sealed class ShardIteratorTokenCodec
{
    internal const string Prefix = "aws2az-it-";
    internal const int MaxAgeSeconds = 300;
    private const int FieldCount = 6;

    private readonly HmacTokenCodec _codec;
    private readonly TimeProvider _timeProvider;

    public ShardIteratorTokenCodec(ReadOnlySpan<byte> signingKey, TimeProvider? timeProvider = null)
    {
        _codec = new HmacTokenCodec(Prefix, signingKey);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Encode(ShardIteratorToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(token.Stream);
        ArgumentNullException.ThrowIfNull(token.Shard);

        if (!IsValidPosition(token.Type, token.Position))
        {
            throw new ArgumentException("Position is invalid for the shard iterator type.", nameof(token));
        }

        Span<string> fields =
        [
            token.Stream,
            token.Shard,
            ((int)token.Type).ToString(CultureInfo.InvariantCulture),
            token.Position ?? string.Empty,
            token.IssuedAtUnixSeconds.ToString(CultureInfo.InvariantCulture),
            token.IteratorId,
        ];
        return _codec.Encode(fields);
    }

    public bool TryDecode(string encoded, out ShardIteratorToken token, out ShardIteratorVerifyError error)
    {
        token = new ShardIteratorToken(string.Empty, string.Empty, ShardIteratorType.TrimHorizon, null, 0);
        error = ShardIteratorVerifyError.None;

        if (!_codec.TryDecode(encoded, FieldCount, out var fields, out var decodeError))
        {
            error = MapDecodeError(decodeError);
            return false;
        }

        if (!int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var typeValue)
            || !TryParseType(typeValue, out var type)
            || !long.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var issuedAtUnixSeconds))
        {
            error = ShardIteratorVerifyError.MalformedPayload;
            return false;
        }

        var position = fields[3].Length == 0 ? null : fields[3];
        if (!IsValidPosition(type, position))
        {
            error = ShardIteratorVerifyError.MalformedPayload;
            return false;
        }

        var decoded = new ShardIteratorToken(fields[0], fields[1], type, position, issuedAtUnixSeconds, fields[5]);
        var ageSeconds = _timeProvider.GetUtcNow().ToUnixTimeSeconds() - decoded.IssuedAtUnixSeconds;
        if (ageSeconds < 0 || ageSeconds > MaxAgeSeconds)
        {
            error = ShardIteratorVerifyError.Expired;
            return false;
        }

        token = decoded;
        return true;
    }

    private static ShardIteratorVerifyError MapDecodeError(HmacTokenDecodeError error) => error switch
    {
        HmacTokenDecodeError.BadSignature => ShardIteratorVerifyError.BadSignature,
        HmacTokenDecodeError.MalformedPayload => ShardIteratorVerifyError.MalformedPayload,
        _ => ShardIteratorVerifyError.MalformedFormat,
    };

    private static bool TryParseType(int value, out ShardIteratorType type)
    {
        switch (value)
        {
            case (int)ShardIteratorType.TrimHorizon:
                type = ShardIteratorType.TrimHorizon;
                return true;
            case (int)ShardIteratorType.Latest:
                type = ShardIteratorType.Latest;
                return true;
            case (int)ShardIteratorType.AtSequenceNumber:
                type = ShardIteratorType.AtSequenceNumber;
                return true;
            case (int)ShardIteratorType.AfterSequenceNumber:
                type = ShardIteratorType.AfterSequenceNumber;
                return true;
            case (int)ShardIteratorType.AtTimestamp:
                type = ShardIteratorType.AtTimestamp;
                return true;
            default:
                type = default;
                return false;
        }
    }

    private static bool IsValidPosition(ShardIteratorType type, string? position)
    {
        return type switch
        {
            ShardIteratorType.TrimHorizon or ShardIteratorType.Latest => position is null,
            ShardIteratorType.AtSequenceNumber or ShardIteratorType.AfterSequenceNumber or ShardIteratorType.AtTimestamp
                => !string.IsNullOrEmpty(position),
            _ => false,
        };
    }
}
