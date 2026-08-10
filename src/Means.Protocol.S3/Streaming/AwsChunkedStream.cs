using Means.Core;
using Microsoft.AspNetCore.Http;

namespace Means.Protocol.S3;

/// <summary>
/// Decodes the <c>aws-chunked</c> transfer encoding used by AWS SigV4 streaming payloads.
/// AWS SDKs (for example the AWS SDK for .NET v4) upload object content as
/// <c>&lt;hex-size&gt;;chunk-signature=&lt;sig&gt;\r\n&lt;payload&gt;\r\n</c> frames terminated by a zero-length
/// frame plus optional trailing headers. Without decoding, those frame headers would be stored as
/// object content.
/// </summary>
public sealed class AwsChunkedStream : Stream
{
    private const int MaxHeaderLength = 8 * 1024;

    private readonly Stream _inner;
    private readonly byte[] _buffer;
    private int _bufferStart;
    private int _bufferEnd;
    private long _chunkRemaining;
    private bool _finished;
    private long _decodedLength;

    public AwsChunkedStream(Stream inner, int bufferSize = 16 * 1024)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _buffer = new byte[Math.Max(bufferSize, 1024)];
    }

    /// <summary>Total number of payload bytes produced so far.</summary>
    public long DecodedLength => _decodedLength;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(new Memory<byte>(buffer, offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (true)
        {
            if (_finished)
            {
                return 0;
            }

            if (_chunkRemaining == 0)
            {
                if (!await BeginNextChunkAsync(cancellationToken))
                {
                    return 0;
                }

                continue;
            }

            if (!await EnsureBufferedAsync(1, cancellationToken))
            {
                throw MalformedChunkedBody();
            }

            var available = (int)Math.Min(Math.Min(buffer.Length, _bufferEnd - _bufferStart), _chunkRemaining);
            _buffer.AsSpan(_bufferStart, available).CopyTo(buffer.Span);
            _bufferStart += available;
            _chunkRemaining -= available;
            _decodedLength += available;

            if (_chunkRemaining == 0)
            {
                await ConsumeCrlfAsync(cancellationToken);
            }

            return available;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Returns true when the request body uses SigV4 streaming (<c>aws-chunked</c>) framing.
    /// </summary>
    public static bool IsChunkedUpload(HttpRequest request)
    {
        var payloadHash = request.Headers["x-amz-content-sha256"].ToString();
        if (payloadHash.StartsWith("STREAMING-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Some clients only advertise the framing through Content-Encoding.
        return request.Headers["x-amz-decoded-content-length"].Count > 0
            && ContainsAwsChunked(request.Headers.ContentEncoding.ToString());
    }

    /// <summary>
    /// Returns the client-declared decoded payload size, when present.
    /// </summary>
    public static long? GetDecodedContentLength(HttpRequest request)
    {
        var value = request.Headers["x-amz-decoded-content-length"].ToString();
        return long.TryParse(value, out var parsed) && parsed >= 0 ? parsed : null;
    }

    private static bool ContainsAwsChunked(string contentEncoding)
    {
        return contentEncoding
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => value.Equals("aws-chunked", StringComparison.OrdinalIgnoreCase));
    }

    private async ValueTask<bool> BeginNextChunkAsync(CancellationToken cancellationToken)
    {
        var header = await ReadLineAsync(cancellationToken);
        if (header is null)
        {
            // Truncated body after the last payload chunk; treat as end of stream.
            _finished = true;
            return false;
        }

        if (header.Length == 0)
        {
            // Tolerate an extra CRLF between frames.
            return true;
        }

        var size = ParseChunkSize(header);
        if (size == 0)
        {
            await SkipTrailersAsync(cancellationToken);
            _finished = true;
            return false;
        }

        _chunkRemaining = size;
        return true;
    }

    private static long ParseChunkSize(string header)
    {
        var separator = header.IndexOf(';', StringComparison.Ordinal);
        var sizeText = (separator < 0 ? header : header[..separator]).Trim();
        if (sizeText.Length is 0 or > 16
            || !long.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var size)
            || size < 0)
        {
            throw MalformedChunkedBody();
        }

        return size;
    }

    private async ValueTask SkipTrailersAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken);
            if (line is null || line.Length == 0)
            {
                return;
            }
        }
    }

    private async ValueTask ConsumeCrlfAsync(CancellationToken cancellationToken)
    {
        if (!await EnsureBufferedAsync(1, cancellationToken))
        {
            return;
        }

        if (_buffer[_bufferStart] == (byte)'\r')
        {
            _bufferStart++;
            if (await EnsureBufferedAsync(1, cancellationToken) && _buffer[_bufferStart] == (byte)'\n')
            {
                _bufferStart++;
            }

            return;
        }

        if (_buffer[_bufferStart] == (byte)'\n')
        {
            _bufferStart++;
            return;
        }

        throw MalformedChunkedBody();
    }

    private async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new System.Text.StringBuilder();
        while (true)
        {
            if (!await EnsureBufferedAsync(1, cancellationToken))
            {
                return line.Length == 0 ? null : line.ToString();
            }

            var current = _buffer[_bufferStart++];
            if (current == (byte)'\n')
            {
                return line.ToString();
            }

            if (current == (byte)'\r')
            {
                continue;
            }

            if (line.Length >= MaxHeaderLength)
            {
                throw MalformedChunkedBody();
            }

            line.Append((char)current);
        }
    }

    private async ValueTask<bool> EnsureBufferedAsync(int minimumBytes, CancellationToken cancellationToken)
    {
        while (_bufferEnd - _bufferStart < minimumBytes)
        {
            if (_bufferStart > 0)
            {
                var remaining = _bufferEnd - _bufferStart;
                if (remaining > 0)
                {
                    Array.Copy(_buffer, _bufferStart, _buffer, 0, remaining);
                }

                _bufferStart = 0;
                _bufferEnd = remaining;
            }

            var read = await _inner.ReadAsync(_buffer.AsMemory(_bufferEnd), cancellationToken);
            if (read == 0)
            {
                return _bufferEnd - _bufferStart >= minimumBytes;
            }

            _bufferEnd += read;
        }

        return true;
    }

    private static MeansException MalformedChunkedBody()
    {
        return new MeansException(MeansErrorCodes.InvalidRequest, "Malformed aws-chunked request body.", 400);
    }
}
