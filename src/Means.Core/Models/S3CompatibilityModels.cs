namespace Means.Core;

public static class BucketVersioningStatuses
{
    public const string Off = "Off";
    public const string Enabled = "Enabled";
    public const string Suspended = "Suspended";
}

public static class CopyMetadataDirectives
{
    public const string Copy = "COPY";
    public const string Replace = "REPLACE";
}

public static class S3CannedAcls
{
    public const string Private = "private";
    public const string PublicRead = "public-read";
}

/// <summary>
/// Access control view of a bucket or object.
/// Means has no per-user ownership model yet, so ACLs are not stored: the owner is a single
/// deployment identity and public read is derived from the effective bucket policy. That keeps
/// <c>?acl</c> responses consistent with the access the request would actually get.
/// </summary>
public sealed record S3AccessControlPolicy(string OwnerId, string OwnerDisplayName, bool PublicRead)
{
    public const string DeploymentOwner = "means";

    public static S3AccessControlPolicy ForDeploymentOwner(bool publicRead)
    {
        return new S3AccessControlPolicy(DeploymentOwner, DeploymentOwner, publicRead);
    }
}

public sealed record BucketVersioningInfo(string BucketName, string Status);

public sealed record DeleteObjectResult(
    string BucketName,
    string Key,
    string? VersionId,
    bool DeleteMarker);

public sealed record BatchDeleteObjectIdentifier(string Key, string? VersionId = null);

public sealed record BatchDeleteRequest(
    string BucketName,
    IReadOnlyList<BatchDeleteObjectIdentifier> Objects);

public sealed record BatchDeleteError(
    string Key,
    string? VersionId,
    string Code,
    string Message);

public sealed record BatchDeleteResult(
    string BucketName,
    IReadOnlyList<DeleteObjectResult> Deleted,
    IReadOnlyList<BatchDeleteError> Errors);

public sealed record ObjectTagSet(IReadOnlyDictionary<string, string> Tags);

public sealed record BucketLifecycleConfiguration(
    IReadOnlyList<LifecycleRule> Rules);

public sealed record LifecycleRule(
    string Id,
    string Status,
    string Prefix,
    int? ExpirationDays,
    int? NoncurrentVersionExpirationDays,
    int? AbortIncompleteMultipartUploadDays);

public sealed record BucketCorsConfiguration(string Xml);

public sealed record BucketNotificationConfiguration(string Xml);
