using Microsoft.AspNetCore.Http;

namespace Means.Protocol.S3;

/// <summary>
/// Resolves S3 bucket addressing before signature verification.
/// The resolver intentionally performs only syntactic address extraction; bucket
/// existence, policy, and permissions are handled by later layers.
/// </summary>
public static class S3AddressResolver
{
    public static S3Address Resolve(HttpRequest request, S3AddressingOptions options)
    {
        var host = request.Host.Host.ToLowerInvariant();
        var path = request.Path.Value ?? "/";
        if (!string.IsNullOrWhiteSpace(options.AliasPrefix)
            && path.StartsWith(options.AliasPrefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            path = path[options.AliasPrefix.TrimEnd('/').Length..];
            if (string.IsNullOrEmpty(path))
            {
                path = "/";
            }
        }

        // AWS SDKs commonly issue bucket operations as "/{bucket}/" (trailing slash) and may
        // produce an extra slash after a path-style ServiceURL that already ends with '/'.
        // Collapse only leading duplicate slashes so "/s3//bucket/" still resolves as a bucket.
        var trimmedPath = path.TrimStart('/');

        if (IsVirtualHostedHost(host, options, out var bucketFromHost))
        {
            return new S3Address(bucketFromHost, NormalizeObjectKey(trimmedPath), true);
        }

        if (string.IsNullOrEmpty(trimmedPath))
        {
            return new S3Address(null, null, false);
        }

        var slash = trimmedPath.IndexOf('/');
        if (slash < 0)
        {
            return new S3Address(Uri.UnescapeDataString(trimmedPath), null, false);
        }

        var bucket = Uri.UnescapeDataString(trimmedPath[..slash]);
        var key = NormalizeObjectKey(trimmedPath[(slash + 1)..]);
        return new S3Address(bucket, key, false);
    }

    /// <summary>
    /// Treats a missing or empty path segment as "no object key".
    /// A trailing slash after the bucket name is a bucket-level request in S3 clients, not an empty key.
    /// </summary>
    private static string? NormalizeObjectKey(string? pathSegment)
    {
        if (string.IsNullOrEmpty(pathSegment))
        {
            return null;
        }

        var key = Uri.UnescapeDataString(pathSegment);
        return string.IsNullOrEmpty(key) ? null : key;
    }

    private static bool IsVirtualHostedHost(string host, S3AddressingOptions options, out string bucket)
    {
        bucket = "";
        var serviceHost = options.ServiceHost.ToLowerInvariant();
        var suffix = options.DomainSuffix.ToLowerInvariant();

        if (host == serviceHost || host is "localhost" or "127.0.0.1" or "::1")
        {
            return false;
        }

        var suffixWithDot = "." + suffix;
        if (!host.EndsWith(suffixWithDot, StringComparison.Ordinal))
        {
            return false;
        }

        bucket = host[..^suffixWithDot.Length];
        return !string.IsNullOrWhiteSpace(bucket) && bucket != "api";
    }
}
