using System.Diagnostics.CodeAnalysis;

namespace Means.Protocol.S3;

/// <summary>
/// Maps between S3 checksum algorithm names, their <c>x-amz-checksum-*</c> header names, and
/// incremental calculators.
/// </summary>
public static class S3Checksums
{
    private const string HeaderPrefix = "x-amz-checksum-";

    /// <summary>Header carrying the algorithm a client will send in an <c>aws-chunked</c> trailer.</summary>
    public const string SdkAlgorithmHeader = "x-amz-sdk-checksum-algorithm";

    /// <summary>Header listing the trailer names that follow the final <c>aws-chunked</c> frame.</summary>
    public const string TrailerHeader = "x-amz-trailer";

    /// <summary>Header a client sets to request stored checksums back on read operations.</summary>
    public const string ModeHeader = "x-amz-checksum-mode";

    /// <summary>
    /// Parses an algorithm name such as <c>CRC32</c> or a header name such as
    /// <c>x-amz-checksum-crc32</c>.
    /// </summary>
    public static bool TryParse(string? value, out S3ChecksumAlgorithm algorithm)
    {
        algorithm = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var name = value.Trim();
        if (name.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[HeaderPrefix.Length..];
        }

        switch (name.ToUpperInvariant())
        {
            case "CRC32":
                algorithm = S3ChecksumAlgorithm.Crc32;
                return true;
            case "CRC32C":
                algorithm = S3ChecksumAlgorithm.Crc32C;
                return true;
            case "CRC64NVME":
                algorithm = S3ChecksumAlgorithm.Crc64Nvme;
                return true;
            case "SHA1":
                algorithm = S3ChecksumAlgorithm.Sha1;
                return true;
            case "SHA256":
                algorithm = S3ChecksumAlgorithm.Sha256;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Returns the canonical S3 algorithm name, for example <c>CRC32C</c>.</summary>
    public static string Name(S3ChecksumAlgorithm algorithm)
    {
        return algorithm switch
        {
            S3ChecksumAlgorithm.Crc32 => "CRC32",
            S3ChecksumAlgorithm.Crc32C => "CRC32C",
            S3ChecksumAlgorithm.Crc64Nvme => "CRC64NVME",
            S3ChecksumAlgorithm.Sha1 => "SHA1",
            S3ChecksumAlgorithm.Sha256 => "SHA256",
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
        };
    }

    /// <summary>Returns the response/trailer header name, for example <c>x-amz-checksum-crc32c</c>.</summary>
    public static string HeaderName(S3ChecksumAlgorithm algorithm)
    {
        return HeaderPrefix + Name(algorithm).ToLowerInvariant();
    }

    public static S3ChecksumCalculator CreateCalculator(S3ChecksumAlgorithm algorithm)
    {
        return new S3ChecksumCalculator(algorithm);
    }

    /// <summary>
    /// Finds the first <c>x-amz-checksum-*</c> name in a comma-separated list, as used by
    /// <c>x-amz-trailer</c>.
    /// </summary>
    public static bool TryParseTrailerList(string? value, [NotNullWhen(true)] out string? trailerName, out S3ChecksumAlgorithm algorithm)
    {
        trailerName = null;
        algorithm = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var candidate in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (candidate.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase) && TryParse(candidate, out algorithm))
            {
                trailerName = candidate;
                return true;
            }
        }

        return false;
    }
}
