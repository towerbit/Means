namespace Means.Protocol.S3;

/// <summary>
/// Payload checksum algorithms that S3 clients negotiate through <c>x-amz-checksum-*</c> headers,
/// <c>x-amz-sdk-checksum-algorithm</c>, and <c>aws-chunked</c> trailers.
/// </summary>
public enum S3ChecksumAlgorithm
{
    Crc32,
    Crc32C,
    Crc64Nvme,
    Sha1,
    Sha256
}
