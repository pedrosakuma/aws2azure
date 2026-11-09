using System.Buffers.Binary;
using System.Security.Cryptography;
using Aws2Azure.Core.Azure;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.S3.Streaming;

internal enum FlexibleChecksumAlgorithm
{
    Crc32,
    Crc32C,
    Crc64Nvme,
    Sha1,
    Sha256,
}

internal readonly record struct FlexibleChecksumError(string Code, string Message);

internal sealed class FlexibleChecksumValidation
{
    private static readonly FlexibleChecksumAlgorithm[] Algorithms =
    [
        FlexibleChecksumAlgorithm.Crc32,
        FlexibleChecksumAlgorithm.Crc32C,
        FlexibleChecksumAlgorithm.Crc64Nvme,
        FlexibleChecksumAlgorithm.Sha1,
        FlexibleChecksumAlgorithm.Sha256,
    ];

    private readonly FlexibleChecksumAlgorithm _algorithm;
    private readonly byte[]? _headerDigest;
    private readonly string? _trailerHeader;
    private readonly AwsChunkedDecoder? _decoder;

    private FlexibleChecksumValidation(
        FlexibleChecksumAlgorithm algorithm,
        byte[]? headerDigest,
        string? trailerHeader,
        AwsChunkedDecoder? decoder)
    {
        _algorithm = algorithm;
        _headerDigest = headerDigest;
        _trailerHeader = trailerHeader;
        _decoder = decoder;
    }

    public Stream CreateStream(Stream body) =>
        new ValidatingStream(body, _algorithm, _headerDigest, _trailerHeader, _decoder);

    public static bool TryCreate(
        HttpRequest request,
        AwsChunkedDecoder? decoder,
        out FlexibleChecksumValidation? validation,
        out FlexibleChecksumError error)
    {
        validation = null;
        error = default;

        foreach (var header in request.Headers)
        {
            if (header.Key.StartsWith("x-amz-checksum-", StringComparison.OrdinalIgnoreCase) &&
                !TryParseHeaderName(header.Key, out _))
            {
                error = InvalidArgument($"Checksum header '{header.Key}' is not supported.");
                return false;
            }
        }

        FlexibleChecksumAlgorithm? selectedByHeader = null;
        string? encodedHeaderDigest = null;

        foreach (var algorithm in Algorithms)
        {
            var headerName = GetHeaderName(algorithm);
            if (!TryReadSingleHeader(request, headerName, out var value, out var duplicate))
            {
                if (duplicate)
                {
                    error = InvalidArgument($"Header {headerName} must contain exactly one checksum value.");
                    return false;
                }

                continue;
            }

            if (selectedByHeader is not null)
            {
                error = InvalidArgument("Only one x-amz-checksum-* value may be supplied for PutObject.");
                return false;
            }

            selectedByHeader = algorithm;
            encodedHeaderDigest = value;
        }

        if (!TryReadTrailerAlgorithm(request, out var selectedByTrailer, out error))
        {
            return false;
        }

        if (selectedByHeader is not null && selectedByTrailer is not null)
        {
            error = InvalidArgument("A checksum must be supplied either as a request header or as an aws-chunked trailer, not both.");
            return false;
        }

        FlexibleChecksumAlgorithm? selectedBySdk = null;
        if (TryReadSingleHeader(request, "x-amz-sdk-checksum-algorithm", out var sdkValue, out var duplicateSdk))
        {
            if (!TryParseAlgorithm(sdkValue, out var parsed))
            {
                error = InvalidArgument($"Checksum algorithm '{sdkValue}' is not supported.");
                return false;
            }

            selectedBySdk = parsed;
        }
        else if (duplicateSdk)
        {
            error = InvalidArgument("Header x-amz-sdk-checksum-algorithm must contain exactly one value.");
            return false;
        }

        var selectedByValue = selectedByHeader ?? selectedByTrailer;
        if (selectedBySdk is not null && selectedByValue is not null && selectedBySdk != selectedByValue)
        {
            error = InvalidArgument(
                $"x-amz-sdk-checksum-algorithm {GetToken(selectedBySdk.Value)} does not match {GetHeaderName(selectedByValue.Value)}.");
            return false;
        }

        var algorithmToValidate = selectedBySdk ?? selectedByValue;
        if (algorithmToValidate is null)
        {
            return true;
        }

        if (selectedByValue is null)
        {
            error = InvalidArgument(
                $"Checksum algorithm {GetToken(algorithmToValidate.Value)} requires a matching checksum header or x-amz-trailer declaration.");
            return false;
        }

        if (selectedByTrailer is not null && (decoder is null || !decoder.ExpectsTrailer))
        {
            error = InvalidArgument("A checksum trailer requires an aws-chunked *-TRAILER payload.");
            return false;
        }

        byte[]? headerDigest = null;
        if (encodedHeaderDigest is not null &&
            !TryDecodeDigest(encodedHeaderDigest, GetDigestLength(algorithmToValidate.Value), out headerDigest))
        {
            error = BadDigest($"Header {GetHeaderName(algorithmToValidate.Value)} is not valid base64 with the required digest length.");
            return false;
        }

        validation = new FlexibleChecksumValidation(
            algorithmToValidate.Value,
            headerDigest,
            selectedByTrailer is null ? null : GetHeaderName(selectedByTrailer.Value),
            decoder);
        return true;
    }

    public static bool TryFindFailure(Exception exception, out FlexibleChecksumError error)
    {
        Exception? current = exception;
        while (current is not null)
        {
            if (current is FlexibleChecksumException checksumException)
            {
                error = checksumException.Error;
                return true;
            }
            if (current is AwsChunkedTrailerException trailerException)
            {
                error = BadDigest(trailerException.Message);
                return true;
            }

            current = current.InnerException;
        }

        error = default;
        return false;
    }

    private static bool TryReadTrailerAlgorithm(
        HttpRequest request,
        out FlexibleChecksumAlgorithm? algorithm,
        out FlexibleChecksumError error)
    {
        algorithm = null;
        error = default;

        if (!request.Headers.TryGetValue("x-amz-trailer", out var values))
        {
            return true;
        }

        foreach (var rawValue in values)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            var remaining = rawValue.AsSpan();
            while (!remaining.IsEmpty)
            {
                var comma = remaining.IndexOf(',');
                var name = (comma < 0 ? remaining : remaining[..comma]).Trim();
                remaining = comma < 0 ? default : remaining[(comma + 1)..];
                if (name.IsEmpty)
                {
                    error = InvalidArgument("Header x-amz-trailer contains an empty trailer name.");
                    return false;
                }

                if (!name.StartsWith("x-amz-checksum-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryParseHeaderName(name, out var parsed))
                {
                    error = InvalidArgument($"Checksum trailer '{name.ToString()}' is not supported.");
                    return false;
                }

                if (algorithm is not null)
                {
                    error = InvalidArgument("Only one x-amz-checksum-* trailer may be supplied for PutObject.");
                    return false;
                }

                algorithm = parsed;
            }
        }

        return true;
    }

    private static bool TryReadSingleHeader(
        HttpRequest request,
        string name,
        out string value,
        out bool duplicate)
    {
        value = string.Empty;
        duplicate = false;
        if (!request.Headers.TryGetValue(name, out var values))
        {
            return false;
        }

        var found = false;
        foreach (var rawValue in values)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            if (found)
            {
                duplicate = true;
                return false;
            }

            value = rawValue.Trim();
            found = true;
        }

        return found;
    }

    private static bool TryParseAlgorithm(string value, out FlexibleChecksumAlgorithm algorithm)
    {
        if (value.Equals("CRC32", StringComparison.OrdinalIgnoreCase))
        {
            algorithm = FlexibleChecksumAlgorithm.Crc32;
            return true;
        }
        if (value.Equals("CRC32C", StringComparison.OrdinalIgnoreCase))
        {
            algorithm = FlexibleChecksumAlgorithm.Crc32C;
            return true;
        }
        if (value.Equals("CRC64NVME", StringComparison.OrdinalIgnoreCase))
        {
            algorithm = FlexibleChecksumAlgorithm.Crc64Nvme;
            return true;
        }
        if (value.Equals("SHA1", StringComparison.OrdinalIgnoreCase))
        {
            algorithm = FlexibleChecksumAlgorithm.Sha1;
            return true;
        }
        if (value.Equals("SHA256", StringComparison.OrdinalIgnoreCase))
        {
            algorithm = FlexibleChecksumAlgorithm.Sha256;
            return true;
        }

        algorithm = default;
        return false;
    }

    private static bool TryParseHeaderName(ReadOnlySpan<char> value, out FlexibleChecksumAlgorithm algorithm)
    {
        foreach (var candidate in Algorithms)
        {
            if (value.Equals(GetHeaderName(candidate), StringComparison.OrdinalIgnoreCase))
            {
                algorithm = candidate;
                return true;
            }
        }

        algorithm = default;
        return false;
    }

    private static bool TryParseHeaderName(string value, out FlexibleChecksumAlgorithm algorithm) =>
        TryParseHeaderName(value.AsSpan(), out algorithm);

    private static bool TryDecodeDigest(string value, int expectedLength, out byte[]? digest)
    {
        Span<byte> decoded = stackalloc byte[32];
        if (!Convert.TryFromBase64String(value, decoded, out var bytesWritten) || bytesWritten != expectedLength)
        {
            digest = null;
            return false;
        }

        digest = decoded[..bytesWritten].ToArray();
        return true;
    }

    private static string GetToken(FlexibleChecksumAlgorithm algorithm) => algorithm switch
    {
        FlexibleChecksumAlgorithm.Crc32 => "CRC32",
        FlexibleChecksumAlgorithm.Crc32C => "CRC32C",
        FlexibleChecksumAlgorithm.Crc64Nvme => "CRC64NVME",
        FlexibleChecksumAlgorithm.Sha1 => "SHA1",
        FlexibleChecksumAlgorithm.Sha256 => "SHA256",
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    private static string GetHeaderName(FlexibleChecksumAlgorithm algorithm) => algorithm switch
    {
        FlexibleChecksumAlgorithm.Crc32 => "x-amz-checksum-crc32",
        FlexibleChecksumAlgorithm.Crc32C => "x-amz-checksum-crc32c",
        FlexibleChecksumAlgorithm.Crc64Nvme => "x-amz-checksum-crc64nvme",
        FlexibleChecksumAlgorithm.Sha1 => "x-amz-checksum-sha1",
        FlexibleChecksumAlgorithm.Sha256 => "x-amz-checksum-sha256",
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    private static int GetDigestLength(FlexibleChecksumAlgorithm algorithm) => algorithm switch
    {
        FlexibleChecksumAlgorithm.Crc32 or FlexibleChecksumAlgorithm.Crc32C => 4,
        FlexibleChecksumAlgorithm.Crc64Nvme => 8,
        FlexibleChecksumAlgorithm.Sha1 => 20,
        FlexibleChecksumAlgorithm.Sha256 => 32,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    private static FlexibleChecksumError InvalidArgument(string message) => new("InvalidArgument", message);
    private static FlexibleChecksumError BadDigest(string message) => new("BadDigest", message);

    private sealed class ValidatingStream : Stream
    {
        private readonly Stream _inner;
        private readonly FlexibleChecksumAccumulator _accumulator;
        private readonly byte[]? _headerDigest;
        private readonly string? _trailerHeader;
        private readonly AwsChunkedDecoder? _decoder;
        private readonly byte[] _lookahead = new byte[1];
        private byte _pendingByte;
        private bool _hasPendingByte;
        private bool _validated;
        private bool _disposed;

        public ValidatingStream(
            Stream inner,
            FlexibleChecksumAlgorithm algorithm,
            byte[]? headerDigest,
            string? trailerHeader,
            AwsChunkedDecoder? decoder)
        {
            _inner = inner;
            _accumulator = new FlexibleChecksumAccumulator(algorithm);
            _headerDigest = headerDigest;
            _trailerHeader = trailerHeader;
            _decoder = decoder;
        }

        public override bool CanRead => !_disposed && _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (count == 0)
            {
                return 0;
            }

            if (!_hasPendingByte && !ReadInitialPendingByte())
            {
                return 0;
            }

            if (count == 1)
            {
                var next = _inner.Read(_lookahead, 0, 1);
                if (next == 0)
                {
                    Validate();
                    buffer[offset] = _pendingByte;
                    _hasPendingByte = false;
                    return 1;
                }

                _accumulator.Append(_lookahead);
                buffer[offset] = _pendingByte;
                _pendingByte = _lookahead[0];
                return 1;
            }

            buffer[offset] = _pendingByte;
            var read = _inner.Read(buffer, offset + 1, count - 1);
            if (read == 0)
            {
                Validate();
                _hasPendingByte = false;
                return 1;
            }

            _accumulator.Append(buffer.AsSpan(offset + 1, read));
            _pendingByte = buffer[offset + read];
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.IsEmpty)
            {
                return 0;
            }

            if (!_hasPendingByte && !await ReadInitialPendingByteAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            if (buffer.Length == 1)
            {
                var next = await _inner.ReadAsync(_lookahead, cancellationToken).ConfigureAwait(false);
                if (next == 0)
                {
                    Validate();
                    buffer.Span[0] = _pendingByte;
                    _hasPendingByte = false;
                    return 1;
                }

                _accumulator.Append(_lookahead);
                buffer.Span[0] = _pendingByte;
                _pendingByte = _lookahead[0];
                return 1;
            }

            buffer.Span[0] = _pendingByte;
            var read = await _inner.ReadAsync(buffer[1..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                Validate();
                _hasPendingByte = false;
                return 1;
            }

            _accumulator.Append(buffer.Span.Slice(1, read));
            _pendingByte = buffer.Span[read];
            return read;
        }

        private bool ReadInitialPendingByte()
        {
            var read = _inner.Read(_lookahead, 0, 1);
            if (read == 0)
            {
                Validate();
                return false;
            }

            _accumulator.Append(_lookahead);
            _pendingByte = _lookahead[0];
            _hasPendingByte = true;
            return true;
        }

        private async ValueTask<bool> ReadInitialPendingByteAsync(CancellationToken cancellationToken)
        {
            var read = await _inner.ReadAsync(_lookahead, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                Validate();
                return false;
            }

            _accumulator.Append(_lookahead);
            _pendingByte = _lookahead[0];
            _hasPendingByte = true;
            return true;
        }

        private void Validate()
        {
            if (_validated)
            {
                return;
            }

            byte[] expected;
            if (_headerDigest is not null)
            {
                expected = _headerDigest;
            }
            else
            {
                if (_trailerHeader is null || _decoder is null ||
                    !_decoder.TryGetTrailerValue(_trailerHeader, out var encoded) ||
                    !TryDecodeDigest(encoded, _accumulator.DigestLength, out var trailerDigest))
                {
                    throw new FlexibleChecksumException(BadDigest(
                        $"Trailer {_trailerHeader ?? "checksum"} is missing, malformed, or has the wrong digest length."));
                }

                expected = trailerDigest!;
            }

            var actual = _accumulator.Complete();
            _validated = true;
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                throw new FlexibleChecksumException(BadDigest(
                    "The checksum you specified did not match what the proxy received."));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (disposing)
            {
                _accumulator.Dispose();
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FlexibleChecksumAccumulator : IDisposable
    {
        private static readonly uint[] Crc32Table = BuildCrc32Table(0xEDB88320u);
        private static readonly uint[] Crc32CTable = BuildCrc32Table(0x82F63B78u);
        private static readonly ulong[] Crc64NvmeTable = BuildCrc64Table(0x9A6C9329AC4BC9B5ul);

        private readonly FlexibleChecksumAlgorithm _algorithm;
        private readonly IncrementalHash? _hash;
        private uint _crc32 = uint.MaxValue;
        private ulong _crc64 = ulong.MaxValue;
        private byte[]? _completed;

        public FlexibleChecksumAccumulator(FlexibleChecksumAlgorithm algorithm)
        {
            _algorithm = algorithm;
            _hash = algorithm switch
            {
                FlexibleChecksumAlgorithm.Sha1 => IncrementalHash.CreateHash(HashAlgorithmName.SHA1),
                FlexibleChecksumAlgorithm.Sha256 => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
                _ => null,
            };
        }

        public int DigestLength => GetDigestLength(_algorithm);

        public void Append(ReadOnlySpan<byte> bytes)
        {
            if (_completed is not null)
            {
                throw new InvalidOperationException("The checksum has already been completed.");
            }

            switch (_algorithm)
            {
                case FlexibleChecksumAlgorithm.Crc32:
                    foreach (var value in bytes)
                    {
                        _crc32 = Crc32Table[(byte)(_crc32 ^ value)] ^ (_crc32 >> 8);
                    }
                    break;
                case FlexibleChecksumAlgorithm.Crc32C:
                    foreach (var value in bytes)
                    {
                        _crc32 = Crc32CTable[(byte)(_crc32 ^ value)] ^ (_crc32 >> 8);
                    }
                    break;
                case FlexibleChecksumAlgorithm.Crc64Nvme:
                    foreach (var value in bytes)
                    {
                        _crc64 = Crc64NvmeTable[(byte)(_crc64 ^ value)] ^ (_crc64 >> 8);
                    }
                    break;
                case FlexibleChecksumAlgorithm.Sha1:
                case FlexibleChecksumAlgorithm.Sha256:
                    _hash!.AppendData(bytes);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public byte[] Complete()
        {
            if (_completed is not null)
            {
                return _completed;
            }

            _completed = new byte[DigestLength];
            switch (_algorithm)
            {
                case FlexibleChecksumAlgorithm.Crc32:
                case FlexibleChecksumAlgorithm.Crc32C:
                    BinaryPrimitives.WriteUInt32BigEndian(_completed, _crc32 ^ uint.MaxValue);
                    break;
                case FlexibleChecksumAlgorithm.Crc64Nvme:
                    BinaryPrimitives.WriteUInt64BigEndian(_completed, _crc64 ^ ulong.MaxValue);
                    break;
                case FlexibleChecksumAlgorithm.Sha1:
                case FlexibleChecksumAlgorithm.Sha256:
                    if (!_hash!.TryGetHashAndReset(_completed, out var written) || written != _completed.Length)
                    {
                        throw new CryptographicException("Unable to finalize the flexible checksum.");
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return _completed;
        }

        public void Dispose() => _hash?.Dispose();

        private static uint[] BuildCrc32Table(uint polynomial)
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value >> 1) ^ ((value & 1) == 0 ? 0 : polynomial);
                }
                table[index] = value;
            }
            return table;
        }

        private static ulong[] BuildCrc64Table(ulong polynomial)
        {
            var table = new ulong[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = (ulong)index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value >> 1) ^ ((value & 1) == 0 ? 0 : polynomial);
                }
                table[index] = value;
            }
            return table;
        }
    }

    private sealed class FlexibleChecksumException : RequestContentValidationException
    {
        public FlexibleChecksumException(FlexibleChecksumError error)
            : base(error.Message)
        {
            Error = error;
        }

        public FlexibleChecksumError Error { get; }
    }
}
