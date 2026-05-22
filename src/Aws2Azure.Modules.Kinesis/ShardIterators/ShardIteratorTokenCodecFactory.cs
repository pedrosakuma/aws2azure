using System.Security.Cryptography;
using Aws2Azure.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.Kinesis.ShardIterators;

public sealed class ShardIteratorTokenCodecFactory
{
    private static readonly Lazy<byte[]> ProcessSigningKey = new(static () => RandomNumberGenerator.GetBytes(32));

    private readonly ILogger<ShardIteratorTokenCodecFactory> _logger;
    private readonly TimeProvider _timeProvider;

    public ShardIteratorTokenCodecFactory(
        ILogger<ShardIteratorTokenCodecFactory> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ShardIteratorTokenCodec Create(EventHubsCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        if (!string.IsNullOrWhiteSpace(credentials.ShardIteratorSigningKey))
        {
            var decoded = Convert.FromBase64String(credentials.ShardIteratorSigningKey);
            if (decoded.Length < 32)
            {
                throw new ArgumentException(
                    "Event Hubs shard iterator signing key must decode to at least 32 bytes.",
                    nameof(credentials));
            }

            return new ShardIteratorTokenCodec(decoded, _timeProvider);
        }

        ShardIteratorTokenCodecFactoryLog.UsingEphemeralSigningKey(_logger);
        return new ShardIteratorTokenCodec(ProcessSigningKey.Value, _timeProvider);
    }
}
