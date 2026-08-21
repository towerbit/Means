using System.Security.Cryptography;
using System.Text;
using Means.Core;
using Means.Protocol.S3;
using Microsoft.AspNetCore.Http;

namespace Means.UnitTests;

public sealed class S3UploadBodyStreamTests
{
    private const string Payload = "Hello from the official AWS SDK for .NET.";
    private const string PayloadCrc32 = "IgkKYA==";

    [Fact]
    public async Task DecodesChunkedUploadAndExposesTheComputedChecksum()
    {
        var request = ChunkedRequest(Payload, PayloadCrc32);
        await using var body = S3UploadBodyStream.Create(request);

        Assert.Equal(Payload, await ReadAllAsync(body));
        Assert.Equal("x-amz-checksum-crc32", body.ChecksumHeaderName);
        Assert.Equal(PayloadCrc32, body.ChecksumValue);
    }

    [Fact]
    public async Task RejectsChunkedUploadWhoseTrailerChecksumDoesNotMatch()
    {
        var request = ChunkedRequest(Payload, "AAAAAA==");
        await using var body = S3UploadBodyStream.Create(request);

        var error = await Assert.ThrowsAsync<MeansException>(() => ReadAllAsync(body));
        Assert.Equal(MeansErrorCodes.XAmzContentChecksumMismatch, error.Code);
    }

    [Fact]
    public async Task RejectsChunkedUploadShorterThanTheDeclaredDecodedLength()
    {
        var request = ChunkedRequest(Payload, PayloadCrc32);
        request.Headers["x-amz-decoded-content-length"] = (Payload.Length + 10).ToString();
        await using var body = S3UploadBodyStream.Create(request);

        var error = await Assert.ThrowsAsync<MeansException>(() => ReadAllAsync(body));
        Assert.Equal(MeansErrorCodes.IncompleteBody, error.Code);
    }

    [Fact]
    public async Task ValidatesChecksumSentAsARequestHeader()
    {
        var request = PlainRequest(Payload);
        request.Headers["x-amz-checksum-crc32"] = PayloadCrc32;
        await using var body = S3UploadBodyStream.Create(request);

        Assert.Equal(Payload, await ReadAllAsync(body));
        Assert.Equal(PayloadCrc32, body.ChecksumValue);
    }

    [Fact]
    public async Task RejectsMismatchedChecksumRequestHeader()
    {
        var request = PlainRequest(Payload);
        request.Headers["x-amz-checksum-sha256"] = Convert.ToBase64String(SHA256.HashData("other"u8));
        await using var body = S3UploadBodyStream.Create(request);

        var error = await Assert.ThrowsAsync<MeansException>(() => ReadAllAsync(body));
        Assert.Equal(MeansErrorCodes.XAmzContentChecksumMismatch, error.Code);
    }

    [Fact]
    public async Task ValidatesContentMd5()
    {
        var request = PlainRequest(Payload);
        request.Headers.ContentMD5 = Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(Payload)));
        await using var body = S3UploadBodyStream.Create(request);

        Assert.Equal(Payload, await ReadAllAsync(body));
    }

    [Fact]
    public async Task RejectsMismatchedContentMd5()
    {
        var request = PlainRequest(Payload);
        request.Headers.ContentMD5 = Convert.ToBase64String(MD5.HashData("other"u8));
        await using var body = S3UploadBodyStream.Create(request);

        var error = await Assert.ThrowsAsync<MeansException>(() => ReadAllAsync(body));
        Assert.Equal(MeansErrorCodes.BadDigest, error.Code);
    }

    [Fact]
    public void RejectsMalformedContentMd5()
    {
        var request = PlainRequest(Payload);
        request.Headers.ContentMD5 = "not-base64";

        var error = Assert.Throws<MeansException>(() => S3UploadBodyStream.Create(request));
        Assert.Equal(MeansErrorCodes.InvalidDigest, error.Code);
    }

    [Fact]
    public async Task ComputesTheChecksumAnnouncedByTheSdkHeaderWhenNoTrailerArrives()
    {
        var request = PlainRequest(Payload);
        request.Headers["x-amz-sdk-checksum-algorithm"] = "CRC32";
        await using var body = S3UploadBodyStream.Create(request);

        Assert.Equal(Payload, await ReadAllAsync(body));
        Assert.Equal(PayloadCrc32, body.ChecksumValue);
    }

    [Fact]
    public async Task IgnoresChecksumAlgorithmsItDoesNotRecognise()
    {
        var request = PlainRequest(Payload);
        request.Headers["x-amz-trailer"] = "x-amz-checksum-future";
        await using var body = S3UploadBodyStream.Create(request);

        Assert.Equal(Payload, await ReadAllAsync(body));
        Assert.Null(body.ChecksumHeaderName);
    }

    [Fact]
    public async Task ValidatesEmptyPayloads()
    {
        var request = PlainRequest("");
        request.Headers["x-amz-checksum-crc32"] = "AAAAAA==";
        await using var body = S3UploadBodyStream.Create(request);

        Assert.Equal("", await ReadAllAsync(body));
    }

    [Fact]
    public async Task DoesNotExposeAChecksumBeforeTheBodyIsFullyRead()
    {
        var request = ChunkedRequest(Payload, PayloadCrc32);
        await using var body = S3UploadBodyStream.Create(request);

        var buffer = new byte[4];
        Assert.True(await body.ReadAsync(buffer) > 0);
        Assert.Null(body.ChecksumValue);
    }

    private static HttpRequest PlainRequest(string payload)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        return context.Request;
    }

    private static HttpRequest ChunkedRequest(string payload, string crc32)
    {
        var encoded = new StringBuilder()
            .Append($"{payload.Length:x};chunk-signature={new string('a', 64)}\r\n")
            .Append(payload)
            .Append("\r\n")
            .Append($"0;chunk-signature={new string('b', 64)}\r\n")
            .Append($"x-amz-checksum-crc32:{crc32}\r\n")
            .Append($"x-amz-trailer-signature:{new string('c', 64)}\r\n")
            .Append("\r\n")
            .ToString();

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        context.Request.Headers.ContentEncoding = "aws-chunked";
        context.Request.Headers["x-amz-content-sha256"] = "STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER";
        context.Request.Headers["x-amz-decoded-content-length"] = payload.Length.ToString();
        context.Request.Headers["x-amz-trailer"] = "x-amz-checksum-crc32";
        return context.Request;
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        await using var target = new MemoryStream();
        await stream.CopyToAsync(target);
        return Encoding.UTF8.GetString(target.ToArray());
    }
}
