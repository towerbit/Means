using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Means.Protocol.S3;

/// <summary>
/// Computes a single S3 payload checksum incrementally so uploads can be validated while they
/// stream to storage.
/// </summary>
/// <remarks>
/// The CRC variants are generated from their reflected polynomials at first use rather than
/// hardcoded as tables, which keeps the values verifiable against the published CRC check vectors.
/// </remarks>
public sealed class S3ChecksumCalculator : IDisposable
{
    // Reflected polynomials: CRC-32/ISO-HDLC, CRC-32/ISCSI (Castagnoli), and CRC-64/NVME.
    private const ulong Crc32Polynomial = 0xEDB88320UL;
    private const ulong Crc32CPolynomial = 0x82F63B78UL;
    private const ulong Crc64NvmePolynomial = 0x9A6C9329AC4BC9B5UL;

    private static readonly Lazy<ulong[]> Crc32Table = new(() => BuildTable(Crc32Polynomial, uint.MaxValue));
    private static readonly Lazy<ulong[]> Crc32CTable = new(() => BuildTable(Crc32CPolynomial, uint.MaxValue));
    private static readonly Lazy<ulong[]> Crc64NvmeTable = new(() => BuildTable(Crc64NvmePolynomial, ulong.MaxValue));

    private readonly IncrementalHash? _hash;
    private readonly ulong[]? _table;
    private readonly ulong _mask;
    private readonly int _width;
    private ulong _crc;

    internal S3ChecksumCalculator(S3ChecksumAlgorithm algorithm)
    {
        Algorithm = algorithm;
        switch (algorithm)
        {
            case S3ChecksumAlgorithm.Crc32:
                (_table, _mask, _width) = (Crc32Table.Value, uint.MaxValue, 4);
                break;
            case S3ChecksumAlgorithm.Crc32C:
                (_table, _mask, _width) = (Crc32CTable.Value, uint.MaxValue, 4);
                break;
            case S3ChecksumAlgorithm.Crc64Nvme:
                (_table, _mask, _width) = (Crc64NvmeTable.Value, ulong.MaxValue, 8);
                break;
            case S3ChecksumAlgorithm.Sha1:
                _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                break;
            case S3ChecksumAlgorithm.Sha256:
                _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(algorithm));
        }

        _crc = _mask;
    }

    public S3ChecksumAlgorithm Algorithm { get; }

    /// <summary>Response header name for this checksum, for example <c>x-amz-checksum-crc32</c>.</summary>
    public string HeaderName => S3Checksums.HeaderName(Algorithm);

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_hash is not null)
        {
            _hash.AppendData(data);
            return;
        }

        var table = _table!;
        var crc = _crc;
        foreach (var value in data)
        {
            crc = table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        _crc = crc;
    }

    /// <summary>Returns the base64 checksum S3 clients compare against.</summary>
    public string ToBase64()
    {
        if (_hash is not null)
        {
            return Convert.ToBase64String(_hash.GetCurrentHash());
        }

        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, (_crc ^ _mask) & _mask);
        return Convert.ToBase64String(buffer[(8 - _width)..]);
    }

    public void Dispose()
    {
        _hash?.Dispose();
    }

    private static ulong[] BuildTable(ulong polynomial, ulong mask)
    {
        var table = new ulong[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (ulong)index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ polynomial : value >> 1;
            }

            table[index] = value & mask;
        }

        return table;
    }
}
