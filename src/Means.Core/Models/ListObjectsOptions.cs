namespace Means.Core;

/// <summary>
/// Options shared by S3 ListObjects (v1) and ListObjectsV2 after query-string parsing.
/// <see cref="ContinuationToken"/> carries the opaque V2 token; <see cref="StartAfter"/> carries a
/// plain object key, which is how both the V1 <c>marker</c> and the V2 <c>start-after</c> arrive.
/// A continuation token wins when both are present, matching AWS.
/// </summary>
public sealed record ListObjectsOptions(
    string? Prefix,
    string? Delimiter,
    string? ContinuationToken,
    int MaxKeys,
    string? StartAfter = null);
