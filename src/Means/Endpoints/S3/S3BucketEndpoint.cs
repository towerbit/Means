using Means.Core;
using Means.Protocol.S3;

namespace Means.Endpoints.S3;

/// <summary>
/// Handles bucket-scoped S3 operations such as create, head, delete, ACL, location, and listings.
/// Object-key operations are handled separately so bucket behavior remains easy to scan.
/// </summary>
internal static class S3BucketEndpoint
{
    /// <summary>
    /// Bucket subresources defined by S3 that Means does not implement. They are rejected explicitly
    /// so a request such as <c>GET /{bucket}?logging</c> cannot fall through to an object listing.
    /// </summary>
    private static readonly string[] UnsupportedSubresources =
    [
        "accelerate",
        "analytics",
        "encryption",
        "intelligent-tiering",
        "inventory",
        "logging",
        "metrics",
        "object-lock",
        "ownershipControls",
        "publicAccessBlock",
        "replication",
        "requestPayment",
        "tagging",
        "website"
    ];

    public static async Task HandleAsync(
        HttpContext context,
        string bucketName,
        string region,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        var method = context.Request.Method;
        if (context.Request.Query.ContainsKey("versioning"))
        {
            await HandleVersioningAsync(context, bucketName, store, authorizer, cancellationToken);
            return;
        }

        if (context.Request.Query.ContainsKey("lifecycle"))
        {
            await HandleLifecycleAsync(context, bucketName, store, authorizer, cancellationToken);
            return;
        }

        if (context.Request.Query.ContainsKey("cors"))
        {
            await HandleCorsAsync(context, bucketName, store, authorizer, cancellationToken);
            return;
        }

        if (context.Request.Query.ContainsKey("notification"))
        {
            await HandleNotificationAsync(context, bucketName, store, authorizer, cancellationToken);
            return;
        }

        if (context.Request.Query.ContainsKey("location"))
        {
            await HandleLocationAsync(context, bucketName, region, store, authorizer, cancellationToken);
            return;
        }

        if (context.Request.Query.ContainsKey("acl"))
        {
            await HandleAclAsync(context, bucketName, store, authorizer, cancellationToken);
            return;
        }

        if (context.Request.Query.ContainsKey("delete"))
        {
            await HandleDeleteObjectsAsync(context, bucketName, store, authorizer, cancellationToken);
            return;
        }

        var unsupported = UnsupportedSubresources.FirstOrDefault(context.Request.Query.ContainsKey);
        if (unsupported is not null)
        {
            // Authenticate first so anonymous callers cannot probe which subresources exist.
            await authorizer.RequireAuthenticatedAsync(context, cancellationToken);
            throw new MeansException(
                MeansErrorCodes.NotImplemented,
                $"The bucket subresource '{unsupported}' is not implemented.",
                501);
        }

        if (HttpMethods.IsPut(method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.CreateBucket, bucketName, null, requireAuthenticated: true, cancellationToken);
            S3RequestParser.EnsureSupportedCannedAcl(context);
            await store.CreateBucketAsync(bucketName, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.Headers.Location = "/" + bucketName;
            context.Response.Headers["x-amz-bucket-region"] = region;
            return;
        }

        if (HttpMethods.IsHead(method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.ListBucket, bucketName, null, requireAuthenticated: false, cancellationToken);
            await EnsureBucketExistsAsync(store, bucketName, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status200OK;
            // Clients such as s3fs and the AWS SDKs read the bucket region from HeadBucket to pick a signing region.
            context.Response.Headers["x-amz-bucket-region"] = region;
            return;
        }

        if (HttpMethods.IsDelete(method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.DeleteBucket, bucketName, null, requireAuthenticated: true, cancellationToken);
            await store.DeleteBucketAsync(bucketName, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (HttpMethods.IsGet(method) && context.Request.Query.ContainsKey("uploads"))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.ListBucket, bucketName, null, requireAuthenticated: false, cancellationToken);
            var options = new ListMultipartUploadsOptions(
                context.Request.Query["prefix"].FirstOrDefault(),
                context.Request.Query["delimiter"].FirstOrDefault(),
                context.Request.Query["key-marker"].FirstOrDefault(),
                context.Request.Query["upload-id-marker"].FirstOrDefault(),
                S3RequestParser.ParseMaxUploads(context.Request.Query["max-uploads"].FirstOrDefault()));
            var result = await store.ListMultipartUploadsAsync(bucketName, options, cancellationToken);
            await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.ListMultipartUploads(result), cancellationToken);
            return;
        }

        if (HttpMethods.IsGet(method) && context.Request.Query.ContainsKey("versions"))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.ListBucketVersions, bucketName, null, requireAuthenticated: false, cancellationToken);
            var options = new ListObjectVersionsOptions(
                context.Request.Query["prefix"].FirstOrDefault(),
                context.Request.Query["delimiter"].FirstOrDefault(),
                context.Request.Query["key-marker"].FirstOrDefault(),
                context.Request.Query["version-id-marker"].FirstOrDefault(),
                S3RequestParser.ParseMaxKeys(context.Request.Query["max-keys"].FirstOrDefault()));
            var result = await store.ListObjectVersionsAsync(bucketName, options, cancellationToken);
            await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.ListObjectVersions(result), cancellationToken);
            return;
        }

        if (HttpMethods.IsGet(method))
        {
            await HandleListObjectsAsync(context, bucketName, store, authorizer, cancellationToken);
            return;
        }

        throw new MeansException(MeansErrorCodes.InvalidRequest, "Unsupported bucket operation.", 400);
    }

    /// <summary>
    /// Serves both listing generations: <c>list-type=2</c> selects ListObjectsV2, and its absence
    /// selects the original ListObjects, which is what s3fs and older SDKs send by default.
    /// </summary>
    private static async Task HandleListObjectsAsync(
        HttpContext context,
        string bucketName,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        await authorizer.AuthorizeAsync(context, S3Actions.ListBucket, bucketName, null, requireAuthenticated: false, cancellationToken);
        var prefix = context.Request.Query["prefix"].FirstOrDefault();
        var delimiter = context.Request.Query["delimiter"].FirstOrDefault();
        var maxKeys = S3RequestParser.ParseMaxKeys(context.Request.Query["max-keys"].FirstOrDefault());
        var encodingType = S3RequestParser.ParseEncodingType(context.Request.Query["encoding-type"].FirstOrDefault());

        if (context.Request.Query["list-type"] == "2")
        {
            var continuationToken = context.Request.Query["continuation-token"].FirstOrDefault();
            var startAfter = context.Request.Query["start-after"].FirstOrDefault();
            var result = await store.ListObjectsAsync(
                bucketName,
                new ListObjectsOptions(prefix, delimiter, continuationToken, maxKeys, startAfter),
                cancellationToken);
            await S3ResponseWriter.WriteXmlAsync(
                context,
                StatusCodes.Status200OK,
                S3Xml.ListObjectsV2(result, continuationToken, startAfter, encodingType),
                cancellationToken);
            return;
        }

        var marker = context.Request.Query["marker"].FirstOrDefault();
        var listing = await store.ListObjectsAsync(
            bucketName,
            new ListObjectsOptions(prefix, delimiter, null, maxKeys, marker),
            cancellationToken);
        await S3ResponseWriter.WriteXmlAsync(
            context,
            StatusCodes.Status200OK,
            S3Xml.ListObjects(listing, marker, encodingType),
            cancellationToken);
    }

    private static async Task HandleLocationAsync(
        HttpContext context,
        string bucketName,
        string region,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            throw new MeansException(MeansErrorCodes.InvalidRequest, "Unsupported location operation.", 400);
        }

        await authorizer.AuthorizeAsync(context, S3Actions.GetBucketLocation, bucketName, null, requireAuthenticated: false, cancellationToken);
        await EnsureBucketExistsAsync(store, bucketName, cancellationToken);
        context.Response.Headers["x-amz-bucket-region"] = region;
        await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.BucketLocation(region), cancellationToken);
    }

    private static async Task HandleAclAsync(
        HttpContext context,
        string bucketName,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.GetBucketAcl, bucketName, null, requireAuthenticated: true, cancellationToken);
            await EnsureBucketExistsAsync(store, bucketName, cancellationToken);
            var publicRead = await authorizer.IsAnonymousAllowedAsync(S3Actions.ListBucket, bucketName, null, cancellationToken);
            await S3ResponseWriter.WriteXmlAsync(
                context,
                StatusCodes.Status200OK,
                S3Xml.AccessControlPolicy(S3AccessControlPolicy.ForDeploymentOwner(publicRead)),
                cancellationToken);
            return;
        }

        if (HttpMethods.IsPut(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.PutBucketAcl, bucketName, null, requireAuthenticated: true, cancellationToken);
            await EnsureBucketExistsAsync(store, bucketName, cancellationToken);
            await EnsureOwnerOnlyAclAsync(context, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        throw new MeansException(MeansErrorCodes.InvalidRequest, "Unsupported ACL operation.", 400);
    }

    /// <summary>
    /// Deletes up to 1000 keys in one request. Authorization is evaluated per key so a denied key is
    /// reported as an entry-level error, matching how S3 reports partial failures.
    /// </summary>
    private static async Task HandleDeleteObjectsAsync(
        HttpContext context,
        string bucketName,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            throw new MeansException(MeansErrorCodes.InvalidRequest, "DeleteObjects requires POST.", 400);
        }

        var (requested, quiet) = await S3RequestParser.ParseDeleteObjectsAsync(context.Request.Body, cancellationToken);
        var authorized = new List<BatchDeleteObjectIdentifier>(requested.Count);
        var rejected = new List<BatchDeleteError>();
        foreach (var identifier in requested)
        {
            try
            {
                S3RequestParser.ValidateObjectKey(identifier.Key);
                await authorizer.AuthorizeAsync(context, S3Actions.DeleteObject, bucketName, identifier.Key, requireAuthenticated: false, cancellationToken);
                authorized.Add(identifier);
            }
            catch (MeansException ex)
            {
                rejected.Add(new BatchDeleteError(identifier.Key, identifier.VersionId, ex.Code, ex.Message));
            }
        }

        var result = await store.DeleteObjectsAsync(bucketName, authorized, cancellationToken);
        if (rejected.Count > 0)
        {
            result = result with { Errors = [.. result.Errors, .. rejected] };
        }

        await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.DeleteResult(result, quiet), cancellationToken);
    }

    private static async Task HandleVersioningAsync(
        HttpContext context,
        string bucketName,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.GetBucketVersioning, bucketName, null, requireAuthenticated: false, cancellationToken);
            var versioning = await store.GetBucketVersioningAsync(bucketName, cancellationToken);
            await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.BucketVersioning(versioning), cancellationToken);
            return;
        }

        if (HttpMethods.IsPut(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.PutBucketVersioning, bucketName, null, requireAuthenticated: true, cancellationToken);
            var status = await S3RequestParser.ParseBucketVersioningStatusAsync(context.Request.Body, cancellationToken);
            await store.PutBucketVersioningAsync(bucketName, status, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        throw new MeansException(MeansErrorCodes.InvalidRequest, "Unsupported versioning operation.", 400);
    }

    private static async Task HandleLifecycleAsync(
        HttpContext context,
        string bucketName,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.GetLifecycleConfiguration, bucketName, null, requireAuthenticated: false, cancellationToken);
            var lifecycle = await store.GetBucketLifecycleAsync(bucketName, cancellationToken)
                ?? throw new MeansException(MeansErrorCodes.NoSuchLifecycleConfiguration, "Lifecycle configuration does not exist.", 404);
            await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.BucketLifecycle(lifecycle), cancellationToken);
            return;
        }

        if (HttpMethods.IsPut(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.PutLifecycleConfiguration, bucketName, null, requireAuthenticated: true, cancellationToken);
            var lifecycle = await S3RequestParser.ParseLifecycleAsync(context.Request.Body, cancellationToken);
            await store.PutBucketLifecycleAsync(bucketName, lifecycle, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        if (HttpMethods.IsDelete(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.PutLifecycleConfiguration, bucketName, null, requireAuthenticated: true, cancellationToken);
            await store.DeleteBucketLifecycleAsync(bucketName, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        throw new MeansException(MeansErrorCodes.InvalidRequest, "Unsupported lifecycle operation.", 400);
    }

    private static async Task HandleCorsAsync(
        HttpContext context,
        string bucketName,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.GetBucketCORS, bucketName, null, requireAuthenticated: false, cancellationToken);
            var cors = await store.GetBucketCorsAsync(bucketName, cancellationToken)
                ?? throw new MeansException(MeansErrorCodes.NoSuchCORSConfiguration, "CORS configuration does not exist.", 404);
            await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, cors.Xml, cancellationToken);
            return;
        }

        if (HttpMethods.IsPut(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.PutBucketCORS, bucketName, null, requireAuthenticated: true, cancellationToken);
            var xml = await S3RequestParser.ReadAndValidateXmlAsync(context.Request.Body, "CORSConfiguration", cancellationToken);
            await store.PutBucketCorsAsync(bucketName, new BucketCorsConfiguration(xml), cancellationToken);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        if (HttpMethods.IsDelete(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.PutBucketCORS, bucketName, null, requireAuthenticated: true, cancellationToken);
            await store.DeleteBucketCorsAsync(bucketName, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        throw new MeansException(MeansErrorCodes.InvalidRequest, "Unsupported CORS operation.", 400);
    }

    private static async Task HandleNotificationAsync(
        HttpContext context,
        string bucketName,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.GetBucketNotification, bucketName, null, requireAuthenticated: false, cancellationToken);
            var notification = await store.GetBucketNotificationAsync(bucketName, cancellationToken)
                ?? new BucketNotificationConfiguration("<NotificationConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\" />");
            await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, notification.Xml, cancellationToken);
            return;
        }

        if (HttpMethods.IsPut(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.PutBucketNotification, bucketName, null, requireAuthenticated: true, cancellationToken);
            var xml = await S3RequestParser.ReadAndValidateXmlAsync(context.Request.Body, "NotificationConfiguration", cancellationToken);
            await store.PutBucketNotificationAsync(bucketName, new BucketNotificationConfiguration(xml), cancellationToken);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        if (HttpMethods.IsDelete(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.PutBucketNotification, bucketName, null, requireAuthenticated: true, cancellationToken);
            await store.DeleteBucketNotificationAsync(bucketName, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        throw new MeansException(MeansErrorCodes.InvalidRequest, "Unsupported notification operation.", 400);
    }

    internal static async Task EnsureOwnerOnlyAclAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!await S3RequestParser.ParseAclRequestIsOwnerOnlyAsync(context, cancellationToken))
        {
            throw new MeansException(
                MeansErrorCodes.NotImplemented,
                "Only owner-only ACLs are supported. Use a bucket policy to grant anonymous access.",
                501);
        }
    }

    private static async Task EnsureBucketExistsAsync(IObjectStore store, string bucketName, CancellationToken cancellationToken)
    {
        if (await store.GetBucketAsync(bucketName, cancellationToken) is null)
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }
    }
}
