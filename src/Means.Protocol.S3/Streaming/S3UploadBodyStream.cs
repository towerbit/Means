using System.Security.Cryptography;
using Means.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Means.Protocol.S3;

/// <summary>
/// The payload stream for <c>PutObject</c> and <c>UploadPart</c>: it strips <c>aws-chunked</c>
/// framing when present and verifies the integrity values the client attached to the upload.
/// </summary>
/// <remarks>
/// Verification happens while the payload streams to storage, so a corrupted or truncated body
/// fails the request instead of being committed as a valid object. AWS SDKs send the checksum in an
/// <c>aws-chunked</c> trailer, which only arrives after the last payload byte; the computed value is
/// therefore compared at end of stream and then echoed back on the response.
/// </remarks>
public sealed class S3UploadBodyStream : Stream
{
    private readonly Stream _inner;
    private readonly AwsChunkedStream? _chunked;
    private readonly S3ChecksumCalculator? _checksum;
    private readonly string? _expectedChecksum;
    private readonly string? _trailerChecksumName;
    private readonly IncrementalHash? _md5;
    private readonly byte[]? _expectedMd5;
    private bool _validated;

    private S3UploadBodyStream(
        Stream inner,
        AwsChunkedStream? chunked,
        S3ChecksumCalculator? checksum,
        string? expectedChecksum,
        string? trailerChecksumName,
        byte[]? expectedMd5)
    {
        _inner = inner;
        _chunked = chunked;
        _checksum = checksum;
        _expectedChecksum = expectedChecksum;
        _trailerChecksumName = trailerChecksumName;
        _expectedMd5 = expectedMd5;
        _md5 = expectedMd5 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    }

    /// <summary>The checksum header to echo on the response, available once the body is consumed.</summary>
    public string? ChecksumHeaderName => _checksum?.HeaderName;

    /// <summary>The computed base64 checksum, available once the body is consumed.</summary>
    public string? ChecksumValue => _validated ? _checksum?.ToBase64() : null;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <summary>
    /// Builds the payload stream for an upload request, wiring up chunk decoding and whichever
    /// integrity values the client negotiated.
    /// </summary>
    public static S3UploadBodyStream Create(HttpRequest request)
    {
        var chunked = AwsChunkedStream.IsChunkedUpload(request)
            ? new AwsChunkedStream(request.Body, AwsChunkedStream.GetDecodedContentLength(request))
            : null;

        var (checksum, expectedChecksum, trailerName) = ResolveChecksum(request);
        return new S3UploadBodyStream(
            chunked ?? request.Body,
            chunked,
            checksum,
            expectedChecksum,
            trailerName,
            ParseContentMd5(request));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        if (read > 0)
        {
            var span = buffer.Span[..read];
            _checksum?.Append(span);
            _md5?.AppendData(span);
            return read;
        }

        Validate();
        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(new Memory<byte>(buffer, offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Echoes the negotiated checksum so SDK-side upload verification can complete.</summary>
    public void ApplyChecksumHeaders(HttpResponse response)
    {
        if (ChecksumValue is { } value && ChecksumHeaderName is { } header)
        {
            response.Headers[header] = value;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _checksum?.Dispose();
            _md5?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Validate()
    {
        if (_validated)
        {
            return;
        }

        _validated = true;

        if (_md5 is not null && !CryptographicOperations.FixedTimeEquals(_md5.GetCurrentHash(), _expectedMd5!))
        {
            throw new MeansException(
                MeansErrorCodes.BadDigest,
                "The Content-MD5 header does not match the uploaded payload.",
                400);
        }

        if (_checksum is null)
        {
            return;
        }

        var expected = _expectedChecksum ?? ReadTrailerChecksum();
        if (expected is null)
        {
            return;
        }

        if (!string.Equals(expected, _checksum.ToBase64(), StringComparison.Ordinal))
        {
            throw new MeansException(
                MeansErrorCodes.XAmzContentChecksumMismatch,
                $"The {S3Checksums.Name(_checksum.Algorithm)} checksum sent with the request does not match the uploaded payload.",
                400);
        }
    }

    private string? ReadTrailerChecksum()
    {
        return _trailerChecksumName is not null
            && _chunked is not null
            && _chunked.Trailers.TryGetValue(_trailerChecksumName, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
    }

    /// <summary>
    /// Determines which checksum to compute, and the expected value when the client already sent it
    /// as a header. Algorithms Means does not recognise are ignored rather than rejected so that a
    /// newer client still uploads successfully, just without server-side verification.
    /// </summary>
    private static (S3ChecksumCalculator? Calculator, string? Expected, string? TrailerName) ResolveChecksum(HttpRequest request)
    {
        foreach (var header in request.Headers)
        {
            if (header.Key.StartsWith("x-amz-checksum-", StringComparison.OrdinalIgnoreCase)
                && S3Checksums.TryParse(header.Key, out var headerAlgorithm))
            {
                var value = header.Value.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return (S3Checksums.CreateCalculator(headerAlgorithm), value.Trim(), null);
                }
            }
        }

        if (S3Checksums.TryParseTrailerList(request.Headers[S3Checksums.TrailerHeader].ToString(), out var trailerName, out var trailerAlgorithm))
        {
            return (S3Checksums.CreateCalculator(trailerAlgorithm), null, trailerName);
        }

        if (S3Checksums.TryParse(request.Headers[S3Checksums.SdkAlgorithmHeader].ToString(), out var sdkAlgorithm))
        {
            return (S3Checksums.CreateCalculator(sdkAlgorithm), null, S3Checksums.HeaderName(sdkAlgorithm));
        }

        return (null, null, null);
    }

    private static byte[]? ParseContentMd5(HttpRequest request)
    {
        var value = request.Headers[HeaderNames.ContentMD5].ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Span<byte> digest = stackalloc byte[16];
        return Convert.TryFromBase64String(value.Trim(), digest, out var written) && written == digest.Length
            ? digest.ToArray()
            : throw new MeansException(
                MeansErrorCodes.InvalidDigest,
                "The Content-MD5 header is not a base64-encoded 128-bit digest.",
                400);
    }
}
