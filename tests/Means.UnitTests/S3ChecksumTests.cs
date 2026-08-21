using System.Security.Cryptography;
using System.Text;
using Means.Protocol.S3;

namespace Means.UnitTests;

public sealed class S3ChecksumTests
{
    // The published CRC check value is the checksum of the ASCII string "123456789".
    [Theory]
    [InlineData(S3ChecksumAlgorithm.Crc32, "y/Q5Jg==")]
    [InlineData(S3ChecksumAlgorithm.Crc32C, "4waSgw==")]
    [InlineData(S3ChecksumAlgorithm.Crc64Nvme, "rosUhgp5mIg=")]
    public void MatchesPublishedCrcCheckValues(S3ChecksumAlgorithm algorithm, string expected)
    {
        using var calculator = S3Checksums.CreateCalculator(algorithm);
        calculator.Append("123456789"u8);

        Assert.Equal(expected, calculator.ToBase64());
    }

    [Fact]
    public void MatchesChecksumReportedByTheAwsSdkInIssue3()
    {
        // The trailer captured in https://github.com/AIDotNet/Means/issues/3.
        using var calculator = S3Checksums.CreateCalculator(S3ChecksumAlgorithm.Crc32);
        calculator.Append("Hello from the official AWS SDK for .NET."u8);

        Assert.Equal("IgkKYA==", calculator.ToBase64());
    }

    [Fact]
    public void ComputesShaChecksums()
    {
        var payload = Encoding.UTF8.GetBytes("means");

        using var sha1 = S3Checksums.CreateCalculator(S3ChecksumAlgorithm.Sha1);
        sha1.Append(payload);
        Assert.Equal(Convert.ToBase64String(SHA1.HashData(payload)), sha1.ToBase64());

        using var sha256 = S3Checksums.CreateCalculator(S3ChecksumAlgorithm.Sha256);
        sha256.Append(payload);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(payload)), sha256.ToBase64());
    }

    [Fact]
    public void ProducesTheSameChecksumAcrossAppendBoundaries()
    {
        var payload = new byte[10_000];
        Random.Shared.NextBytes(payload);

        using var whole = S3Checksums.CreateCalculator(S3ChecksumAlgorithm.Crc32C);
        whole.Append(payload);

        using var chunked = S3Checksums.CreateCalculator(S3ChecksumAlgorithm.Crc32C);
        for (var offset = 0; offset < payload.Length; offset += 997)
        {
            chunked.Append(payload.AsSpan(offset, Math.Min(997, payload.Length - offset)));
        }

        Assert.Equal(whole.ToBase64(), chunked.ToBase64());
    }

    [Fact]
    public void ReadingTheChecksumTwiceReturnsTheSameValue()
    {
        using var calculator = S3Checksums.CreateCalculator(S3ChecksumAlgorithm.Sha256);
        calculator.Append("means"u8);

        Assert.Equal(calculator.ToBase64(), calculator.ToBase64());
    }

    [Theory]
    [InlineData("CRC32", S3ChecksumAlgorithm.Crc32)]
    [InlineData("crc32c", S3ChecksumAlgorithm.Crc32C)]
    [InlineData("CRC64NVME", S3ChecksumAlgorithm.Crc64Nvme)]
    [InlineData("x-amz-checksum-sha1", S3ChecksumAlgorithm.Sha1)]
    [InlineData("X-Amz-Checksum-SHA256", S3ChecksumAlgorithm.Sha256)]
    public void ParsesAlgorithmAndHeaderNames(string value, S3ChecksumAlgorithm expected)
    {
        Assert.True(S3Checksums.TryParse(value, out var algorithm));
        Assert.Equal(expected, algorithm);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MD5")]
    [InlineData("x-amz-checksum-mode")]
    [InlineData("x-amz-checksum-future")]
    public void RejectsUnknownAlgorithmNames(string value)
    {
        Assert.False(S3Checksums.TryParse(value, out _));
    }

    [Fact]
    public void PicksTheChecksumTrailerOutOfATrailerList()
    {
        Assert.True(S3Checksums.TryParseTrailerList("x-amz-trailer-signature, x-amz-checksum-crc32", out var trailerName, out var algorithm));
        Assert.Equal("x-amz-checksum-crc32", trailerName);
        Assert.Equal(S3ChecksumAlgorithm.Crc32, algorithm);
    }

    [Fact]
    public void UsesLowercaseChecksumHeaderNames()
    {
        Assert.Equal("x-amz-checksum-crc32c", S3Checksums.HeaderName(S3ChecksumAlgorithm.Crc32C));
        Assert.Equal("x-amz-checksum-crc64nvme", S3Checksums.HeaderName(S3ChecksumAlgorithm.Crc64Nvme));
    }
}
