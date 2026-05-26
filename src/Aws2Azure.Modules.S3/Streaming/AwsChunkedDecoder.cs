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
/// A read-only stream that decodes AWS chunked encoding (STREAMING-AWS4-HMAC-SHA256-PAYLOAD).
/// Each chunk has format: &lt;hex-size&gt;;chunk-signature=&lt;sig&gt;\r\n&lt;data&gt;\r\n
/// The final chunk is: 0;chunk-signature=&lt;sig&gt;\r\n\r\n
/// </summary>
public sealed class AwsChunkedDecoder : Stream
{
    private const int MaxChunkHeaderSize = 256; // generous for "hex;chunk-signature=64hex\r\n"

    // AWS SDK default chunk size is 64KB-128KB; S3 allows up to 5GB objects but
    // individual chunks are typically much smaller. We cap at 64MB to prevent
    // malicious clients from forcing huge buffer allocations when signature
    // verification is enabled. Non-verified streams don't buffer.
    private const int MaxSignedChunkSize = 64 * 1024 * 1024;

    private readonly Stream _inner;
    private readonly ChunkSigningContext? _signingContext;
    private readonly bool _leaveOpen;

    private byte[]? _headerBuffer;
    private int _headerBufferLen;

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

    public AwsChunkedDecoder(Stream inner, ChunkSigningContext? signingContext = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _signingContext = signingContext;
        _leaveOpen = leaveOpen;
        _previousSignature = signingContext?.SeedSignature ?? string.Empty;
        _headerBuffer = ArrayPool<byte>.Shared.Rent(MaxChunkHeaderSize);
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
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

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
                    // Final chunk - consume trailing CRLF and mark done
                    await ConsumeExactAsync(2, cancellationToken).ConfigureAwait(false); // final \r\n
                    _reachedFinalChunk = true;

                    // Verify final chunk signature if signing context provided
                    if (_signingContext is not null)
                    {
                        VerifyChunkSignature(ReadOnlySpan<byte>.Empty, signature);
                    }

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
                        var read = await _inner.ReadAsync(_chunkBuffer.AsMemory(totalRead, (int)chunkSize - totalRead), cancellationToken).ConfigureAwait(false);
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
                var read = await _inner.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
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
        // Read until we find \r\n
        _headerBufferLen = 0;

        while (true)
        {
            if (_headerBufferLen >= MaxChunkHeaderSize)
                throw new InvalidDataException("aws-chunked header too long");

            var read = await _inner.ReadAsync(_headerBuffer.AsMemory(_headerBufferLen, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new InvalidDataException("Unexpected end of aws-chunked stream while reading header");

            _headerBufferLen++;

            // Check for \r\n at end
            if (_headerBufferLen >= 2 &&
                _headerBuffer![_headerBufferLen - 2] == '\r' &&
                _headerBuffer[_headerBufferLen - 1] == '\n')
            {
                break;
            }
        }

        // Parse: <hex>;chunk-signature=<sig>\r\n
        var headerSpan = _headerBuffer.AsSpan(0, _headerBufferLen - 2); // exclude \r\n

        var semicolonIdx = headerSpan.IndexOf((byte)';');
        if (semicolonIdx < 0)
            throw new InvalidDataException("aws-chunked header missing semicolon");

        var sizeHex = Encoding.ASCII.GetString(headerSpan[..semicolonIdx]);
        if (!long.TryParse(sizeHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize))
            throw new InvalidDataException($"Invalid aws-chunked size: {sizeHex}");

        var afterSemicolon = headerSpan[(semicolonIdx + 1)..];

        // Find chunk-signature=
        ReadOnlySpan<byte> prefixSpan = "chunk-signature="u8;
        if (!afterSemicolon.StartsWith(prefixSpan))
            throw new InvalidDataException("aws-chunked header missing chunk-signature");

        var signature = Encoding.ASCII.GetString(afterSemicolon[prefixSpan.Length..]);

        return (chunkSize, signature);
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
        var buf = new byte[count];
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await _inner.ReadAsync(buf.AsMemory(totalRead, count - totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new InvalidDataException("Unexpected end of aws-chunked stream");
            totalRead += read;
        }
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
