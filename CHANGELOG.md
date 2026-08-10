# Changelog

## Unreleased

- Fixed Docker Compose bootstrap credential handling: configured `DefaultSecretKey` is reapplied on startup without wiping volumes, Compose env interpolation guidance was clarified, and the Console can disable/delete the bootstrap AccessKey after another enabled key exists.
- Fixed AWS S3 SDK `PutObject`/`UploadPart` compatibility: SigV4 `aws-chunked` streaming uploads (with `chunk-signature` frames and trailers) are decoded before storage so object content no longer includes framing bytes.
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
