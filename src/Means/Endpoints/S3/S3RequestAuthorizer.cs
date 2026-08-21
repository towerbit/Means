using Means.Core;
using Means.Protocol.S3;

namespace Means.Endpoints.S3;

/// <summary>
/// Coordinates SigV4 authentication and policy authorization for one S3 request.
/// Order: SigV4 → access-key policy → bucket policy.
/// One instance serves a single request, so the signature is verified once even when an operation
/// authorizes many keys (for example DeleteObjects).
/// </summary>
internal sealed class S3RequestAuthorizer(
    IAccessKeyStore accessKeys,
    IBucketPolicyRepository policies,
    BucketPolicyEvaluator policyEvaluator,
    SigV4RequestVerifier verifier)
{
    private SigV4AuthResult? _auth;
    private AccessKeyCredential? _credential;
    private bool _credentialResolved;

    /// <summary>
    /// Authorizes one action and returns the authenticated access key, or null when anonymous.
    /// </summary>
    public async Task<string?> AuthorizeAsync(
        HttpContext context,
        string action,
        string? bucketName,
        string? key,
        bool requireAuthenticated,
        CancellationToken cancellationToken)
    {
        var auth = await VerifyAsync(context, cancellationToken);
        if (auth.IsSigned && !auth.IsAuthenticated)
        {
            throw new MeansException(auth.ErrorCode ?? MeansErrorCodes.AccessDenied, auth.ErrorMessage ?? "Access denied.", 403);
        }

        if (auth.IsAuthenticated && !string.IsNullOrEmpty(auth.AccessKey))
        {
            var credential = await ResolveCredentialAsync(auth.AccessKey, cancellationToken);
            if (!string.IsNullOrWhiteSpace(credential?.PolicyJson))
            {
                var accessKeyDecision = policyEvaluator.Evaluate(
                    credential.PolicyJson,
                    action,
                    bucketName,
                    key,
                    auth.AccessKey,
                    PolicyPrincipalMode.AccessKey);
                if (accessKeyDecision != PolicyDecision.Allow)
                {
                    throw new MeansException(MeansErrorCodes.AccessDenied, "Access denied by access key policy.", 403);
                }
            }
        }

        if (bucketName is null)
        {
            if (!auth.IsAuthenticated)
            {
                throw new MeansException(MeansErrorCodes.AccessDenied, "Authentication is required.", 403);
            }

            return auth.AccessKey;
        }

        var policy = await policies.GetPolicyAsync(bucketName, cancellationToken);
        var decision = policyEvaluator.Evaluate(policy, action, bucketName, key, auth.AccessKey);
        if (decision == PolicyDecision.Deny)
        {
            throw new MeansException(MeansErrorCodes.AccessDenied, "Access denied by bucket policy.", 403);
        }

        if (!auth.IsAuthenticated && (requireAuthenticated || decision != PolicyDecision.Allow))
        {
            throw new MeansException(MeansErrorCodes.AccessDenied, "Authentication is required.", 403);
        }

        return auth.AccessKey;
    }

    public async Task RequireAuthenticatedAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var auth = await VerifyAsync(context, cancellationToken);
        if (!auth.IsAuthenticated)
        {
            throw new MeansException(auth.ErrorCode ?? MeansErrorCodes.AccessDenied, auth.ErrorMessage ?? "Authentication is required.", 403);
        }
    }

    /// <summary>
    /// Reports whether the bucket policy grants the action to anonymous callers.
    /// ACL responses use this so a reported public-read grant reflects real access.
    /// </summary>
    public async Task<bool> IsAnonymousAllowedAsync(
        string action,
        string bucketName,
        string? key,
        CancellationToken cancellationToken)
    {
        var policy = await policies.GetPolicyAsync(bucketName, cancellationToken);
        return policyEvaluator.Evaluate(policy, action, bucketName, key, null) == PolicyDecision.Allow;
    }

    private async Task<SigV4AuthResult> VerifyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        return _auth ??= await verifier.VerifyAsync(context.Request, accessKeys.GetCredentialAsync, cancellationToken);
    }

    private async Task<AccessKeyCredential?> ResolveCredentialAsync(string accessKey, CancellationToken cancellationToken)
    {
        if (!_credentialResolved)
        {
            _credential = await accessKeys.GetCredentialAsync(accessKey, cancellationToken);
            _credentialResolved = true;
        }

        return _credential;
    }
}
