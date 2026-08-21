using System.Text;
using Means.Core;
using Means.Protocol.S3;
using Microsoft.AspNetCore.Http;

namespace Means.UnitTests;

public sealed class AwsChunkedStreamTests
{
    [Theory]
    [InlineData("STREAMING-AWS4-HMAC-SHA256-PAYLOAD", true)]
    [InlineData("STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER", true)]
    [InlineData("UNSIGNED-PAYLOAD", false)]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", false)]
    public void DetectsStreamingPayloadHash(string payloadHash, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-amz-content-sha256"] = payloadHash;

        Assert.Equal(expected, AwsChunkedStream.IsChunkedUpload(context.Request));
    }

    [Fact]
    public void DetectsAwsChunkedContentEncodingWithDecodedLength()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.ContentEncoding = "aws-chunked";
        context.Request.Headers["x-amz-decoded-content-length"] = "42";

        Assert.True(AwsChunkedStream.IsChunkedUpload(context.Request));
        Assert.Equal(42, AwsChunkedStream.GetDecodedContentLength(context.Request));
    }

    [Fact]
    public async Task DecodesSingleChunkWithTrailer()
    {
        const string payload = "Hello from the official AWS SDK for .NET.";
        var encoded = new StringBuilder()
            .Append("29;chunk-signature=119647e959407fc595af3ca390407f4eda10a71d15009395d6eac1a445ce1ba8\r\n")
            .Append(payload)
            .Append("\r\n")
            .Append("0;chunk-signature=dc06f087243710e4960118c084d5a8ea2c80476e2cc6b1e3216336c77f5ecce3\r\n")
            .Append("x-amz-checksum-crc32:IgkKYA==\r\n")
            .Append("x-amz-trailer-signature:0b46b3c7ae91b3f1614830d2b9eb3f8cb1199998010b0bb13ee9915623341f6d\r\n")
            .Append("\r\n")
            .ToString();

        Assert.Equal(payload, await DecodeAsync(encoded));
    }

    [Fact]
    public async Task DecodesMultipleChunks()
    {
        var first = new string('a', 16);
        var second = new string('b', 5);
        var encoded = $"10;chunk-signature=aaaa\r\n{first}\r\n5;chunk-signature=bbbb\r\n{second}\r\n0;chunk-signature=cccc\r\n\r\n";

        Assert.Equal(first + second, await DecodeAsync(encoded));
    }

    [Fact]
    public async Task DecodesChunksWithoutSignatures()
    {
        var encoded = "4\r\nabcd\r\n0\r\n\r\n";

        Assert.Equal("abcd", await DecodeAsync(encoded));
    }

    [Fact]
    public async Task ReportsDecodedLength()
    {
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("5;chunk-signature=aaaa\r\nhello\r\n0;chunk-signature=bbbb\r\n\r\n"));
        await using var chunked = new AwsChunkedStream(source);
        await using var target = new MemoryStream();
        await chunked.CopyToAsync(target);

        Assert.Equal(5, chunked.DecodedLength);
    }

    [Fact]
    public async Task RejectsMalformedChunkSize()
    {
        await Assert.ThrowsAsync<MeansException>(() => DecodeAsync("not-a-size;chunk-signature=aaaa\r\ndata\r\n0\r\n\r\n"));
    }

    [Fact]
    public async Task CapturesTrailingHeaders()
    {
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(
            "5;chunk-signature=aaaa\r\nhello\r\n0;chunk-signature=bbbb\r\nx-amz-checksum-crc32:IgkKYA==\r\nx-amz-trailer-signature:cccc\r\n\r\n"));
        await using var chunked = new AwsChunkedStream(source);
        await using var target = new MemoryStream();
        await chunked.CopyToAsync(target);

        Assert.Equal("IgkKYA==", chunked.Trailers["x-amz-checksum-crc32"]);
        Assert.Equal("cccc", chunked.Trailers["x-amz-trailer-signature"]);
    }

    [Fact]
    public async Task AcceptsBodyMatchingDeclaredDecodedLength()
    {
        Assert.Equal("hello", await DecodeAsync("5;chunk-signature=aaaa\r\nhello\r\n0;chunk-signature=bbbb\r\n\r\n", expectedDecodedLength: 5));
    }

    [Fact]
    public async Task RejectsBodyTruncatedOnAChunkBoundary()
    {
        // A dropped connection leaves the framing well-formed but the payload short, which is
        // exactly the case that silently produced corrupt objects.
        var error = await Assert.ThrowsAsync<MeansException>(
            () => DecodeAsync("5;chunk-signature=aaaa\r\nhello\r\n", expectedDecodedLength: 11));

        Assert.Equal(MeansErrorCodes.IncompleteBody, error.Code);
    }

    [Fact]
    public async Task RejectsBodyTruncatedInsideAChunkPayload()
    {
        var error = await Assert.ThrowsAsync<MeansException>(
            () => DecodeAsync("b;chunk-signature=aaaa\r\nhello", expectedDecodedLength: 11));

        Assert.Equal(MeansErrorCodes.IncompleteBody, error.Code);
    }

    [Fact]
    public async Task RejectsBodyLongerThanDeclaredDecodedLength()
    {
        var error = await Assert.ThrowsAsync<MeansException>(
            () => DecodeAsync("5;chunk-signature=aaaa\r\nhello\r\n0;chunk-signature=bbbb\r\n\r\n", expectedDecodedLength: 2));

        Assert.Equal(MeansErrorCodes.IncompleteBody, error.Code);
    }

    [Fact]
    public async Task AcceptsAnEmptyBodyDeclaringZeroLength()
    {
        Assert.Equal("", await DecodeAsync("0;chunk-signature=aaaa\r\n\r\n", expectedDecodedLength: 0));
    }

    [Fact]
    public async Task DecodesPayloadLargerThanInternalBuffer()
    {
        var payload = new string('x', 40 * 1024);
        var encoded = $"{payload.Length:x};chunk-signature=aaaa\r\n{payload}\r\n0;chunk-signature=bbbb\r\n\r\n";

        Assert.Equal(payload, await DecodeAsync(encoded));
    }

    private static async Task<string> DecodeAsync(string encoded, long? expectedDecodedLength = null)
    {
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        await using var chunked = new AwsChunkedStream(source, expectedDecodedLength);
        await using var target = new MemoryStream();
        await chunked.CopyToAsync(target);
        return Encoding.UTF8.GetString(target.ToArray());
    }
}
