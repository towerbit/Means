using System.Globalization;
using System.Text;
using Means.Core;
using Means.Protocol.S3;

namespace Means.Endpoints.S3;

/// <summary>
/// Small parsing helpers for S3-specific HTTP values.
/// Keeping these helpers in one place keeps endpoint handlers focused on operation flow.
/// </summary>
internal static class S3RequestParser
{
    /// <summary>
    /// Returns the object payload stream for an upload request.
    /// AWS SDKs default to SigV4 streaming uploads, which frame the payload with
    /// <c>aws-chunked</c> chunk headers and trailers; those frames must be stripped before storage,
    /// and the declared length and checksums must be verified against what actually arrives.
    /// </summary>
    public static S3UploadBodyStream OpenUploadBody(HttpContext context)
    {
        return S3UploadBodyStream.Create(context.Request);
    }

    public static IReadOnlyDictionary<string, string> ExtractMetadata(HttpContext context)
    {
        return context.Request.Headers
            .Where(header => header.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                header => header.Key["x-amz-meta-".Length..].ToLowerInvariant(),
                header => header.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);
    }

    public static (string Bucket, string Key, string? VersionId) ParseCopySource(string value)
    {
        var source = Uri.UnescapeDataString(value.Trim().TrimStart('/'));
        var queryIndex = source.IndexOf('?', StringComparison.Ordinal);
        var path = queryIndex >= 0 ? source[..queryIndex] : source;
        var query = queryIndex >= 0 ? source[(queryIndex + 1)..] : "";
        var slash = path.IndexOf('/');
        if (slash <= 0 || slash == path.Length - 1)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Invalid x-amz-copy-source header.", 400);
        }

        var versionId = ParseQueryValue(query, "versionId");
        return (path[..slash], path[(slash + 1)..], versionId);
    }

    public static string ParseMetadataDirective(HttpContext context)
    {
        var directive = context.Request.Headers["x-amz-metadata-directive"].ToString();
        if (string.IsNullOrWhiteSpace(directive))
        {
            return CopyMetadataDirectives.Copy;
        }

        if (string.Equals(directive, CopyMetadataDirectives.Copy, StringComparison.OrdinalIgnoreCase))
        {
            return CopyMetadataDirectives.Copy;
        }

        if (string.Equals(directive, CopyMetadataDirectives.Replace, StringComparison.OrdinalIgnoreCase))
        {
            return CopyMetadataDirectives.Replace;
        }

        throw new MeansException(MeansErrorCodes.InvalidArgument, "Invalid x-amz-metadata-directive header.", 400);
    }

    public static (long Start, long End)? ParseCopySourceRange(string? value, long length)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ParseRange(value, length)
            ?? throw new MeansException(MeansErrorCodes.InvalidRange, "Invalid copy source range.", 400);
    }

    public static (long Start, long End)? ParseRange(string value, long length)
    {
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = value["bytes=".Length..].Split('-', 2);
        if (parts.Length != 2)
        {
            return null;
        }

        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                return null;
            }

            return (Math.Max(0, length - suffix), length - 1);
        }

        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start))
        {
            return null;
        }

        var end = parts[1].Length == 0
            ? length - 1
            : long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedEnd)
                ? parsedEnd
                : -1;
        return start < 0 || end < start || start >= length ? null : (start, Math.Min(end, length - 1));
    }

    public static string? GetHeader(HttpContext context, string headerName)
    {
        var value = context.Request.Headers[headerName].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static int ParseMaxKeys(string? value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 1, 1000)
            : 1000;
    }

    /// <summary>
    /// Validates the <c>encoding-type</c> listing parameter. Only <c>url</c> exists in S3.
    /// </summary>
    public static string? ParseEncodingType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Equals(value, "url", StringComparison.OrdinalIgnoreCase)
            ? "url"
            : throw new MeansException(MeansErrorCodes.InvalidArgument, "Invalid encoding-type. Only 'url' is supported.", 400);
    }

    /// <summary>
    /// Rejects canned ACLs that would grant access Means cannot represent.
    /// Means has no ACL store, so grants beyond the bucket owner must fail loudly instead of being
    /// silently dropped; anonymous access is configured through bucket policy instead.
    /// </summary>
    public static void EnsureSupportedCannedAcl(HttpContext context)
    {
        var requested = GetHeader(context, "x-amz-acl");
        var hasExplicitGrant = context.Request.Headers.Keys.Any(name =>
            name.StartsWith("x-amz-grant-", StringComparison.OrdinalIgnoreCase));
        if (hasExplicitGrant || (requested is not null && !IsOwnerOnlyCannedAcl(NormalizeCannedAcl(requested))))
        {
            throw new MeansException(
                MeansErrorCodes.NotImplemented,
                "Only owner-only canned ACLs are supported. Use a bucket policy to grant anonymous access.",
                501);
        }
    }

    /// <summary>
    /// Reads the canned ACL of a PUT ?acl request from either the <c>x-amz-acl</c> header or an
    /// <c>AccessControlPolicy</c> body, and reports whether it only grants the owner.
    /// </summary>
    public static async Task<bool> ParseAclRequestIsOwnerOnlyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var requested = GetHeader(context, "x-amz-acl");
        if (requested is not null)
        {
            return IsOwnerOnlyCannedAcl(NormalizeCannedAcl(requested));
        }

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var xml = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return true;
        }

        var root = ReadRootContent(xml, "AccessControlPolicy", "Malformed AccessControlPolicy XML.");
        return !Elements(root, "URI").Any(uri =>
            DecodeXml(uri).Contains("AllUsers", StringComparison.OrdinalIgnoreCase)
            || DecodeXml(uri).Contains("AuthenticatedUsers", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<(IReadOnlyList<BatchDeleteObjectIdentifier> Objects, bool Quiet)> ParseDeleteObjectsAsync(
        Stream body,
        CancellationToken cancellationToken)
    {
        var root = ReadRootContent(await ReadXmlBodyAsync(body, cancellationToken), "Delete", "Malformed Delete XML.");
        var objects = new List<BatchDeleteObjectIdentifier>();
        foreach (var element in Elements(root, "Object"))
        {
            var key = FirstElementValue(element, "Key");
            if (string.IsNullOrEmpty(key))
            {
                throw new MeansException(MeansErrorCodes.MalformedXML, "Delete entries require a Key.", 400);
            }

            objects.Add(new BatchDeleteObjectIdentifier(key, FirstElementValue(element, "VersionId")));
        }

        if (objects.Count == 0)
        {
            throw new MeansException(MeansErrorCodes.MalformedXML, "Delete requires at least one Object entry.", 400);
        }

        if (objects.Count > 1000)
        {
            throw new MeansException(MeansErrorCodes.MalformedXML, "Delete accepts at most 1000 Object entries.", 400);
        }

        var quiet = string.Equals(FirstElementValue(root, "Quiet"), "true", StringComparison.OrdinalIgnoreCase);
        return (objects, quiet);
    }

    private static bool IsOwnerOnlyCannedAcl(string cannedAcl)
    {
        // bucket-owner-* grants collapse to "owner only" in a single-tenant deployment.
        return cannedAcl is S3CannedAcls.Private or "bucket-owner-read" or "bucket-owner-full-control";
    }

    private static string NormalizeCannedAcl(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is S3CannedAcls.Private
            or S3CannedAcls.PublicRead
            or "public-read-write"
            or "authenticated-read"
            or "aws-exec-read"
            or "bucket-owner-read"
            or "bucket-owner-full-control"
            or "log-delivery-write"
            ? normalized
            : throw new MeansException(MeansErrorCodes.InvalidArgument, "Invalid x-amz-acl header.", 400);
    }

    public static int ParseMaxUploads(string? value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1000;
    }

    public static int ParseMaxParts(string? value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1000;
    }

    public static int ParsePartNumber(string? value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var partNumber)
            || partNumber is < 1 or > 10000)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Part number must be between 1 and 10000.", 400);
        }

        return partNumber;
    }

    public static string ParseUploadId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Missing uploadId.", 400);
        }

        return value;
    }

    public static async Task<string> ParseBucketVersioningStatusAsync(Stream body, CancellationToken cancellationToken)
    {
        if (body == Stream.Null || body.CanSeek && body.Length == 0)
        {
            return BucketVersioningStatuses.Off;
        }

        var root = ReadRootContent(await ReadXmlBodyAsync(body, cancellationToken), "VersioningConfiguration", "Malformed VersioningConfiguration XML.");
        return FirstElementValue(root, "Status") ?? BucketVersioningStatuses.Off;
    }

    public static async Task<ObjectTagSet> ParseTaggingAsync(Stream body, CancellationToken cancellationToken)
    {
        var root = ReadRootContent(await ReadXmlBodyAsync(body, cancellationToken), "Tagging", "Malformed Tagging XML.");
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in Elements(root, "Tag"))
        {
            var key = FirstElementValue(tag, "Key");
            var value = FirstElementValue(tag, "Value") ?? "";
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new MeansException(MeansErrorCodes.InvalidTag, "Tag key is required.", 400);
            }

            tags[key] = value;
        }

        return new ObjectTagSet(tags);
    }

    public static async Task<BucketLifecycleConfiguration> ParseLifecycleAsync(Stream body, CancellationToken cancellationToken)
    {
        var root = ReadRootContent(await ReadXmlBodyAsync(body, cancellationToken), "LifecycleConfiguration", "Malformed LifecycleConfiguration XML.");
        var rules = new List<LifecycleRule>();
        foreach (var rule in Elements(root, "Rule"))
        {
            var id = FirstElementValue(rule, "ID") ?? Guid.NewGuid().ToString("N");
            var status = FirstElementValue(rule, "Status") ?? "Disabled";
            var prefix = FirstElementValue(rule, "Prefix") ?? "";
            var expirationDays = ParseOptionalPositiveInt(FirstElementValue(FirstElementContent(rule, "Expiration"), "Days"));
            var noncurrentDays = ParseOptionalPositiveInt(FirstElementValue(FirstElementContent(rule, "NoncurrentVersionExpiration"), "NoncurrentDays"));
            var abortDays = ParseOptionalPositiveInt(FirstElementValue(FirstElementContent(rule, "AbortIncompleteMultipartUpload"), "DaysAfterInitiation"));
            rules.Add(new LifecycleRule(id, status, prefix, expirationDays, noncurrentDays, abortDays));
        }

        return new BucketLifecycleConfiguration(rules);
    }

    public static async Task<string> ReadAndValidateXmlAsync(Stream body, string rootName, CancellationToken cancellationToken)
    {
        var xml = await ReadXmlBodyAsync(body, cancellationToken);
        _ = ReadRootContent(xml, rootName, $"Malformed {rootName} XML.");
        return xml;
    }

    public static async Task<IReadOnlyList<CompletedMultipartPart>> ParseCompleteMultipartUploadAsync(Stream body, CancellationToken cancellationToken)
    {
        try
        {
            var root = ReadRootContent(await ReadXmlBodyAsync(body, cancellationToken), "CompleteMultipartUpload", "Malformed CompleteMultipartUpload XML.");
            var parts = new List<CompletedMultipartPart>();
            foreach (var part in Elements(root, "Part"))
            {
                var partNumberText = FirstElementValue(part, "PartNumber");
                var etag = FirstElementValue(part, "ETag");
                if (!int.TryParse(partNumberText, NumberStyles.None, CultureInfo.InvariantCulture, out var partNumber)
                    || string.IsNullOrWhiteSpace(etag))
                {
                    throw MalformedCompleteMultipartUpload();
                }

                parts.Add(new CompletedMultipartPart(partNumber, etag));
            }

            return parts;
        }
        catch (MeansException)
        {
            throw;
        }
        catch
        {
            throw MalformedCompleteMultipartUpload();
        }
    }

    public static void ValidateBucketName(string bucketName) => S3NameValidator.ValidateBucketName(bucketName);

    public static void ValidateObjectKey(string objectKey) => S3NameValidator.ValidateObjectKey(objectKey);

    private static async Task<string> ReadXmlBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var xml = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(xml))
        {
            throw new MeansException(MeansErrorCodes.MalformedXML, "Malformed XML document.", 400);
        }

        return xml;
    }

    private static string ReadRootContent(string xml, string rootName, string message)
    {
        return TryReadNextElement(xml, 0, out var localName, out var content, out _)
            && string.Equals(localName, rootName, StringComparison.Ordinal)
            ? content
            : throw new MeansException(MeansErrorCodes.MalformedXML, message, 400);
    }

    private static string? FirstElementContent(string xml, string localName)
    {
        return Elements(xml, localName).FirstOrDefault();
    }

    private static string? FirstElementValue(string? xml, string localName)
    {
        if (string.IsNullOrEmpty(xml))
        {
            return null;
        }

        var content = Elements(xml, localName).FirstOrDefault();
        return content is null ? null : DecodeXml(content);
    }

    private static IEnumerable<string> Elements(string xml, string localName)
    {
        var position = 0;
        while (TryReadNextElement(xml, position, out var name, out var content, out var next))
        {
            if (string.Equals(name, localName, StringComparison.Ordinal))
            {
                yield return content;
            }

            foreach (var child in Elements(content, localName))
            {
                yield return child;
            }

            position = next;
        }
    }

    private static bool TryReadNextElement(
        string xml,
        int start,
        out string localName,
        out string content,
        out int next)
    {
        localName = "";
        content = "";
        next = start;
        var position = start;
        while (position < xml.Length)
        {
            var openStart = xml.IndexOf('<', position);
            if (openStart < 0 || openStart + 1 >= xml.Length)
            {
                return false;
            }

            var marker = xml[openStart + 1];
            if (marker == '/')
            {
                position = openStart + 2;
                continue;
            }

            if (marker == '?')
            {
                position = SkipUntil(xml, openStart + 2, "?>");
                continue;
            }

            if (marker == '!')
            {
                position = xml.AsSpan(openStart).StartsWith("<!--", StringComparison.Ordinal)
                    ? SkipUntil(xml, openStart + 4, "-->")
                    : SkipUntil(xml, openStart + 2, ">");
                continue;
            }

            var nameStart = openStart + 1;
            var nameEnd = nameStart;
            while (nameEnd < xml.Length && !char.IsWhiteSpace(xml[nameEnd]) && xml[nameEnd] != '/' && xml[nameEnd] != '>')
            {
                nameEnd++;
            }

            if (nameEnd == nameStart)
            {
                return false;
            }

            localName = LocalName(xml[nameStart..nameEnd]);
            var openEnd = xml.IndexOf('>', nameEnd);
            if (openEnd < 0)
            {
                return false;
            }

            if (IsSelfClosing(xml, openEnd))
            {
                next = openEnd + 1;
                return true;
            }

            var closeStart = FindCloseTag(xml, localName, openEnd + 1);
            if (closeStart < 0)
            {
                return false;
            }

            var closeEnd = xml.IndexOf('>', closeStart);
            if (closeEnd < 0)
            {
                return false;
            }

            content = xml[(openEnd + 1)..closeStart];
            next = closeEnd + 1;
            return true;
        }

        return false;
    }

    private static int FindCloseTag(string xml, string localName, int start)
    {
        var position = start;
        while (position < xml.Length)
        {
            var closeStart = xml.IndexOf("</", position, StringComparison.Ordinal);
            if (closeStart < 0)
            {
                return -1;
            }

            var nameStart = closeStart + 2;
            var nameEnd = nameStart;
            while (nameEnd < xml.Length && !char.IsWhiteSpace(xml[nameEnd]) && xml[nameEnd] != '>')
            {
                nameEnd++;
            }

            if (string.Equals(LocalName(xml[nameStart..nameEnd]), localName, StringComparison.Ordinal))
            {
                return closeStart;
            }

            position = nameEnd;
        }

        return -1;
    }

    private static int SkipUntil(string xml, int start, string marker)
    {
        var end = xml.IndexOf(marker, start, StringComparison.Ordinal);
        return end < 0 ? xml.Length : end + marker.Length;
    }

    private static bool IsSelfClosing(string xml, int openEnd)
    {
        var index = openEnd - 1;
        while (index >= 0 && char.IsWhiteSpace(xml[index]))
        {
            index--;
        }

        return index >= 0 && xml[index] == '/';
    }

    private static string LocalName(string name)
    {
        var separator = name.IndexOf(':', StringComparison.Ordinal);
        return separator < 0 ? name : name[(separator + 1)..];
    }

    private static string DecodeXml(string value)
    {
        return value
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);
    }

    private static int? ParseOptionalPositiveInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new MeansException(MeansErrorCodes.MalformedXML, "Lifecycle numeric values must be positive integers.", 400);
    }

    private static string? ParseQueryValue(string query, string name)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(pair[0], name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        return null;
    }

    private static MeansException MalformedCompleteMultipartUpload()
    {
        return new MeansException(MeansErrorCodes.MalformedXML, "Malformed CompleteMultipartUpload XML.", 400);
    }
}
