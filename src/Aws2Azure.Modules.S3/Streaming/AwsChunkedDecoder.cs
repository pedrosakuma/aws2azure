using System.Buffers;
using System.Globalization;
using System.Text;
using Aws2Azure.Core.SigV4;

namespace Aws2Azure.Modules.S3.Streaming;

/// <summary>
/// Context required for verifying aws-chunked payload signatures.
/// </summary>
public sealed class ChunkSigningContext
{
    public required byte[] SigningKey { get; init; }
    public required string AmzDate { get; init; }
    public required string CredentialScope { get; init; }
    public required string SeedSignature { get; init; }
}

/// <summary>
/// A read-only stream that decodes AWS chunked encoding. Supports both the
/// signed (<c>STREAMING-AWS4-HMAC-SHA256-PAYLOAD</c>) and unsigned
/// (<c>STREAMING-UNSIGNED-PAYLOAD</c>) framings, with or without a trailing
/// header section (the <c>-TRAILER</c> variants emitted by modern AWS SDKs
/// when flexible checksums are enabled).
///
/// <para>
/// Signed chunk:   <c>&lt;hex-size&gt;;chunk-signature=&lt;sig&gt;\r\n&lt;data&gt;\r\n</c><br/>
/// Unsigned chunk: <c>&lt;hex-size&gt;\r\n&lt;data&gt;\r\n</c><br/>
/// Final chunk:    <c>0[;chunk-signature=&lt;sig&gt;]\r\n</c> followed by either a
/// bare <c>\r\n</c> (no trailer) or zero or more trailing header lines
/// (e.g. <c>x-amz-checksum-crc32:&lt;value&gt;\r\n</c> and, when signed,
/// <c>x-amz-trailer-signature:&lt;sig&gt;\r\n</c>) terminated by a blank line.
/// </para>
/// </summary>
public sealed class AwsChunkedDecoder : Stream
{
    private const int MaxChunkHeaderSize = 256; // generous for "hex;chunk-signature=64hex\r\n"
    private const int ReadAheadBufferSize = 1024;

    // AWS SDK default chunk size is 64KB-128KB; S3 allows up to 5GB objects but
    // individual chunks are typically much smaller. We cap at 64MB to prevent
    // malicious clients from forcing huge buffer allocations when signature
    // verification is enabled. Non-verified streams don't buffer.
    private const int MaxSignedChunkSize = 64 * 1024 * 1024;

    private readonly Stream _inner;
    private readonly ChunkSigningContext? _signingContext;
    private readonly bool _leaveOpen;
    private readonly bool _expectTrailer;

    private byte[]? _headerBuffer;
    private int _headerBufferLen;

    private byte[]? _readAheadBuffer;
    private int _readAheadPos;
    private int _readAheadLen;
    private readonly byte[] _consumeBuffer = new byte[2];

    private byte[]? _chunkBuffer;
    private int _chunkBufferPos;
    private int _chunkBufferLen;

    private long _remainingInChunk;
    private bool _needChunkTrailerCrLf;
    private bool _reachedFinalChunk;
    private bool _disposed;

    private string _previousSignature;
    private long _totalBytesRead;

    private static readonly string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public AwsChunkedDecoder(Stream inner, ChunkSigningContext? signingContext = null, bool leaveOpen = false, bool expectTrailer = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _signingContext = signingContext;
        _leaveOpen = leaveOpen;
        _expectTrailer = expectTrailer;
        _previousSignature = signingContext?.SeedSignature ?? string.Empty;
        _headerBuffer = ArrayPool<byte>.Shared.Rent(MaxChunkHeaderSize);
        _readAheadBuffer = ArrayPool<byte>.Shared.Rent(ReadAheadBufferSize);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _totalBytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        while (true)
        {
            if (_reachedFinalChunk)
                return 0;

            // If we have buffered chunk data, serve from there first
            if (_chunkBufferLen > 0)
            {
                var toCopy = Math.Min(buffer.Length, _chunkBufferLen - _chunkBufferPos);
                _chunkBuffer.AsSpan(_chunkBufferPos, toCopy).CopyTo(buffer.Span);
                _chunkBufferPos += toCopy;
                if (_chunkBufferPos >= _chunkBufferLen)
                {
                    _chunkBufferPos = 0;
                    _chunkBufferLen = 0;
                }
                _totalBytesRead += toCopy;
                return toCopy;
            }

            // Skip chunk trailer CRLF if needed
            if (_needChunkTrailerCrLf)
            {
                await ConsumeExactAsync(2, cancellationToken).ConfigureAwait(false);
                _needChunkTrailerCrLf = false;
            }

            // Need to read next chunk header?
            if (_remainingInChunk == 0)
            {
                var (chunkSize, signature) = await ReadChunkHeaderAsync(cancellationToken).ConfigureAwait(false);

                if (chunkSize == 0)
                {
                    // Verify the final (empty-payload) chunk signature if signing.
                    if (_signingContext is not null)
                    {
                        VerifyChunkSignature(ReadOnlySpan<byte>.Empty, signature);
                    }

                    if (_expectTrailer)
                    {
                        // -TRAILER streams emit zero or more trailing header lines
                        // (x-amz-checksum-*, and x-amz-trailer-signature when
                        // signed) terminated by a blank line. Consume and discard
                        // them — Azure computes its own content integrity.
                        await ConsumeTrailerAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        // No trailer: a bare CRLF terminates the stream.
                        await ConsumeExactAsync(2, cancellationToken).ConfigureAwait(false);
                    }

                    _reachedFinalChunk = true;
                    return 0;
                }

                _remainingInChunk = chunkSize;

                // If verifying signatures, we need to buffer the entire chunk to compute hash
                if (_signingContext is not null)
                {
                    // Prevent malicious clients from forcing huge allocations
                    if (chunkSize > MaxSignedChunkSize)
                        throw new InvalidDataException($"aws-chunked chunk size {chunkSize} exceeds maximum {MaxSignedChunkSize}");

                    EnsureChunkBuffer((int)chunkSize);

                    var totalRead = 0;
                    while (totalRead < chunkSize)
                    {
                        var read = await ReadInnerAsync(_chunkBuffer.AsMemory(totalRead, (int)chunkSize - totalRead), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                            throw new InvalidDataException("Unexpected end of aws-chunked stream");
                        totalRead += read;
                    }

                    // Consume chunk trailer CRLF
                    await ConsumeExactAsync(2, cancellationToken).ConfigureAwait(false);

                    // Verify signature
                    VerifyChunkSignature(_chunkBuffer.AsSpan(0, (int)chunkSize), signature);

                    _chunkBufferPos = 0;
                    _chunkBufferLen = (int)chunkSize;
                    _remainingInChunk = 0;

                    // Loop back to serve from buffer
                    continue;
                }
            }

            // Read directly from chunk (no signature verification path)
            if (_remainingInChunk > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, _remainingInChunk);
                var read = await ReadInnerAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new InvalidDataException("Unexpected end of aws-chunked stream");

                _remainingInChunk -= read;
                _totalBytesRead += read;

                if (_remainingInChunk == 0)
                    _needChunkTrailerCrLf = true;

                return read;
            }
        }
    }

    private void EnsureChunkBuffer(int size)
    {
        if (_chunkBuffer is null || _chunkBuffer.Length < size)
        {
            if (_chunkBuffer is not null)
                ArrayPool<byte>.Shared.Return(_chunkBuffer);
            _chunkBuffer = ArrayPool<byte>.Shared.Rent(size);
        }
    }

    private async ValueTask<(long ChunkSize, string Signature)> ReadChunkHeaderAsync(CancellationToken cancellationToken)
    {
        var lineLen = await ReadCrlfLineAsync(cancellationToken).ConfigureAwait(false);

        // Parse: <hex>[;chunk-signature=<sig>]
        var headerSpan = _headerBuffer.AsSpan(0, lineLen);

        var semicolonIdx = headerSpan.IndexOf((byte)';');
        var sizeSpan = semicolonIdx < 0 ? headerSpan : headerSpan[..semicolonIdx];

        var sizeHex = Encoding.ASCII.GetString(sizeSpan);
        if (!long.TryParse(sizeHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize))
            throw new InvalidDataException($"Invalid aws-chunked size: {sizeHex}");

        // Unsigned framings (STREAMING-UNSIGNED-PAYLOAD[-TRAILER]) omit the
        // chunk-signature extension entirely.
        if (semicolonIdx < 0)
            return (chunkSize, string.Empty);

        var afterSemicolon = headerSpan[(semicolonIdx + 1)..];

        // Find chunk-signature=
        ReadOnlySpan<byte> prefixSpan = "chunk-signature="u8;
        if (!afterSemicolon.StartsWith(prefixSpan))
            throw new InvalidDataException("aws-chunked header missing chunk-signature");

        var signature = Encoding.ASCII.GetString(afterSemicolon[prefixSpan.Length..]);

        return (chunkSize, signature);
    }

    /// <summary>
    /// Reads bytes from the inner stream up to and including the next CRLF,
    /// returning the length of the line excluding the trailing CRLF. The line
    /// content is left in <see cref="_headerBuffer"/>.
    /// </summary>
    private async ValueTask<int> ReadCrlfLineAsync(CancellationToken cancellationToken)
    {
        _headerBufferLen = 0;
        var headerBuffer = _headerBuffer;
        if (headerBuffer is null)
            throw new ObjectDisposedException(nameof(AwsChunkedDecoder));

        var readAheadBuffer = _readAheadBuffer;
        if (readAheadBuffer is null)
            throw new ObjectDisposedException(nameof(AwsChunkedDecoder));

        while (true)
        {
            if (_readAheadPos >= _readAheadLen)
            {
                await FillReadAheadAsync(cancellationToken).ConfigureAwait(false);
            }

            while (_readAheadPos < _readAheadLen)
            {
                if (_headerBufferLen >= MaxChunkHeaderSize)
                    throw new InvalidDataException("aws-chunked header too long");

                headerBuffer[_headerBufferLen++] = readAheadBuffer[_readAheadPos++];

                // Check for \r\n at end
                if (_headerBufferLen >= 2 &&
                    headerBuffer[_headerBufferLen - 2] == '\r' &&
                    headerBuffer[_headerBufferLen - 1] == '\n')
                {
                    return _headerBufferLen - 2; // exclude \r\n
                }
            }
        }
    }

    /// <summary>
    /// Consumes the trailing header section that follows the terminal zero-size
    /// chunk in a <c>-TRAILER</c> stream: zero or more <c>name:value</c> lines
    /// ended by a blank line. The values (checksums, trailer signature) are
    /// discarded — Azure Blob validates content integrity independently.
    /// </summary>
    private async ValueTask ConsumeTrailerAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var lineLen = await ReadCrlfLineAsync(cancellationToken).ConfigureAwait(false);
            if (lineLen == 0)
                return; // blank line terminates the trailer
        }
    }

    private void VerifyChunkSignature(ReadOnlySpan<byte> chunkData, string providedSignature)
    {
        if (_signingContext is null)
            return;

        // String to sign for chunk:
        // AWS4-HMAC-SHA256-PAYLOAD
        // <timestamp>
        // <credential-scope>
        // <previous-signature>
        // <sha256-empty-string>
        // <sha256-chunk-data>

        var chunkHash = SigningKey.Sha256Hex(chunkData);

        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256-PAYLOAD",
            _signingContext.AmzDate,
            _signingContext.CredentialScope,
            _previousSignature,
            EmptySha256,
            chunkHash);

        var expectedSigBytes = SigningKey.HmacSha256(_signingContext.SigningKey, Encoding.UTF8.GetBytes(stringToSign));
        var expectedSig = SigningKey.ToLowerHex(expectedSigBytes);

        if (!string.Equals(expectedSig, providedSignature, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"aws-chunked signature mismatch");
        }

        _previousSignature = providedSignature;
    }

    private async ValueTask ConsumeExactAsync(int count, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await ReadInnerAsync(
                _consumeBuffer.AsMemory(0, Math.Min(_consumeBuffer.Length, count - totalRead)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new InvalidDataException("Unexpected end of aws-chunked stream");
            totalRead += read;
        }
    }

    private async ValueTask<int> ReadInnerAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_readAheadPos < _readAheadLen)
        {
            var readAheadBuffer = _readAheadBuffer;
            if (readAheadBuffer is null)
                throw new ObjectDisposedException(nameof(AwsChunkedDecoder));

            var available = _readAheadLen - _readAheadPos;
            var toCopy = Math.Min(buffer.Length, available);
            readAheadBuffer.AsSpan(_readAheadPos, toCopy).CopyTo(buffer.Span);
            _readAheadPos += toCopy;
            return toCopy;
        }

        return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask FillReadAheadAsync(CancellationToken cancellationToken)
    {
        var readAheadBuffer = _readAheadBuffer;
        if (readAheadBuffer is null)
            throw new ObjectDisposedException(nameof(AwsChunkedDecoder));

        var read = await _inner.ReadAsync(readAheadBuffer.AsMemory(0, ReadAheadBufferSize), cancellationToken).ConfigureAwait(false);
        if (read == 0)
            throw new InvalidDataException("Unexpected end of aws-chunked stream while reading header");

        _readAheadPos = 0;
        _readAheadLen = read;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;

        if (disposing)
        {
            if (_headerBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_headerBuffer);
                _headerBuffer = null;
            }

            if (_readAheadBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_readAheadBuffer);
                _readAheadBuffer = null;
            }

            if (_chunkBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_chunkBuffer);
                _chunkBuffer = null;
            }

            if (!_leaveOpen)
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
