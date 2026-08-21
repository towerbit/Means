# S3 API 与兼容性

Means 实现 S3-compatible 数据面，目标是覆盖常用 bucket/object/multipart/versioning/lifecycle/tagging/cors/policy 能力，同时保持错误响应、XML 结构、SigV4 签名和分页语义尽量接近 S3。

## 地址风格

Means 支持三种入口：

| 风格 | 示例 | 说明 |
| --- | --- | --- |
| 同源 alias | `http://localhost:5178/s3/{bucket}/{key}` | 默认 `AliasPrefix=/s3`，适合单域名和 Console 浏览器上传 |
| Path-style | `https://api.means.local/{bucket}/{key}` | `Means:S3:ServiceHost` 指定 canonical host |
| Virtual-hosted-style | `https://{bucket}.means.local/{key}` | `Means:S3:DomainSuffix` 指定 bucket 子域后缀 |

本地反向代理或网关部署时，SDK 的 endpoint 必须指向真实 S3 数据面路径。例如服务在同域 `/s3` 下暴露时，endpoint 应配置为 `https://example.com/s3/`，而不是 Console 根路径。

## 认证模式

| 模式 | 适用场景 |
| --- | --- |
| SigV4 header | 服务端 SDK、AWS CLI、可信后端 |
| SigV4 query presign | 临时下载、临时上传、浏览器直传 |
| Anonymous policy | Bucket policy 显式允许的匿名读取或写入 |
| Console cookie | 仅 `/api/console` 管理面，不适用于 S3 数据面 |

SigV4 默认 region 为 `us-east-1`，service 为 `s3`。预签名 URL 最大有效期为 7 天。请求方法、path、query subresource 和 signed headers 必须与签名一致。

Means 不校验 credential scope 里的 region，但 `GetBucketLocation` 和 `x-amz-bucket-region` 会返回 `Means:S3:Region`（默认 `us-east-1`）。部分客户端会用这个值决定签名 region，因此它应与客户端配置保持一致。

## 支持矩阵

| 能力 | 状态 | 说明 |
| --- | --- | --- |
| ListBuckets | 支持 | `GET /`，需要签名 |
| CreateBucket | 支持 | `PUT /{bucket}` |
| HeadBucket | 支持 | `HEAD /{bucket}`，返回 `x-amz-bucket-region` |
| DeleteBucket | 支持 | 非空 bucket 返回 `BucketNotEmpty` |
| GetBucketLocation | 支持 | `GET /{bucket}?location`，region 为 `us-east-1` 时按 S3 惯例返回空 constraint |
| ListObjects (v1) | 支持 | 无 `list-type` 参数时生效；`prefix`、`delimiter`、`marker`、`max-keys`、`encoding-type` |
| ListObjectsV2 | 支持 | `prefix`、`delimiter`、`continuation-token`、`start-after`、`max-keys`、`encoding-type` |
| DeleteObjects | 支持 | `POST /{bucket}?delete`，单次最多 1000 个 key，支持 `Quiet` |
| Bucket / Object ACL | 有限支持 | `?acl` 只读表达 owner 与 policy 推导出的 public-read；写入仅接受 owner-only canned ACL |
| PutObject | 支持 | metadata、content type、cache-control、content-disposition |
| GetObject | 支持 | Range、压缩、versionId、response-* 响应头覆盖 |
| HeadObject | 支持 | metadata、versionId |
| DeleteObject | 支持 | versioning 下写 delete marker 或永久删除指定版本 |
| CopyObject | 支持 | `x-amz-copy-source`，`COPY`/`REPLACE` metadata directive |
| Conditional headers | 支持 | `If-Match`、`If-None-Match` 覆盖 GET/HEAD/PUT |
| Multipart Upload | 支持 | initiate/upload part/upload part copy/list/complete/abort |
| ListMultipartUploads | 支持 | marker、max、delimiter/common prefixes |
| ListParts | 支持 | part marker、max parts |
| Bucket Versioning | 支持 | `Enabled`、`Suspended`、未配置 |
| ListObjectVersions | 支持 | prefix、delimiter、key marker、version marker、max-keys |
| Object tagging | 支持 | current version 和指定 versionId |
| Bucket Lifecycle | 支持 | expiration、noncurrent expiration、abort incomplete multipart |
| Bucket CORS | 支持 | 配置 CRUD 与 OPTIONS preflight |
| Bucket notification | 预留 | 配置可持久化和读取，事件投递 worker 尚未实现 |
| Bucket Policy | 基础支持 | Allow/Deny、Principal、Action、Resource；condition 待补齐 |

未实现的 subresource（`?website`、`?logging`、`?replication`、`?encryption`、`?object-lock`、`?requestPayment`、object 的 `?torrent`、`?retention` 等）返回 `NotImplemented`，而不会退化成其他操作。这些请求同样需要签名，避免匿名探测。

### ACL 模型

Means 目前没有多用户 IAM，因此不存储 ACL：

- `GET ?acl` 返回单一部署 owner（ID/DisplayName 均为 `means`）的 `FULL_CONTROL` grant。如果 bucket policy 允许匿名读取，响应会额外包含 `AllUsers` 的 `READ` grant，使 ACL 与实际可访问性一致。
- `PUT ?acl` 与写请求上的 `x-amz-acl` 只接受 owner-only canned ACL（`private`、`bucket-owner-read`、`bucket-owner-full-control`）；其他值返回 `NotImplemented`，而不是静默忽略。
- 需要匿名访问时使用 bucket policy。

### 目录语义

Means 不维护目录树，`dir/` 只是一个普通 key：

- 以 `/` 结尾的零字节对象可以写入、`HEAD`、删除，FUSE 客户端用它保存目录的 POSIX mode。
- 带 `delimiter=/` 的列举会把 `dir/` 折叠成 `CommonPrefixes`；用 `prefix=dir/` 列举时，`dir/` 自身出现在 `Contents` 中。这与 S3 一致。
- 重命名是 copy + delete，客户端负责递归。

## 上传完整性

AWS SDK 默认用 SigV4 streaming 上传对象：请求体被切成 `<hex-size>;chunk-signature=<sig>` 帧，末尾附带 `x-amz-checksum-*` 等 trailer，`Content-Encoding` 为 `aws-chunked`，真实载荷长度放在 `x-amz-decoded-content-length`。

`PutObject` 和 `UploadPart` 在写入存储前处理这一层：

- 识别 `x-amz-content-sha256: STREAMING-*`（或 `aws-chunked` + `x-amz-decoded-content-length`）并剥离帧头与 trailer，因此对象内容不会包含 `chunk-signature` 等框架字节。
- 解码后的字节数必须等于 `x-amz-decoded-content-length`，否则返回 `IncompleteBody`。连接在帧边界断开时，框架看起来是完整的但载荷是截断的，没有这项检查会静默写入损坏对象。
- 校验客户端携带的完整性值，不匹配则拒绝写入：trailer 或请求头里的 `x-amz-checksum-crc32`、`x-amz-checksum-crc32c`、`x-amz-checksum-crc64nvme`、`x-amz-checksum-sha1`、`x-amz-checksum-sha256`，以及 `Content-MD5`。
- 响应回显协商到的 `x-amz-checksum-*`，让 SDK 侧的上传校验闭环。

Means 不识别的 checksum 算法会被忽略而不是拒绝，这样更新的客户端仍能上传，只是少了服务端校验。存储的 checksum 目前不会在 `GetObject`/`HeadObject` 上回显，因此 `x-amz-checksum-mode: ENABLED` 的读取不做校验。

## Multipart 规则

- `partNumber` 范围：`1..10000`。
- 非最终 part 至少 `5 MiB`。
- SDK 默认 part size：`16 MiB`。
- `CompleteMultipartUpload` 要求 part 编号升序且 ETag 匹配。
- Means 不模拟 AWS `CompleteMultipartUpload` 的 `200 OK` 内嵌错误边缘行为；失败直接返回 4xx XML 错误。

## 错误模型

S3 数据面统一返回 XML 错误：

```xml
<Error>
  <Code>NoSuchKey</Code>
  <Message>...</Message>
  <Resource>/bucket/key</Resource>
  <RequestId>...</RequestId>
</Error>
```

常见错误：

| Code | HTTP | 场景 |
| --- | --- | --- |
| `AccessDenied` | 403 | 未认证、policy 不允许、presign 过期或参数不完整 |
| `SignatureDoesNotMatch` | 403 | 方法、path、query、host、signed headers 或 secret 不匹配 |
| `NoSuchBucket` | 404 | bucket 不存在 |
| `NoSuchKey` | 404 | object 不存在 |
| `NoSuchVersion` | 404 | versionId 不存在 |
| `BucketAlreadyExists` | 409 | bucket 已存在 |
| `BucketNotEmpty` | 409 | 删除非空 bucket |
| `InvalidArgument` | 400 | bucket/key 命名、range、参数非法 |
| `IncompleteBody` | 400 | 上传字节数与 `x-amz-decoded-content-length` 不一致，或 `aws-chunked` 请求体被截断 |
| `XAmzContentChecksumMismatch` | 400 | `x-amz-checksum-*`（trailer 或请求头）与实际载荷不匹配 |
| `BadDigest` | 400 | `Content-MD5` 与实际载荷不匹配 |
| `InvalidDigest` | 400 | `Content-MD5` 不是合法的 base64 128-bit digest |
| `InvalidPart` | 400 | multipart complete 的 part 或 ETag 不匹配 |
| `InvalidPartOrder` | 400 | multipart complete part 顺序错误 |
| `EntityTooSmall` | 400 | 非最终 multipart part 小于 5 MiB |
| `EntityTooLarge` | 413 | 请求体超过配置限制 |
| `MalformedXML` | 400 | 请求 XML 结构非法，例如 `Delete` 缺少 `Object` 或超过 1000 条 |
| `NotImplemented` | 501 | 未实现的 subresource，或不支持的 ACL grant |
| `SlowDown` | 503 | 上传并发或 API 固定窗口限流 |

Console API 错误为 JSON，不使用 XML。

## Bucket policy 支持范围

支持的 action 包括：

- `s3:ListBucket`
- `s3:GetObject`
- `s3:PutObject`
- `s3:DeleteObject`
- `s3:GetObjectTagging`
- `s3:PutObjectTagging`
- `s3:DeleteObjectTagging`
- `s3:GetBucketLocation`
- `s3:GetBucketAcl`
- `s3:PutBucketAcl`
- `s3:GetObjectAcl`
- `s3:PutObjectAcl`
- `s3:GetBucketCORS`
- `s3:PutBucketCORS`
- `s3:GetBucketNotification`
- `s3:PutBucketNotification`
- `s3:AbortMultipartUpload`
- `s3:ListMultipartUploadParts`

`DeleteObjects` 按每个 key 单独授权：被拒绝的 key 以 `<Error>` 条目返回，其余 key 正常删除，与 S3 的部分失败语义一致。

当前支持 `Allow`/`Deny`、`Principal` 和 `Resource` 匹配；condition、IAM role、STS session、tenant namespace 是后续能力。

## 客户端兼容性目标

| 客户端 | 目标验证范围 |
| --- | --- |
| AWS CLI | bucket/object/multipart/versioning/tagging/cors/lifecycle 基础命令 |
| boto3 | 同步 S3 client API smoke tests |
| aws-sdk-js v3 | S3Client command smoke tests |
| rclone | copy、sync、ls、cat、delete |
| MinIO Client (`mc`) | mb、cp、ls、cat、rm、stat |
| s3fs-fuse | mount、ls、read/write、mkdir/rmdir、mv、chmod |

辅助脚本位于 `scripts/compatibility/run-s3-client-matrix.ps1`。

### s3fs-fuse 挂载

s3fs 默认发送 ListObjects v1 并在启动时读取 bucket region，两者现在都已支持，因此不需要额外开关：

```bash
echo "ACCESS_KEY:SECRET_KEY" > ~/.passwd-s3fs
chmod 600 ~/.passwd-s3fs

s3fs my-bucket /mnt/means \
  -o passwd_file=~/.passwd-s3fs \
  -o url=https://api.means.local \
  -o use_path_request_style \
  -o endpoint=us-east-1
```

要点：

- `use_path_request_style` 用于未配置 bucket 子域的部署；使用 virtual-hosted-style 时可以省略。
- `endpoint` 必须与 `Means:S3:Region` 一致，否则 s3fs 会因签名 region 不匹配而重试失败。
- 同源 alias 部署时 `url` 需要包含 alias，例如 `https://example.com/s3`。
- `-o listobjectsv2` 可选：v1 与 v2 均受支持。
- s3fs 依赖 `x-amz-meta-*` 保存 mode/uid/gid/mtime，并用 CopyObject 更新它们；这些请求会重写对象元数据，不会保留旧版本以外的 ACL 信息。
