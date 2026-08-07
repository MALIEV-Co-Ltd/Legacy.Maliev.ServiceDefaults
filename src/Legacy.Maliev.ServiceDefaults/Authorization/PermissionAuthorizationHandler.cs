using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace Maliev.Aspire.ServiceDefaults.Authorization;

/// <summary>
/// Authorization handler that validates permissions using either live IAM service checks or JWT claims.
/// Falls back to JWT claims-based matching when the IAM service is unavailable.
/// </summary>
public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IIamServiceClient? _iamClient;
    private readonly ILogger<PermissionAuthorizationHandler> _logger;
    private readonly IAuthMetrics? _authMetrics;

    /// <summary>
    /// Per-requirement semaphores to serialize concurrent IAM calls for the same permission.
    /// Entries are reference-counted and removed as soon as the last waiter leaves, so a
    /// high-cardinality principal/resource stream cannot permanently grow this registry.
    /// </summary>
    private static readonly object _permissionSemaphoreRegistryLock = new();
    private static readonly Dictionary<string, PermissionSemaphoreEntry> _permissionSemaphores = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the PermissionAuthorizationHandler with the specified dependencies.
    /// </summary>
    /// <param name="serviceProvider">The service provider for resolving additional services.</param>
    /// <param name="httpContextAccessor">Provides access to the current HTTP context.</param>
    /// <param name="logger">Logger for authorization events and debugging.</param>
    /// <param name="iamClient">Optional IAM service client for live permission checks.</param>
    /// <param name="authMetrics">Optional metrics recorder for authorization success/failure tracking.</param>
    public PermissionAuthorizationHandler(
        IServiceProvider serviceProvider,
        IHttpContextAccessor httpContextAccessor,
        ILogger<PermissionAuthorizationHandler> logger,
        IIamServiceClient? iamClient = null,
        IAuthMetrics? authMetrics = null)
    {
        _serviceProvider = serviceProvider;
        _httpContextAccessor = httpContextAccessor;
        _iamClient = iamClient;
        _logger = logger;
        _authMetrics = authMetrics;
    }

    /// <summary>
    /// Handles the authorization requirement evaluation by checking permissions via IAM service or JWT claims.
    /// </summary>
    /// <param name="context">The authorization context containing the user and resource.</param>
    /// <param name="requirement">The permission requirement to evaluate.</param>
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var requestCancellation = httpContext?.RequestAborted ?? CancellationToken.None;
        requestCancellation.ThrowIfCancellationRequested();

        var principalId = context.User.FindFirst("user_id")?.Value
            ?? context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        // Resolve resource path from template if resource-scoped auth is enabled
        var resourcePath = "global";
        if (!string.IsNullOrEmpty(requirement.ResourcePathTemplate))
        {
            var config = _serviceProvider.GetService<IConfiguration>();
            var resourceScopedEnabled = config?.GetValue<bool>("Features:ResourceScopedAuthEnabled", false) ?? false;
            if (resourceScopedEnabled)
            {
                resourcePath = ResolveResourcePath(requirement.ResourcePathTemplate, httpContext?.GetRouteData().Values ?? new RouteValueDictionary());
                if (resourcePath.Contains('{', StringComparison.Ordinal) || resourcePath.Contains('}', StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Resource path template {ResourcePathTemplate} contains unresolved route values",
                        requirement.ResourcePathTemplate);
                    return;
                }
            }
        }
        var enforcementMode = requirement.RequireLiveCheck ? "live" : "standard";
        var cacheKey = $"perm:{principalId}:{requirement.Permission}:{resourcePath}:{enforcementMode}";

        if (httpContext != null && !string.IsNullOrEmpty(principalId))
        {
            // Short-circuit if this permission was already evaluated for this request
            // (prevents double-check when both UseAuthorization() middleware and the
            // MVC AuthorizeFilter both run the same policy on the same request).
            if (httpContext.Items.TryGetValue(cacheKey, out var cached))
            {
                if (cached is true) context.Succeed(requirement);
                return;
            }
        }

        // Serialize concurrent calls for the same (principal, permission, resource) so that
        // only one IAM HTTP request is made and subsequent callers reuse the result.
        var semKey = $"{principalId}:{requirement.Permission}:{resourcePath}:{enforcementMode}";
        var semaphoreLease = AcquirePermissionSemaphore(semKey);
        var semaphoreAcquired = false;

        try
        {
            await semaphoreLease.Semaphore.WaitAsync(requestCancellation);
            semaphoreAcquired = true;
            requestCancellation.ThrowIfCancellationRequested();

            // Re-check cache after acquiring semaphore — another caller may have populated it
            // while we were waiting.
            if (httpContext != null && !string.IsNullOrEmpty(principalId))
            {
                if (httpContext.Items.TryGetValue(cacheKey, out var cached))
                {
                    if (cached is true) context.Succeed(requirement);
                    return;
                }
            }

            if (await CheckRequirementAsync(
                context,
                requirement.Permission,
                resourcePath,
                requirement.RequireLiveCheck,
                requirement.AuditPurpose,
                requestCancellation))
            {
                context.Succeed(requirement);
            }
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            requestCancellation.ThrowIfCancellationRequested();
            _logger.LogError(ex, "Unhandled exception in PermissionAuthorizationHandler");
        }
        finally
        {
            if (semaphoreAcquired)
            {
                semaphoreLease.Semaphore.Release();
            }

            ReleasePermissionSemaphore(semKey, semaphoreLease.Entry);
        }
    }

    private static SemaphoreLease AcquirePermissionSemaphore(string key)
    {
        lock (_permissionSemaphoreRegistryLock)
        {
            if (!_permissionSemaphores.TryGetValue(key, out var entry))
            {
                entry = new PermissionSemaphoreEntry();
                _permissionSemaphores.Add(key, entry);
            }

            entry.ReferenceCount++;
            return new SemaphoreLease(entry);
        }
    }

    private static void ReleasePermissionSemaphore(string key, PermissionSemaphoreEntry entry)
    {
        lock (_permissionSemaphoreRegistryLock)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0 &&
                _permissionSemaphores.Remove(key, out var removed) &&
                ReferenceEquals(removed, entry))
            {
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class PermissionSemaphoreEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }
    }

    private readonly record struct SemaphoreLease(PermissionSemaphoreEntry Entry)
    {
        public SemaphoreSlim Semaphore => Entry.Semaphore;
    }

    private async Task<bool> CheckRequirementAsync(
        AuthorizationHandlerContext context,
        string permission,
        string resourcePath,
        bool requireLiveCheck,
        string? auditPurpose,
        CancellationToken requestCancellation)
    {
        requestCancellation.ThrowIfCancellationRequested();

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null) return false;

        var user = context.User;
        if (user?.Identity?.IsAuthenticated != true) return false;

        var principalId = user.FindFirst("user_id")?.Value
            ?? user.FindFirst("sub")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(principalId)) return false;

        _logger.LogDebug("Checking permission {Permission} for Principal {PrincipalId} on Resource {ResourcePath}",
            permission, principalId, resourcePath);

        bool hasPermission = false;

        // Try IAM live check first if client is available
        if (_iamClient != null)
        {
            try
            {
                hasPermission = requireLiveCheck
                    ? await _iamClient.CheckPermissionLiveAsync(
                        principalId,
                        permission,
                        resourcePath,
                        requestCancellation)
                    : await _iamClient.CheckPermissionAsync(
                        principalId,
                        permission,
                        resourcePath,
                        requestCancellation);
                requestCancellation.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                requestCancellation.ThrowIfCancellationRequested();
                _logger.LogError(ex, "IAM service call failed for permission {Permission}", permission);
                var config = _serviceProvider.GetService<IConfiguration>();
                if (IsFailOpenOnIamErrorAllowed(config))
                {
                    hasPermission = true;
                }
                else if (config?.GetValue<bool>("Features:FailOpenOnIAMError", false) ?? false)
                {
                    _logger.LogWarning(
                        "Ignoring Features:FailOpenOnIAMError outside Development or Testing for permission {Permission}",
                        permission);
                }
            }
        }

        requestCancellation.ThrowIfCancellationRequested();

        // Exact-snapshot Aspire has no IAM runtime by design. It may opt in to
        // proving server-to-server workflows with the exact scoped permissions
        // already signed into a service JWT. This remains disabled by default,
        // accepts only exact permission claims, and never applies to end-user subjects.
        if (!hasPermission && requireLiveCheck && IsExactLocalServiceGrant(user, principalId, permission))
        {
            hasPermission = true;
            _logger.LogInformation(
                "Local snapshot accepted exact service-token permission {Permission} for Principal {PrincipalId}",
                permission,
                principalId);
        }

        // Standard checks may fall back to token claims. Forced-live checks fail closed
        // when IAM is unavailable or denies the permission.
        if (!hasPermission && !requireLiveCheck)
        {
            var userPermissions = user.Claims
                .Where(c => c.Type == "permissions" || c.Type == "permission" || c.Type == "role" || c.Type == "roles" || c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role")
                .Select(c => c.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // A standalone wildcard is reserved for the short-lived service token emitted by
            // ServiceAccountTokenProvider. End-user tokens must carry exact or suffix-scoped
            // permissions; otherwise a stale/forged wildcard claim would bypass IAM during an
            // outage. The JWT signature is validated before this handler runs, and the marker
            // claims below are emitted together by the service-token provider.
            if (userPermissions.Contains("*") && IsTrustedServiceWildcard(user, principalId))
            {
                _logger.LogDebug("Trusted service JWT contains wildcard permission - granting access for {Permission}", permission);
                hasPermission = true;
            }

            _logger.LogInformation("IAM check failed, falling back to claims. Found {Count} permission claims for Principal {PrincipalId}",
                userPermissions.Count, principalId);

            // Never pass the standalone wildcard into the generic matcher. The matcher keeps
            // its historical semantics for callers that use it directly, while this handler
            // applies the stronger principal-type boundary for request authorization.
            if (!hasPermission && PermissionMatcher.Match(
                    permission,
                    userPermissions.Where(p => !string.Equals(p, "*", StringComparison.Ordinal))))
            {
                hasPermission = true;
            }
        }
        else if (!hasPermission)
        {
            _logger.LogWarning(
                "Live IAM check did not grant permission {Permission} for Principal {PrincipalId}",
                permission,
                principalId);
        }

        requestCancellation.ThrowIfCancellationRequested();

        // Cache result for the remainder of this request to prevent double evaluation.
        // Use the same cache key format as HandleRequirementAsync for consistency.
        var enforcementMode = requireLiveCheck ? "live" : "standard";
        var cacheKey = $"perm:{principalId}:{permission}:{resourcePath}:{enforcementMode}";
        httpContext.Items[cacheKey] = hasPermission;

        if (hasPermission)
        {
            if (!string.IsNullOrEmpty(auditPurpose))
            {
                httpContext.Request.Headers["X-Internal-Audit-Purpose"] = auditPurpose;
            }

            _authMetrics?.RecordSuccess(permission);
            return true;
        }

        _authMetrics?.RecordFailure(permission, "Permission denied");
        return false;
    }

    private bool IsExactLocalServiceGrant(ClaimsPrincipal user, string principalId, string permission)
    {
        var configuration = _serviceProvider.GetService<IConfiguration>();
        if (!(configuration?.GetValue<bool>("Features:AllowExactServiceClaimsForLiveCheck", false) ?? false) ||
            !principalId.StartsWith("service:", StringComparison.Ordinal))
        {
            return false;
        }

        return user.Claims.Any(claim =>
            (claim.Type == "permissions" || claim.Type == "permission") &&
            string.Equals(claim.Value, permission, StringComparison.Ordinal));
    }

    private bool IsTrustedServiceWildcard(ClaimsPrincipal user, string principalId) =>
        principalId.StartsWith("system:service:", StringComparison.OrdinalIgnoreCase) &&
        HasClaimValue(user, "user_type", "service") &&
        HasClaimValue(user, "role", "service-account") &&
        HasClaimValue(user, "purpose", "iam-registration");

    private bool IsFailOpenOnIamErrorAllowed(IConfiguration? configuration)
    {
        if (configuration?.GetValue<bool>("Features:FailOpenOnIAMError", false) != true)
        {
            return false;
        }

        var environmentName = _serviceProvider.GetService<IHostEnvironment>()?.EnvironmentName
            ?? configuration["ASPNETCORE_ENVIRONMENT"]
            ?? configuration["DOTNET_ENVIRONMENT"];

        return string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase)
            || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasClaimValue(ClaimsPrincipal user, string claimType, string expectedValue) =>
        user.Claims.Any(claim =>
            string.Equals(claim.Type, claimType, StringComparison.Ordinal) &&
            string.Equals(claim.Value, expectedValue, StringComparison.OrdinalIgnoreCase));

    private string ResolveResourcePath(string template, RouteValueDictionary routeValues)
    {
        var path = template;
        foreach (var rv in routeValues)
        {
            path = path.Replace($"{{{rv.Key}}}", rv.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        return path;
    }
}
