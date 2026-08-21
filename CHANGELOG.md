# Changelog

## Unreleased

- Added S3 `ListObjects` (v1) so clients that do not send `list-type=2`, including s3fs-fuse and older SDKs, can list buckets; truncated pages return `NextMarker`, and a marker naming a `CommonPrefix` resumes after that whole prefix instead of repeating it.
- Added `GetBucketLocation`, the `x-amz-bucket-region` response header, and the `Means:S3:Region` setting so clients can discover the signing region.
- Added `DeleteObjects` (`POST /{bucket}?delete`) with per-key authorization, so a denied key is reported as an entry-level error while the remaining keys are deleted.
- Added bucket and object `?acl` support: reads report the deployment owner plus a public-read grant derived from bucket policy, and writes accept only owner-only canned ACLs. Non-owner grants on `?acl`, `PutObject`, `CopyObject`, and multipart initiate now return `NotImplemented` instead of being silently dropped.
- Added `start-after` to `ListObjectsV2`, `encoding-type=url` to both listing generations, and fixed `MaxKeys` to echo the requested limit rather than the number of returned keys.
- S3 subresources that Means does not implement (`?website`, `?logging`, `?replication`, `?torrent`, and similar) now return `NotImplemented` for authenticated callers instead of falling through to an unrelated operation.
- Fixed Docker Compose bootstrap credential handling: configured `DefaultSecretKey` is reapplied on startup without wiping volumes, Compose env interpolation guidance was clarified, and the Console can disable/delete the bootstrap AccessKey after another enabled key exists.
- Fixed AWS S3 SDK `PutObject`/`UploadPart` compatibility: SigV4 `aws-chunked` streaming uploads (with `chunk-signature` frames and trailers) are decoded before storage so object content no longer includes framing bytes.
- Fixed silent corruption of `aws-chunked` uploads: the decoded payload size must now match `x-amz-decoded-content-length`, so a body truncated on a frame boundary fails with `IncompleteBody` instead of being stored short.
- Added upload integrity verification for `PutObject`/`UploadPart`: `x-amz-checksum-crc32`, `x-amz-checksum-crc32c`, `x-amz-checksum-crc64nvme`, `x-amz-checksum-sha1`, `x-amz-checksum-sha256` (sent as `aws-chunked` trailers or request headers) and `Content-MD5` are validated against the payload, and the negotiated checksum is echoed on the response.
- Fixed a startup failure introduced with the access-key enable/disable endpoint: `SetAccessKeyStatusRequest` was missing from the source-generated JSON context, so the host threw `NoMetadataForType` while building endpoints.
- Fixed AWS S3 SDK `PutBucket` against path-style endpoints: trailing-slash paths such as `PUT /bucket/` and `PUT /s3//bucket/` are now treated as bucket operations instead of empty object keys (`Invalid bucket name.`).
- Added access-key-level IAM-style policy support for S3 authorization, Console management APIs, and console UI.
- Documented access-key policy evaluation order and Principal optional semantics in the SDK contract.

## 0.1.2 - 2026-06-05

- Bumped the C# and TypeScript SDK package versions to `0.1.2`.
- Added cluster diagnostics, metrics, and console UI updates.

## 0.1.1 - 2026-06-05

- Bumped the C# and TypeScript SDK package versions to `0.1.1`.
- Added cluster shard transport and internal cluster endpoint support.
- Added C# and TypeScript SDK examples for S3 compatibility checks.
- Expanded docs for cluster topology, configuration, troubleshooting, and SDK usage.
